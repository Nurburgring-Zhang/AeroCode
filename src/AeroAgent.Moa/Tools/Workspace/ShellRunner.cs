// Copyright (c) AeroCode
// ShellRunner — run_shell 的真实进程执行器：超时强杀 + 输出上限，零 mock。
// 批次 C 安全切片（R3 波次 γ）：可选沙箱强制（sandbox.enforce），fail-closed，绝不降级直跑。
using System.Diagnostics;
using System.Text;
using AeroCode.Harness.Curation;

namespace AeroAgent.Moa.Tools.Workspace;

/// <summary>一次 shell 执行的真实结果。</summary>
public sealed record ShellResult(int ExitCode, string StdOut, string StdErr, bool TimedOut);

/// <summary>
/// run_shell 沙箱强制配置（批次 C 安全切片，R3 波次 γ builder-γ）。
/// 默认（调用方不传或 <see cref="Enforce"/>=false）= 现行为逐字节兼容：直接执行，不建沙箱。
/// <see cref="Enforce"/>=true 为 fail-closed 语义：非 Windows 平台 / 沙箱创建失败 / 进程圈入失败
/// 一律拒绝执行（审计记录 + 报错），绝不降级为无沙箱直跑。
/// 能力边界（与 <see cref="WindowsJobSandbox"/> 的 [降维实现] 标注同源，按现状接线）：
/// Job Object 级隔离（内存上限 / 活跃进程上限 / CPU 上限 / KillOnJobClose 全灭）；
/// 【不含】restricted token（AppContainer）级文件系统与网络隔离——文件系统边界仍由
/// WorkspaceBoundary + 守卫链承担，restricted token 为后续批次议题。
/// </summary>
public sealed record ShellSandboxOptions
{
    /// <summary>是否强制沙箱。默认 false = 现行为（直接执行）。</summary>
    public bool Enforce { get; init; }

    /// <summary>单进程提交内存上限（字节）；null = 不设。默认 1GB。</summary>
    public long? ProcessMemoryLimitBytes { get; init; } = 1L << 30;

    /// <summary>全 job 提交内存上限（字节）；null = 不设。默认 2GB。</summary>
    public long? JobMemoryLimitBytes { get; init; } = 2L << 30;

    /// <summary>job 内活跃进程数上限；null = 不设。默认 32。</summary>
    public int? MaxActiveProcesses { get; init; } = 32;

    /// <summary>单进程用户态 CPU 时间上限；null = 不设。</summary>
    public TimeSpan? PerProcessCpuTimeLimit { get; init; }

    /// <summary>
    /// 审计记录出口：fail-closed 拒绝事件（平台不支持/创建失败/启动失败）触发。
    /// null = 只抛异常不做额外审计（测试与最小接线用）；组合根注入真实审计 sink（缝合点）。
    /// </summary>
    public Action<string>? Audit { get; init; }
}

/// <summary>
/// 以工作区根为 cwd 的子进程执行器。Windows 走 cmd.exe /c，Unix 走 /bin/sh -c；
/// 超时强制 Kill（整进程树），输出各端有硬上限防止撑爆上下文。
/// 审慎语义：本类不决定"能不能跑"（那是 <see cref="AeroCode.Harness.Permission.PermissionPolicy"/>
/// 的职责），只忠实执行被允许的命令并如实报告退出码与输出。
/// 批次 C：可选挂 <see cref="WindowsJobSandbox"/>（<see cref="ShellSandboxOptions.Enforce"/>=true），
/// enforce 路径 fail-closed——沙箱任何一环不可用即拒绝执行，绝不降级直跑。
/// </summary>
public sealed class ShellRunner
{
    /// <summary>stdout/stderr 各自的硬上限字符数（超出即截断并标注）。</summary>
    public const int MaxCharsPerStream = 50_000;

    /// <summary>审计消息中命令文本的截断长度（审计记录防超长命令撑爆）。</summary>
    public const int MaxAuditCommandChars = 500;

    private readonly string _workingDirectory;
    private readonly TimeSpan _defaultTimeout;

