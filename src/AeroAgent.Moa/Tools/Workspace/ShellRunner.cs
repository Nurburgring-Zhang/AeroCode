// Copyright (c) AeroCode
// ShellRunner — run_shell 的真实进程执行器：超时强杀 + 输出上限，零 mock。
using System.Diagnostics;
using System.Text;

namespace AeroAgent.Moa.Tools.Workspace;

/// <summary>一次 shell 执行的真实结果。</summary>
public sealed record ShellResult(int ExitCode, string StdOut, string StdErr, bool TimedOut);

/// <summary>
/// 以工作区根为 cwd 的子进程执行器。Windows 走 cmd.exe /c，Unix 走 /bin/sh -c；
/// 超时强制 Kill（整进程树），输出各端有硬上限防止撑爆上下文。
/// 审慎语义：本类不决定"能不能跑"（那是 <see cref="AeroCode.Harness.Permission.PermissionPolicy"/>
/// 的职责），只忠实执行被允许的命令并如实报告退出码与输出。
/// </summary>
public sealed class ShellRunner
{
    /// <summary>stdout/stderr 各自的硬上限字符数（超出即截断并标注）。</summary>
    public const int MaxCharsPerStream = 50_000;

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
    /// </summary>
    public async Task<ShellResult> RunAsync(string command, int timeoutSeconds, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new ArgumentException("command must not be empty", nameof(command));
        }

        var timeout = timeoutSeconds > 0
            ? TimeSpan.FromSeconds(timeoutSeconds)
            : _defaultTimeout;

        var isWindows = OperatingSystem.IsWindows();
        var psi = new ProcessStartInfo
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

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) => AppendCapped(stdout, e.Data);
        process.ErrorDataReceived += (_, e) => AppendCapped(stderr, e.Data);

        if (!process.Start())
        {
            throw new InvalidOperationException($"failed to start shell process for: {command}");
        }

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
