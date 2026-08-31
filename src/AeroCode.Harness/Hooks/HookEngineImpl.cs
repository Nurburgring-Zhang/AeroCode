// Copyright (c) AeroCode
// HookEngine — IHookEngine 真实实现（批次 B G4，builder-γ）。
// 零 mock：命令以真实子进程执行（cmd /d /s /c），事件 JSON 以 stdin 传入，
// stdout/stderr 各 50KB 截断，超时杀整棵进程树，结果经 EventBus 发布 HookExecutedEvent。
// 失败语义：单钩子失败/不存在/超时都不阻塞主流程，仅如实记录（Runs + HookExecutedEvent）。
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AeroCode.Harness.EventBus;
using Microsoft.Extensions.Logging;

namespace AeroCode.Harness.Hooks;

/// <summary>一次钩子执行的真实结果（观测/测试用；输出已按 50KB/流 截断）。</summary>
public sealed record HookRunRecord(
    string HookId,
    string EventName,
    bool Success,
    bool TimedOut,
    int ExitCode,
    string StdOut,
    string StdErr,
    long ElapsedMs,
    DateTime StartedUtc);

/// <summary>
/// 钩子引擎实现。契约见 <see cref="IHookEngine"/>：
/// 1. LoadFrom 校验 fail-safe：坏 JSON/缺字段/重复 Id/非法超时 → 整份拒载并抛 InvalidDataException
///    （保留上一份有效配置，绝不半载）；
/// 2. 构造时把 Harness 事件白名单接入 Dispatch（HookExecutedEvent 不在白名单内——
///    引擎自身发布的结果事件不再进钩子分发，杜绝自我递归；用户仍可显式 Dispatch 它）；
/// 3. 执行：stdin 传事件 JSON → 关闭；超时（默认 30s）杀进程树；退出码非零=失败；
/// 4. 异步派发不等待（Task.Run），全程 try/catch，钩子故障绝不外抛。
/// </summary>
public sealed class HookEngine : IHookEngine, IDisposable
{
    /// <summary>stdout/stderr 各自的硬上限字符数（超出即截断并标注，与 ShellRunner 同策略）。</summary>
    public const int MaxCharsPerStream = 50_000;

    private const int MaxRunHistory = 200;

