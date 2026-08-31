// Copyright (c) AeroCode
// SchedulerService — 自动化调度（批次 B G4，builder-γ）：jobs.json 持久化 + Timer 触发 + ESTOP 哨兵联动。
// 零 mock：任务命令以真实子进程执行；jobs.json 真实落盘（原子写）；
// 时区语义显式：cron 按本地时间求值（用户直觉），一次性 AtUtc 显式存 UTC（跨时区/夏令时不漂移）。
// ESTOP：哨兵文件存在 → 触发前拦截（任务不消耗、下轮恢复），并发布 EtopTrippedEvent。
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AeroCode.Harness.EventBus;

namespace AeroCode.Harness.Scheduler;

/// <summary>
/// 一条调度任务。Cron 与 AtUtc 二选一（恰好一个非空，构造后不可变）：
/// - Cron：5 字段 cron 表达式（分 时 日 月 周），按<b>本地时间</b>求值（用户直觉）；
/// - AtUtc：一次性触发时刻，<b>显式 UTC</b> 存储（jobs.json 中带 Z 后缀）。
/// MissionPrompt 供未来 Mission 控制器（批次 B2/G2）接线：随 JobFired 事件透出，本服务只负责触发与真实执行。
/// </summary>
public sealed class JobDef
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("cron")]
    public string? Cron { get; set; }

    /// <summary>一次性触发时刻（UTC）。反序列化时无时区后缀者按 UTC 归一（本服务只产 UTC）。</summary>
    [JsonPropertyName("atUtc")]
    public DateTime? AtUtc { get; set; }

    [JsonPropertyName("command")]
    public string Command { get; set; } = string.Empty;

    [JsonPropertyName("missionPrompt")]
    public string? MissionPrompt { get; set; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("timeoutSec")]
    public int TimeoutSec { get; set; } = 120;

    /// <summary>校验并归一（AtUtc 无时区按 UTC）：不合法抛 ArgumentException。</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id))
        {
            throw new ArgumentException("job id must not be empty", nameof(Id));
        }

        if (string.IsNullOrWhiteSpace(Command))
        {
            throw new ArgumentException($"job '{Id}' command must not be empty", nameof(Id));
        }

        var hasCron = !string.IsNullOrWhiteSpace(Cron);
        if (hasCron == AtUtc.HasValue)
        {
            throw new ArgumentException($"job '{Id}' must specify exactly one of cron/atUtc", nameof(Id));
        }

        if (hasCron)
        {
            try
            {
                CronSchedule.Parse(Cron!);
            }
            catch (FormatException ex)
            {
                throw new ArgumentException($"job '{Id}' has invalid cron expression: {ex.Message}", nameof(Cron), ex);
            }
        }

        if (AtUtc.HasValue && AtUtc.Value.Kind != DateTimeKind.Utc)
        {
            AtUtc = DateTime.SpecifyKind(AtUtc.Value, DateTimeKind.Utc);
        }

        if (TimeoutSec <= 0)
        {
            throw new ArgumentException($"job '{Id}' timeoutSec must be positive", nameof(Id));
        }
    }
}

/// <summary>一次任务真实执行的结果（输出已按 50KB/流 截断）。</summary>
public sealed record JobRunResult(
    string JobId,
    string Command,
    bool Success,
    bool TimedOut,
    int ExitCode,
    string StdOut,
    string StdErr,
    long ElapsedMs,
    DateTime StartedUtc);

/// <summary>
/// 调度服务。实现约束：
/// 1. jobs.json 持久化（AddOrUpdate/Remove/一次性到期自动停用都立即落盘，临时文件 + 原子替换）；
/// 2. Timer 触发：默认 30s 轮询（cron 最小粒度 1 分钟），RunDueJobsOnce 为可测核心（注入 nowUtc）；
/// 3. ESTOP 哨兵（路径构造注入）存在 → 触发前拦截：任务不消耗、不执行，并在"未拦截→拦截"
///    的转换上发布 EtopTrippedEvent（急停期间不重复刷事件）；哨兵移除后下轮自动恢复——急停不销毁计划；
/// 4. jobs.json 损坏 → fail-safe 空载启动并记录 LastLoadError（不崩溃、不伪造）；
/// 5. 命令执行：真实子进程（cmd /d /s /c），超时杀整棵树，stdout/stderr 各 50KB 截断。
/// </summary>
public sealed class SchedulerService : IDisposable
{
    /// <summary>stdout/stderr 各自的硬上限字符数（与 ShellRunner/HookEngine 同策略）。</summary>
    public const int MaxCharsPerStream = 50_000;