    public ShellRunner(string workingDirectory, TimeSpan? defaultTimeout = null)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            throw new ArgumentException("working directory must not be empty", nameof(workingDirectory));
        }

        _workingDirectory = workingDirectory;
        _defaultTimeout = defaultTimeout ?? TimeSpan.FromSeconds(60);
    }

    /// <summary>
    /// 执行一条命令。<paramref name="timeoutSeconds"/> ≤ 0 时用默认超时。
    /// 超时即 Kill 整棵进程树，<see cref="ShellResult.TimedOut"/>=true（不冒充正常退出）。
    /// <paramref name="sandbox"/> 为 null 或 <see cref="ShellSandboxOptions.Enforce"/>=false 时
    /// 行为与既有实现完全一致（无沙箱直跑）；Enforce=true 时走 Job Object 沙箱，fail-closed。
    /// </summary>
    public async Task<ShellResult> RunAsync(
        string command, int timeoutSeconds, CancellationToken ct, ShellSandboxOptions? sandbox = null)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new ArgumentException("command must not be empty", nameof(command));
        }

        var timeout = timeoutSeconds > 0
            ? TimeSpan.FromSeconds(timeoutSeconds)
            : _defaultTimeout;

        if (sandbox is { Enforce: true } enforced)
        {
            return await RunSandboxedAsync(command, timeout, ct, enforced);
        }

        return await RunUnsandboxedAsync(command, timeout, ct);
    }

    /// <summary>无沙箱直跑（现行为，操作序列逐字节保留：建 psi → 建进程 → 挂泵 → Start → 收尾）。</summary>
    private async Task<ShellResult> RunUnsandboxedAsync(string command, TimeSpan timeout, CancellationToken ct)
    {
        var psi = BuildShellStartInfo(command);
        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => AppendCapped(stdout, e.Data);
        process.ErrorDataReceived += (_, e) => AppendCapped(stderr, e.Data);

        if (!process.Start())
        {
            throw new InvalidOperationException($"failed to start shell process for: {command}");
        }

        return await CollectOutputAsync(process, stdout, stderr, timeout, ct, command);
    }

    /// <summary>
    /// 沙箱强制执行（Enforce=true）。三道 fail-closed 门，任何一道不过 = 拒绝执行（审计+报错）：
    /// 1. 平台门：非 Windows 拒绝（Win32 Job Object 是 Windows-only 能力，运行时 OS 守卫）；
    /// 2. 创建门：<see cref="WindowsJobSandbox"/> 构造失败拒绝；
    /// 3. 圈入门：Start+Assign 原子化失败拒绝（Assign 失败时沙箱已杀掉刚拉起的进程）。
    /// </summary>
    private async Task<ShellResult> RunSandboxedAsync(
        string command, TimeSpan timeout, CancellationToken ct, ShellSandboxOptions sandbox)
    {
        // ---- fail-closed #1：非 Windows 平台拒绝（Windows-only 能力，运行时守卫）----
        if (!OperatingSystem.IsWindows())
        {
            sandbox.Audit?.Invoke(
                $"[sandbox] enforce refused: non-Windows platform (Windows-only capability); command: {AuditCommand(command)}");
            throw SandboxUnsupportedError();
        }

        // ---- fail-closed #2：沙箱创建失败 → 拒绝执行，绝不降级直跑 ----
        WindowsJobSandbox job;
        try
        {
            job = new WindowsJobSandbox(
                processMemoryLimitBytes: sandbox.ProcessMemoryLimitBytes,
                jobMemoryLimitBytes: sandbox.JobMemoryLimitBytes,
                maxActiveProcesses: sandbox.MaxActiveProcesses,
                perProcessCpuTimeLimit: sandbox.PerProcessCpuTimeLimit,
                killOnJobClose: true);
        }
        catch (Exception ex)
        {
            sandbox.Audit?.Invoke(
                $"[sandbox] creation failed, refusing unsandboxed execution (fail-closed); command: {AuditCommand(command)}; error: {ex.Message}");
            throw new InvalidOperationException(
                "sandbox.enforce=true but sandbox creation failed; execution refused (fail-closed, no unsandboxed fallback).", ex);
        }

        using (job)
        {
            var psi = BuildShellStartInfo(command);

            // ---- fail-closed #3：Start+Assign 原子化（WindowsJobSandbox.Start：圈入失败先杀刚拉起的进程再抛）----
            Process process;
            try
            {
                process = job.Start(psi);
            }
            catch (Exception ex)
            {
                sandbox.Audit?.Invoke(
                    $"[sandbox] process start/assign failed, refusing unsandboxed execution (fail-closed); command: {AuditCommand(command)}; error: {ex.Message}");
                throw new InvalidOperationException(
                    "sandbox.enforce=true but sandboxed process start failed; execution refused (fail-closed, no unsandboxed fallback).", ex);
            }

            try
            {
                var stdout = new StringBuilder();
                var stderr = new StringBuilder();
                process.OutputDataReceived += (_, e) => AppendCapped(stdout, e.Data);
                process.ErrorDataReceived += (_, e) => AppendCapped(stderr, e.Data);
                return await CollectOutputAsync(process, stdout, stderr, timeout, ct, command);
            }
            finally
            {
                try { process.Dispose(); } catch { /* 进程可能已退 */ }
            }
        }
    }

    /// <summary>非 Windows 平台的 fail-closed 拒绝错误（纯函数：跨平台可断言，不真跑进程）。</summary>
    internal static PlatformNotSupportedException SandboxUnsupportedError() => new(
        "sandbox.enforce=true is a Windows-only capability (Win32 Job Object); refusing to execute unsandboxed (fail-closed).");

    /// <summary>
    /// 审计专用命令文本处理（R3 修复 S-MED-3）：先过 canonical 词表脱敏（SensitiveTextScrubber，
    /// 拒绝事件审计绝不携带 sk-/Bearer 等凭据原文），再截断封顶。顺序固定：先脱敏后截断，
    /// 避免"截断恰好保留半截密钥"的边界。
    /// </summary>
    private static string AuditCommand(string command) =>
        TruncateForAudit(SensitiveTextScrubber.Scrub(command));

    private static string TruncateForAudit(string command) =>
        command.Length <= MaxAuditCommandChars ? command : command[..MaxAuditCommandChars] + "…(truncated)";

    /// <summary>shell 启动参数（Windows cmd.exe /c，Unix /bin/sh -c）——与既有实现逐字节一致。</summary>
    private ProcessStartInfo BuildShellStartInfo(string command)
    {
        var isWindows = OperatingSystem.IsWindows();
        return new ProcessStartInfo
        {
            FileName = isWindows ? Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe" : "/bin/sh",
            Arguments = isWindows ? $"/d /s /c \"{command}\"" : $"-c \"{command.Replace("\"", "\\\"")}\"",
            WorkingDirectory = _workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = ConsoleOutputEncoding(),
            StandardErrorEncoding = ConsoleOutputEncoding(),
        };
    }

    /// <summary>等待进程结束并收集输出（超时杀树、取消传播、如实标注）——与既有实现逐字节一致。</summary>
    private static async Task<ShellResult> CollectOutputAsync(
        Process process, StringBuilder stdout, StringBuilder stderr, TimeSpan timeout, CancellationToken ct, string command)
    {
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var timedOut = false;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // 超时（而非调用方取消）：杀整棵进程树，如实标注。
            timedOut = true;
            KillTree(process);
        }

        if (ct.IsCancellationRequested)
        {
            KillTree(process);
            throw new OperationCanceledException(ct);
        }

        // WaitForExitAsync 返回后异步输出可能仍在管道；再等一小段保证收尾。
        if (!timedOut && !process.HasExited)
        {
            process.WaitForExit(2000);
        }

        var stdoutText = stdout.ToString();
        var stderrText = stderr.ToString();
        if (timedOut)
        {
            stderrText = $"{stderrText}\n[aerocode] command timed out after {timeout.TotalSeconds:N0}s and was killed".Trim();
        }

        return new ShellResult(
            timedOut ? -1 : process.ExitCode,
            stdoutText,
            stderrText,
            timedOut);
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