    private readonly EventBus.EventBus _bus;
    private readonly ILogger? _logger;
    private readonly object _lock = new();
    private readonly List<HookDef> _hooks = new();
    private readonly Queue<HookRunRecord> _runs = new();
    private readonly List<Action> _unsubscribers = new();
    private static readonly JsonSerializerOptions ReadOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public HookEngine(EventBus.EventBus bus, ILogger? logger = null)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _logger = logger;
        WireDefaultEvents();
    }

    public IReadOnlyList<HookDef> Hooks
    {
        get
        {
            lock (_lock)
            {
                return _hooks.ToList();
            }
        }
    }

    /// <summary>最近若干次钩子执行记录（线程安全快照；诊断/设置页/测试观测）。</summary>
    public IReadOnlyList<HookRunRecord> Runs
    {
        get
        {
            lock (_lock)
            {
                return _runs.ToList();
            }
        }
    }

    /// <summary>从磁盘加载 hooks.json。坏配置 → 抛 InvalidDataException 且不动现有加载结果（fail-safe）。</summary>
    public int LoadFrom(string path)
    {
        List<HookDef> parsed;
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                throw new InvalidDataException($"hooks config not found: {path}");
            }

            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json))
            {
                throw new InvalidDataException($"hooks config is empty: {path}");
            }

            parsed = JsonSerializer.Deserialize<List<HookDef>>(json, ReadOpts)
                     ?? throw new InvalidDataException($"hooks config deserialized to null: {path}");
            Validate(parsed, path);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // JSON 语法错误等：包装为 InvalidDataException，保持契约单一失败签名。
            throw new InvalidDataException($"hooks config rejected (fail-safe, not loaded): {path} — {ex.Message}", ex);
        }

        lock (_lock)
        {
            _hooks.Clear();
            _hooks.AddRange(parsed);
        }

        _logger?.LogInformation("HookEngine loaded {Count} hooks from {Path}", parsed.Count, path);
        return parsed.Count;
    }

    /// <summary>分发一个事件：对所有启用且匹配的钩子异步派发，不等待、不外抛。</summary>
    public void Dispatch(string eventName, string eventJson)
    {
        if (string.IsNullOrWhiteSpace(eventName))
        {
            return;
        }

        List<HookDef> targets;
        lock (_lock)
        {
            targets = _hooks
                .Where(h => h.Enabled
                            && string.Equals(h.Event, eventName, StringComparison.Ordinal)
                            && (h.Match is not { Length: > 0 } || (eventJson?.Contains(h.Match, StringComparison.Ordinal) ?? false)))
                .ToList();
        }

        var payload = eventJson ?? string.Empty;
        foreach (var hook in targets)
        {
            var captured = hook;
            _ = Task.Run(() => ExecuteSafe(captured, eventName, payload));
        }
    }

    public void Dispose()
    {
        foreach (var unsub in _unsubscribers)
        {
            try { unsub(); }
            catch { /* 订阅清理失败不影响主流程 */ }
        }

        _unsubscribers.Clear();
    }

    // ---- 内部实现 ----

    /// <summary>
    /// 默认事件接线：把 Harness 既有事件接入 Dispatch（事件名 = 记录类型名）。
    /// 刻意不含 HookExecutedEvent：引擎自己发布的完成事件若再进分发会造成自我递归；
    /// 用户想对它挂钩可显式调用 Dispatch（契约第 4 条）。
    /// </summary>
    private void WireDefaultEvents()
    {
        Wire<ToolCallEvent>();
        Wire<ToolResultEvent>();
        Wire<SessionStartEvent>();
        Wire<SessionEndEvent>();
        Wire<SkillLoadedEvent>();
        Wire<MemoryUpdatedEvent>();
        Wire<PlanModeChangedEvent>();
        Wire<PermissionRequestedEvent>();
        Wire<CompactionTriggeredEvent>();
        Wire<SubAgentCompletedEvent>();
        Wire<ApprovalCircuitBrokenEvent>();
        Wire<SteerRequestedEvent>();
        Wire<EtopTrippedEvent>();
    }

    private void Wire<T>() where T : class
        => _unsubscribers.Add(_bus.Subscribe<T>(evt => DispatchEvent(evt)));

    private void DispatchEvent<T>(T evt) where T : class
    {
        string json;
        try
        {
            json = JsonSerializer.Serialize(evt, typeof(T));
        }
        catch (Exception ex)
        {
            // 事件序列化失败属于引擎侧故障，不阻塞主流程（诚实记录，不静默伪造成功）。
            _logger?.LogWarning("[DEGRADED] HookEngine failed to serialize event {Type}: {Error}", typeof(T).Name, ex.Message);
            return;
        }

        Dispatch(typeof(T).Name, json);
    }

    private static void Validate(List<HookDef> hooks, string path)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var hook in hooks)
        {
            if (string.IsNullOrWhiteSpace(hook.Id))
            {
                throw new InvalidDataException($"hooks config rejected: entry with missing/empty 'id' in {path}");
            }

            if (string.IsNullOrWhiteSpace(hook.Event))
            {
                throw new InvalidDataException($"hooks config rejected: hook '{hook.Id}' missing/empty 'event' in {path}");
            }

            if (string.IsNullOrWhiteSpace(hook.Command))
            {
                throw new InvalidDataException($"hooks config rejected: hook '{hook.Id}' missing/empty 'command' in {path}");
            }

            if (hook.TimeoutSec <= 0)
            {
                throw new InvalidDataException($"hooks config rejected: hook '{hook.Id}' has non-positive 'timeoutSec' in {path}");
            }

            if (!seen.Add(hook.Id))
            {
                throw new InvalidDataException($"hooks config rejected: duplicate hook id '{hook.Id}' in {path}");
            }
        }
    }

    /// <summary>执行单个钩子：任何异常都转为失败记录，绝不外抛（fail-safe）。</summary>
    private async Task ExecuteSafe(HookDef hook, string eventName, string eventJson)
    {
        var startedUtc = DateTime.UtcNow;
        var sw = Stopwatch.StartNew();
        HookRunRecord record;
        try
        {
            record = await Execute(hook, eventName, eventJson, startedUtc, sw);
        }
        catch (Exception ex)
        {
            record = new HookRunRecord(
                hook.Id, eventName, Success: false, TimedOut: false, ExitCode: -1,
                StdOut: string.Empty, StdErr: $"[aerocode] hook execution error: {ex.Message}",
                sw.ElapsedMilliseconds, startedUtc);
        }

        lock (_lock)
        {
            _runs.Enqueue(record);
            while (_runs.Count > MaxRunHistory)
            {
                _runs.Dequeue();
            }
        }

        // 结果事件诚实留痕；Publish 内部吞订阅者异常，不会反噬引擎。
        _bus.Publish(new HookExecutedEvent(hook.Id, eventName, record.Success, (int)sw.ElapsedMilliseconds, DateTime.UtcNow));
    }

    private async Task<HookRunRecord> Execute(HookDef hook, string eventName, string eventJson, DateTime startedUtc, Stopwatch sw)
    {
        var timeout = hook.TimeoutSec > 0 ? TimeSpan.FromSeconds(hook.TimeoutSec) : TimeSpan.FromSeconds(30);
        var isWindows = OperatingSystem.IsWindows();
        var psi = new ProcessStartInfo
        {
            FileName = isWindows ? Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe" : "/bin/sh",
            Arguments = isWindows ? $"/d /s /c \"{hook.Command}\"" : $"-c \"{hook.Command.Replace("\"", "\\\"")}\"",
            UseShellExecute = false,
            RedirectStandardInput = true,
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
            throw new InvalidOperationException($"failed to start hook process for '{hook.Id}'");
        }

        try
        {
            // 事件 JSON 经 stdin 传入，写完即关闭（命令读到 EOF）。
            // 异步写入：子进程若不等 stdin 就退出，写失败不掩盖执行结果。
            using (var stdin = process.StandardInput)
            {
                await stdin.WriteAsync(eventJson);
                await stdin.FlushAsync();
            }
        }
        catch (Exception)
        {
            // 命令可能不等 stdin 就退出——不掩盖执行结果。
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var timedOut = false;
        using var timeoutCts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = true;
            KillTree(process);
        }

        // WaitForExit 返回后异步输出可能仍在管道；再等一小段保证收尾。
        if (!timedOut && !process.HasExited)
        {
            process.WaitForExit(2000);
        }

        var stdoutText = stdout.ToString();
        var stderrText = stderr.ToString();
        if (timedOut)
        {
            stderrText = $"{stderrText}\n[aerocode] hook '{hook.Id}' timed out after {timeout.TotalSeconds:N0}s and was killed".Trim();
        }

        var success = !timedOut && process.ExitCode == 0;
        return new HookRunRecord(
            hook.Id, eventName, success, timedOut,
            timedOut ? -1 : process.ExitCode,
            stdoutText, stderrText, sw.ElapsedMilliseconds, startedUtc);
    }

    private static Encoding ConsoleOutputEncoding()
    {
        // Windows 命令行输出默认代码页（GBK/936），按字节宽容解码避免整行丢字。
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
            // 进程可能已自然退出——强杀失败不掩盖执行结果。
        }
    }
}