    private static readonly JsonSerializerOptions WriteOpts = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions ReadOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private readonly string _jobsFilePath;
    private readonly string? _estopSentinelPath;
    private readonly EventBus.EventBus? _bus;
    private readonly Action<string>? _log;
    private readonly object _lock = new();
    private readonly Dictionary<string, JobDef> _jobs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _lastFiredMinuteLocal = new(StringComparer.Ordinal);
    private readonly HashSet<string> _running = new(StringComparer.Ordinal);
    private Timer? _timer;
    private int _ticking;
    private bool _lastRoundWasEstopBlocked;
    private bool _disposed;

    public SchedulerService(string jobsFilePath, string? estopSentinelPath = null, EventBus.EventBus? bus = null, Action<string>? log = null)
    {
        _jobsFilePath = string.IsNullOrWhiteSpace(jobsFilePath)
            ? throw new ArgumentException("jobs file path must not be empty", nameof(jobsFilePath))
            : jobsFilePath;
        _estopSentinelPath = string.IsNullOrWhiteSpace(estopSentinelPath) ? null : estopSentinelPath;
        _bus = bus;
        _log = log;
    }

    /// <summary>Timer 轮询间隔秒数（Start 前可调；最小 1s。cron 最小粒度 1 分钟）。</summary>
    public int PollSeconds { get; set; } = 30;

    /// <summary>最近一次 Load 的失败原因（fail-safe 空载时非 null，供 UI/诊断诚实展示）。</summary>
    public string? LastLoadError { get; private set; }

    /// <summary>当前任务快照（按 Id 排序）。</summary>
    public IReadOnlyList<JobDef> Jobs
    {
        get
        {
            lock (_lock)
            {
                return _jobs.Values.OrderBy(j => j.Id, StringComparer.Ordinal).ToList();
            }
        }
    }

    /// <summary>任务真实触发后的回调（含 JobDef.MissionPrompt，供 Mission 接线）。</summary>
    public event Action<JobDef, JobRunResult>? JobFired;

    /// <summary>任务因 ESTOP 哨兵被拦截时回调（每轮至多一次聚合通知见 EtopTrippedEvent）。</summary>
    public event Action<JobDef>? EstopBlocked;

