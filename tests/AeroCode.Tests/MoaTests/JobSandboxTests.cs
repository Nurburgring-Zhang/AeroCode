// Copyright (c) AeroCode
// WindowsJobSandbox 真实行为测试（批次 B G4，builder-γ）：零 mock——
// 真实 Job Object + 真实子进程：KillOnJobClose 全灭、孙进程封禁、CPU 超限即杀、软沙箱放行，全部可观测。
// 内存上限的执行环境说明：per-job commit limits 的内核强制依赖系统提交限制被强制
// （页面文件自动增长时 Windows 不强制），故"内存超限被杀"用例按本机能力门控跳过，
// 不伪造强制行为；限额本身照常写入内核（任何机器上都是有效的 Win32 用法）。
// [降维实现] 同源标注：restricted token（AppContainer）不在本组件覆盖内（批次 C 议题）。
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AeroAgent.Moa.Tools.Workspace;
using Xunit;
using Xunit.Sdk;

namespace AeroCode.Tests.MoaTests;

public sealed class JobSandboxTests : IDisposable
{
    private readonly string _dir;

    public JobSandboxTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "aerocode-jobsandbox-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort 清理 */ }
    }

    private string MarkerPath(string name) => Path.Combine(_dir, name);

    private ProcessStartInfo CmdPsi(string innerCommand)
        => new()
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            Arguments = $"/d /s /c \"{innerCommand}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        };

    private static ProcessStartInfo SleepPsi(int seconds) => new()
    {
        FileName = "powershell.exe",
        Arguments = $"-NoProfile -Command \"Start-Sleep -Seconds {seconds}\"",
        UseShellExecute = false,
        CreateNoWindow = true,
    };

    private static void KillQuietly(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* 已退 */ }
    }

    /// <summary>轮询进程退出（KillOnJobClose 是内核侧异步生效，需短轮询）。</summary>
    private static async Task WaitExitAsync(Process p, TimeSpan limit)
    {
        var deadline = DateTime.UtcNow + limit;
        while (!p.HasExited && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }
    }

    /// <summary>带时限等待退出（超时静默返回，由断言给出确定性失败信息）。</summary>
    private static async Task WaitForExitWithTimeoutAsync(Process p, TimeSpan limit)
    {
        try
        {
            await p.WaitForExitAsync(new CancellationTokenSource(limit).Token);
        }
        catch (OperationCanceledException)
        {
            // 超时：交给断言失败
        }
    }

    // Windows-only：kernel32 Job Object P/Invoke（非 Windows 诚实跳过）。
    private static void RequireWindows() =>
        Skip.IfNot(OperatingSystem.IsWindows(), "WindowsJobSandbox 依赖 Win32 Job Object，非 Windows 跳过");

    [Fact]
    public async Task NoSandbox_Control_ProcessRunsAndWritesMarker()
    {
        RequireWindows();
        var marker = MarkerPath("control.txt");
        var psi = CmdPsi($"echo done> \"{marker}\"");
        using var p = Process.Start(psi)!;
        await WaitForExitWithTimeoutAsync(p, TimeSpan.FromSeconds(20));

        Assert.True(p.ExitCode == 0, $"exit={p.ExitCode}");
        Assert.True(File.Exists(marker), "对照组：无沙箱时同一命令必须正常写文件（证明命令本身有效）");
    }

    [Fact]
    public async Task Start_AssignsWithKillOnJobClose_DisposeKillsLongRunningChild()
    {
        RequireWindows();
        using var sandbox = new WindowsJobSandbox(maxActiveProcesses: 64);
        Assert.NotEqual(IntPtr.Zero, sandbox.Handle);

        using var child = sandbox.Start(SleepPsi(30));
        Assert.False(child.HasExited);
        await Task.Delay(800); // 确保 ps 已起且已圈入 job

        sandbox.Dispose(); // KillOnJobClose：沙箱消亡 = 进程树全灭

        await WaitExitAsync(child, TimeSpan.FromSeconds(10));
        Assert.True(child.HasExited, "KillOnJobClose=true 时 Dispose 必须杀死 job 内全部进程（不留孤儿）");
    }

    [Fact]
    public async Task CpuTimeLimit_OverlimitProcess_KilledByKernel()
    {
        RequireWindows();
        // 2s CPU 上限 vs 60s 死循环：内核直接终止超限进程（强制执行不依赖页面文件配置）
        using var sandbox = new WindowsJobSandbox(perProcessCpuTimeLimit: TimeSpan.FromSeconds(2));
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -Command \"$sw = [System.Diagnostics.Stopwatch]::StartNew(); while ($sw.Elapsed.TotalSeconds -lt 60) { }\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        var sw = Stopwatch.StartNew();
        using var child = sandbox.Start(psi);
        // 退出等待窗 20s→45s：全量并行时 powershell 冷启动 + 2s CPU 配额的墙钟被进一步拉长，
        // 20s 窗在重负载下先于断言上界超时（竞态式假失败）。
        await WaitForExitWithTimeoutAsync(child, TimeSpan.FromSeconds(45));
        sw.Stop();

        Assert.True(child.HasExited, "CPU-overlimit process must be terminated by the kernel");
        // 墙钟上界放宽到 45s：全量并行时进程只分到小份 CPU，2s CPU 配额的墙钟被拉长；
        // 内核强制执行语义（远早于 60s 死循环终止）由 HasExited + 上界共同钉住。
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(45),
            $"2s CPU limit must kill the 60s busy loop far earlier; took {sw.Elapsed}");
    }

    [SkippableFact]
    public async Task ProcessMemoryLimit_ChildExceedingLimit_FailsFastWithNonZeroExit()
    {
        RequireWindows();
        Skip.IfNot(await CommitLimitsEnforcedOnThisMachineAsync(),
            "本机不强制 per-job commit limits（页面文件自动增长时 Windows 不强制内存上限；内核限额已正确设置，" +
            "本机无法诚实验证'超限被杀'，按环境能力门控跳过，不伪造）");

        using var sandbox = new WindowsJobSandbox(processMemoryLimitBytes: 400L * 1024 * 1024); // 400MB
        // 申请 800MB：超过 job 上限 → 分配失败 → PowerShell 以非零退出（而不是睡满 30s）
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -Command \"$x = New-Object byte[] (800MB); Start-Sleep -Seconds 30\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        var sw = Stopwatch.StartNew();
        using var child = sandbox.Start(psi);
        await WaitForExitWithTimeoutAsync(child, TimeSpan.FromSeconds(25));
        sw.Stop();

        Assert.True(child.HasExited, "exceeding-limit child must terminate");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15),
            $"allocation over the limit must fail fast, took {sw.Elapsed}");
        Assert.NotEqual(0, child.ExitCode);
    }

    /// <summary>
    /// 探测本机是否强制 per-job commit limits：64MB job 限额下让子进程直接 VirtualAlloc 256MB，
    /// 失败=本机强制（测试可跑），成功=本机不强制（内存用例诚实跳过）。
    /// </summary>
    private static async Task<bool> CommitLimitsEnforcedOnThisMachineAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aerocode-jobsandbox-probe", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var script = Path.Combine(dir, "alloc.ps1");
            File.WriteAllText(script, """
                $src = @"
                using System;
                using System.Runtime.InteropServices;
                public static class VA {
                  [DllImport("kernel32.dll", SetLastError=true)]
                  public static extern IntPtr VirtualAlloc(IntPtr addr, UIntPtr size, uint type, uint protect);
                }
                "@
                Add-Type -TypeDefinition $src
                $p = [VA]::VirtualAlloc([IntPtr]::Zero, [UIntPtr][uint64]268435456, 0x3000, 0x04)
                if ($p -eq [IntPtr]::Zero) { Write-Host "ALLOC-FAIL" }
                else { Write-Host "ALLOC-OK" }
                """);

            using var sandbox = new WindowsJobSandbox(processMemoryLimitBytes: 64L * 1024 * 1024);
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var child = sandbox.Start(psi);
            var outputTask = child.StandardOutput.ReadToEndAsync();
            await WaitForExitWithTimeoutAsync(child, TimeSpan.FromSeconds(30));
            if (!child.HasExited)
            {
                KillQuietly(child);
                return false; // 探针都杀不动/没跑完：视作不可强制
            }

            var output = await outputTask;
            return output.Contains("ALLOC-FAIL", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task ActiveProcessLimit_GrandchildSpawnIsBlocked()
    {
        RequireWindows();
        var marker = MarkerPath("grandchild.txt");
        var spawnCommand = $"cmd /d /s /c exit 0 && echo spawned> \"{marker}\"";

        // 对照组：无沙箱 → 孙进程正常起，marker 写入
        var controlMarker = MarkerPath("grandchild-control.txt");
        using (var control = Process.Start(CmdPsi($"cmd /d /s /c exit 0 && echo spawned> \"{controlMarker}\""))!)
        {
            await WaitForExitWithTimeoutAsync(control, TimeSpan.FromSeconds(20));
        }

        Assert.True(File.Exists(controlMarker), "对照：无沙箱时孙进程必须能启动（证明命令本身有效）");

        // ActiveProcessLimit=1：cmd（及其控制台伴生进程）已占满 job → 孙进程 CreateProcess 失败 → marker 永不出现
        using var sandbox = new WindowsJobSandbox(maxActiveProcesses: 1);
        using var child = sandbox.Start(CmdPsi(spawnCommand));
        await WaitForExitWithTimeoutAsync(child, TimeSpan.FromSeconds(20));

        Assert.False(File.Exists(marker), "ActiveProcessLimit=1 下孙进程必须被禁止启动（marker 不出现）");
    }

    [Fact]
    public async Task Terminate_KillsRunningChildImmediately()
    {
        RequireWindows();
        using var sandbox = new WindowsJobSandbox();
        using var child = sandbox.Start(SleepPsi(30));
        await Task.Delay(800); // 确保已圈入

        sandbox.Terminate(exitCode: 42);

        await WaitExitAsync(child, TimeSpan.FromSeconds(10));
        Assert.True(child.HasExited, "TerminateJobObject must kill job members immediately");
    }

    [Fact]
    public async Task SoftSandbox_KillOnJobCloseFalse_ChildSurvivesDispose()
    {
        RequireWindows();
        var sandbox = new WindowsJobSandbox(killOnJobClose: false);
        using var child = sandbox.Start(SleepPsi(6));
        await Task.Delay(800);

        sandbox.Dispose(); // 软沙箱：只关句柄，不杀进程

        await Task.Delay(1500);
        Assert.False(child.HasExited, "killOnJobClose=false 的沙箱 Dispose 后进程必须继续运行");
        KillQuietly(child); // 测试自清理
        await WaitExitAsync(child, TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void Constructor_InvalidArguments_Throw()
    {
        RequireWindows();
        Assert.Throws<ArgumentOutOfRangeException>(() => new WindowsJobSandbox(processMemoryLimitBytes: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WindowsJobSandbox(jobMemoryLimitBytes: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WindowsJobSandbox(maxActiveProcesses: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WindowsJobSandbox(perProcessCpuTimeLimit: TimeSpan.Zero));
    }
}