    /// <summary>从 jobs.json 加载（Start 自动调用；也可显式调用）。损坏 → 空载 + LastLoadError，不抛。</summary>
    public void Load()
    {
        lock (_lock)
        {
            _jobs.Clear();
            LastLoadError = null;

            if (!File.Exists(_jobsFilePath))
            {
                return; // 首次使用：空载是正常态
            }

            try
            {
                var json = File.ReadAllText(_jobsFilePath);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return;
                }

                var envelope = JsonSerializer.Deserialize<JobsEnvelope>(json, ReadOpts)
                               ?? throw new InvalidDataException("deserialized to null");
                foreach (var job in envelope.Jobs)
                {
                    job.Validate();
                    _jobs[job.Id] = job;
                }
            }
            catch (Exception ex)
            {
                _jobs.Clear();
                LastLoadError = ex.Message;
                _log?.Invoke($"[DEGRADED] SchedulerService: jobs.json rejected (fail-safe, starting empty): {ex.Message}");
            }
        }
    }

    /// <summary>新增或更新任务并立即持久化。不合法（缺 id/command、cron 与 atUtc 同时存在或都不存在等）抛 ArgumentException。</summary>
    public void AddOrUpdate(JobDef job)
    {
        ArgumentNullException.ThrowIfNull(job);
        job.Validate();

        lock (_lock)
        {
            _jobs[job.Id] = job;
            PersistNoLock();
        }
    }

    /// <summary>移除任务并立即持久化。返回是否确有移除。</summary>
    public bool Remove(string id)
    {
        lock (_lock)
        {
            if (!_jobs.Remove(id))
            {
                return false;
            }

            PersistNoLock();
            return true;
        }
    }

    /// <summary>启动轮询 Timer（自动 Load 一次）。</summary>
    public void Start()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_timer is not null)
            {
                return;
            }

            var interval = TimeSpan.FromSeconds(Math.Max(1, PollSeconds));
            _timer = new Timer(static s => ((SchedulerService)s!).Tick(), this, interval, interval);
        }

        Load();
    }

    public void Stop()
    {
        lock (_lock)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }
    }

    /// <summary>
    /// 可测核心：按给定时刻评估到期任务并<b>同步真实执行</b>，返回实际触发的任务数。
    /// - cron：按 nowUtc 的本地分钟求值；同一分钟内不重复触发（进程内去重）；
    /// - 一次性：AtUtc ≤ now → 触发一次并立即停用落盘（重启不复燃）；
    /// - ESTOP：哨兵存在 → 全部到期任务拦截（不消耗、不执行），发布 EtopTrippedEvent，返回拦截数。
    /// </summary>
    public int RunDueJobsOnce(DateTimeOffset nowUtc)
    {
        List<JobDef> due;
        var estopTripped = false;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            estopTripped = _estopSentinelPath is not null && File.Exists(_estopSentinelPath);
            // ESTOP：只收集不消耗（consume=false）——哨兵移除后同分钟/同到期时刻仍可触发。
            due = CollectDueNoLock(nowUtc, consume: !estopTripped);
            if (estopTripped && due.Count > 0 && !_lastRoundWasEstopBlocked)
            {
                // 只在"未拦截 → 拦截"的转换上发布一次（急停期间不重复刷事件）；
                // 未被拦截的轮次复位标志，下次急停再次发布。
                _bus?.Publish(new EtopTrippedEvent($"scheduler blocked by sentinel file: {_estopSentinelPath}", DateTime.UtcNow));
            }

            _lastRoundWasEstopBlocked = estopTripped && due.Count > 0;
        }

        if (estopTripped)
        {
            foreach (var job in due)
            {
                EstopBlocked?.Invoke(job);
            }

            return 0; // 拦截：不执行、不消耗
        }

        var fired = 0;
        foreach (var job in due)
        {
            lock (_lock)
            {
                if (!_running.Add(job.Id))
                {
                    continue; // 同任务不并发重入
                }
            }

            try
            {
                var result = Execute(job);
                fired++;
                JobFired?.Invoke(job, result);
            }
            catch (Exception ex)
            {
                // 执行器自身异常：如实记录为失败结果，不让一条坏任务拖垮调度循环。
                _log?.Invoke($"[DEGRADED] SchedulerService: job '{job.Id}' execution error: {ex.Message}");
                JobFired?.Invoke(job, new JobRunResult(job.Id, job.Command, Success: false, TimedOut: false, ExitCode: -1,
                    StdOut: string.Empty, StdErr: $"[aerocode] job execution error: {ex.Message}", 0, DateTime.UtcNow));
            }
            finally
            {
                lock (_lock)
                {
                    _running.Remove(job.Id);
                }
            }
        }

        return fired;
    }

    // ---- 内部实现 ----

    private void Tick()
    {
        if (Interlocked.Exchange(ref _ticking, 1) == 1)
        {
            return; // 上一轮尚未结束（长任务），跳过本轮防重入堆积
        }

        try
        {
            RunDueJobsOnce(DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[DEGRADED] SchedulerService: tick error: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _ticking, 0);
        }
    }

    /// <summary>调用方必须持 _lock。到期收集：cron 分钟去重与一次性消耗标记仅在 consume=true 时落账。</summary>
    private List<JobDef> CollectDueNoLock(DateTimeOffset nowUtc, bool consume)
    {
        var due = new List<JobDef>();
        var nowLocalMinute = TruncateToMinute(nowUtc.LocalDateTime);
        var persistNeeded = false;

        foreach (var job in _jobs.Values.ToList())
        {
            if (!job.Enabled)
            {
                continue;
            }

            var isDue = false;
            if (job.AtUtc.HasValue)
            {
                isDue = job.AtUtc.Value <= nowUtc.UtcDateTime;
                if (isDue && consume)
                {
                    // 一次性消耗：立即停用并落盘（即使本轮执行失败也不复燃——到期语义以时刻为准）
                    job.Enabled = false;
                    persistNeeded = true;
                }
            }
            else
            {
                if (CronSchedule.Parse(job.Cron!).Matches(nowLocalMinute) && consume)
                {
                    // 同一分钟内不重复触发（进程内去重）；consume=false（ESTOP）时不落账，
                    // 哨兵移除后同一分钟仍可正常触发。
                    if (!_lastFiredMinuteLocal.TryGetValue(job.Id, out var last) || last != nowLocalMinute)
                    {
                        isDue = true;
                        _lastFiredMinuteLocal[job.Id] = nowLocalMinute;
                    }
                }
            }

            if (isDue)
            {
                due.Add(job);
            }
        }

        if (persistNeeded)
        {
            PersistNoLock();
        }

        return due;
    }

    private static DateTime TruncateToMinute(DateTime t)
        => new(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0, t.Kind);

    private JobRunResult Execute(JobDef job)
    {
        var startedUtc = DateTime.UtcNow;
        var sw = Stopwatch.StartNew();
        var timeout = TimeSpan.FromSeconds(Math.Max(1, job.TimeoutSec));
        var isWindows = OperatingSystem.IsWindows();
        var psi = new ProcessStartInfo
        {
            FileName = isWindows ? Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe" : "/bin/sh",
            Arguments = isWindows ? $"/d /s /c \"{job.Command}\"" : $"-c \"{job.Command.Replace("\"", "\\\"")}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = ConsoleOutputEncoding(),
            StandardErrorEncoding = ConsoleOutputEncoding(),
        };

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => AppendCapped(stdout, e.Data);
        process.ErrorDataReceived += (_, e) => AppendCapped(stderr, e.Data);

        if (!process.Start())
        {
            throw new InvalidOperationException($"failed to start process for job '{job.Id}'");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var timedOut = false;
        using var timeoutCts = new CancellationTokenSource(timeout);
        try
        {
            process.WaitForExitAsync(timeoutCts.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            timedOut = true;
            KillTree(process);
        }

        if (!timedOut && !process.HasExited)
        {
            process.WaitForExit(2000);
        }

        var stdoutText = stdout.ToString();
        var stderrText = stderr.ToString();
        if (timedOut)
        {
            stderrText = $"{stderrText}\n[aerocode] job '{job.Id}' timed out after {timeout.TotalSeconds:N0}s and was killed".Trim();
        }

        return new JobRunResult(
            job.Id, job.Command, !timedOut && process.ExitCode == 0, timedOut,
            timedOut ? -1 : process.ExitCode,
            stdoutText, stderrText, sw.ElapsedMilliseconds, startedUtc);
    }

    /// <summary>调用方必须持 _lock。临时文件 + 原子替换，防止半截 JSON。</summary>
    private void PersistNoLock()
    {
        var dir = Path.GetDirectoryName(_jobsFilePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var envelope = new JobsEnvelope { Jobs = _jobs.Values.OrderBy(j => j.Id, StringComparer.Ordinal).ToList() };
        var json = JsonSerializer.Serialize(envelope, WriteOpts);
        var tmp = _jobsFilePath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, _jobsFilePath, overwrite: true);
    }

    private static Encoding ConsoleOutputEncoding()
    {
        try
        {
            return Encoding.GetEncoding(Encoding.Default.CodePage);
        }
        catch (Exception)
        {
            return Encoding.UTF8;
        }
    }

    private static void AppendCapped(StringBuilder sb, string? line)
    {
        if (line is null)
        {
            return;
        }

        if (sb.Length >= MaxCharsPerStream)
        {
            if (sb.Length == MaxCharsPerStream)
            {
                sb.Append("\n[aerocode] output truncated at ").Append(MaxCharsPerStream).Append(" chars");
            }

            return;
        }

        sb.AppendLine(line);
        if (sb.Length > MaxCharsPerStream)
        {
            sb.Length = MaxCharsPerStream;
            sb.Append("\n[aerocode] output truncated at ").Append(MaxCharsPerStream).Append(" chars");
        }
    }

    private static void KillTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // 进程可能已自然退出。
        }
    }

    private sealed class JobsEnvelope
    {
        [JsonPropertyName("jobs")]
        public List<JobDef> Jobs { get; set; } = new();
    }
}

/// <summary>
/// 5 字段 cron 表达式（分 时 日 月 周；本地时间求值）。
/// 支持字段：* / 单值 / 区间 a-b / 步进 *&#47;n 与 a-b&#47;n / 逗号列表。周字段 0 与 7 都是周日。
/// 标准语义：日与周都被显式限制时取“或”，否则取“与”。
/// </summary>
public sealed class CronSchedule
{
    private readonly bool[][] _fields; // [minute, hour, dom, month, dow]
    private readonly bool[] _isStar;
    private static readonly int[] Min = { 0, 0, 1, 1, 0 };
    private static readonly int[] Max = { 59, 23, 31, 12, 6 };

    private CronSchedule(bool[][] fields, bool[] isStar)
    {
        _fields = fields;
        _isStar = isStar;
    }

    public static CronSchedule Parse(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            throw new FormatException("cron expression must not be empty");
        }

        var parts = expression.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 5)
        {
            throw new FormatException($"cron expression must have 5 fields (min hour dom month dow), got {parts.Length}: '{expression}'");
        }

        var fields = new bool[5][];
        var isStar = new bool[5];
        for (var i = 0; i < 5; i++)
        {
            var range = Max[i] - Min[i] + 1;
            var flags = new bool[range];
            var star = false;
            foreach (var term in parts[i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                star |= ParseTerm(term, Min[i], Max[i], flags);
            }

            if (!flags.Any(f => f))
            {
                throw new FormatException($"cron field {i} ('{parts[i]}') matches nothing");
            }

            fields[i] = flags;
            isStar[i] = star;
        }

        return new CronSchedule(fields, isStar);
    }

    /// <summary>按本地时间求值（秒被忽略；分钟粒度）。</summary>
    public bool Matches(DateTime local)
    {
        if (!_fields[0][local.Minute] || !_fields[1][local.Hour] || !_fields[3][local.Month - 1])
        {
            return false;
        }

        var domMatches = _fields[2][local.Day - 1];
        var dowMatches = _fields[4][((int)local.DayOfWeek) % 7];
        if (_isStar[2] && _isStar[4])
        {
            return true;
        }

        if (_isStar[2])
        {
            return dowMatches;
        }

        if (_isStar[4])
        {
            return domMatches;
        }

        return domMatches || dowMatches; // 标准 cron：日与周同时受限 → 或
    }

    private static bool ParseTerm(string term, int min, int max, bool[] flags)
    {
        var star = false;
        var step = 1;
        string rangePart = term;

        var slash = term.IndexOf('/');
        if (slash >= 0)
        {
            rangePart = term[..slash];
            step = int.Parse(term[(slash + 1)..], CultureInfo.InvariantCulture);
            if (step <= 0)
            {
                throw new FormatException($"cron step must be positive: '{term}'");
            }
        }

        int lo, hi;
        if (rangePart == "*")
        {
            // 仅字面量 * 视为“未显式限制”（标准 cron 日/周或规则用）；*/n 视为受限步进。
            star = term == "*";
            lo = min;
            hi = max;
        }
        else if (rangePart.Contains('-'))
        {
            var dash = rangePart.IndexOf('-');
            lo = int.Parse(rangePart[..dash], CultureInfo.InvariantCulture);
            hi = int.Parse(rangePart[(dash + 1)..], CultureInfo.InvariantCulture);
        }
        else
        {
            lo = hi = int.Parse(rangePart, CultureInfo.InvariantCulture);
        }

        // 周字段允许 7 表示周日（0）
        if (min == 0 && max == 6)
        {
            if (lo == 7) lo = 0;
            if (hi == 7) hi = 0;
        }

        if (lo < min || hi > max || lo > hi)
        {
            throw new FormatException($"cron value out of range [{min}-{max}]: '{term}'");
        }

        for (var v = lo; v <= hi; v += step)
        {
            flags[v - min] = true;
        }

        return star;
    }
}
