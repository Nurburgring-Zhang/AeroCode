// Copyright (c) AeroCode
// ShellRunner 沙箱门控测试（批次 C 安全切片，builder-γ）：
// 默认（无 options / Enforce=false）现行为逐字节兼容（真实直跑）；
// Enforce=true Windows 真实 Job Object 执行（与 JobSandboxTests 同源，不涉 AppContainer/restricted token）；
// fail-closed 分支：沙箱创建失败 → 拒绝执行 + 审计记录，绝不降级直跑；
// 非 Windows 拒绝：端到端分支仅非 Windows 跑（Windows 上以纯函数 SandboxUnsupportedError 断言守卫裁决）。
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AeroAgent.Moa.Tools;
using AeroAgent.Moa.Tools.Workspace;
using Xunit;
using Xunit.Sdk;

namespace AeroCode.Tests.MoaTests;

public sealed class ShellSandboxGateTests : IDisposable
{
    private readonly string _dir;

    public ShellSandboxGateTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "aerocode-shellsandbox-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort 清理 */ }
    }

    private ShellRunner CreateRunner() => new(_dir, TimeSpan.FromSeconds(20));

    // Windows-only：真实进程与 kernel32 Job Object（非 Windows 诚实跳过）。
    private static void RequireWindows() =>
        Skip.IfNot(OperatingSystem.IsWindows(), "ShellRunner 直跑/沙箱路径依赖 Windows shell，非 Windows 跳过");

    [SkippableFact]
    public async Task Default_NoOptions_ExistingBehavior_DirectRunUnchanged()
    {
        RequireWindows();
        var result = await CreateRunner().RunAsync("exit 0", timeoutSeconds: 30, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.False(result.TimedOut);
    }

    [SkippableFact]
    public async Task EnforceFalse_ExistingBehavior_DirectRunUnchanged()
    {
        RequireWindows();
        // Enforce=false（显式关）与默认（不传）同路径：现行为逐字节兼容。
        var result = await CreateRunner().RunAsync(
            "exit 0", timeoutSeconds: 30, CancellationToken.None,
            new ShellSandboxOptions { Enforce = false });

        Assert.Equal(0, result.ExitCode);
    }

    [SkippableFact]
    public async Task EnforceTrue_Windows_RealCommandRunsInsideJobObject()
    {
        RequireWindows();
        // 真实 Job Object + 真实子进程（WindowsJobSandbox 现有能力，按现状接线）；
        // 不真启 AppContainer/restricted token（不在本组件能力内）。
        var result = await CreateRunner().RunAsync(
            "echo sandbox-ok", timeoutSeconds: 30, CancellationToken.None,
            new ShellSandboxOptions { Enforce = true, MaxActiveProcesses = 32 });

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("sandbox-ok", result.StdOut, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task EnforceTrue_SandboxCreationFails_FailClosed_AuditRecorded_NoFallback()
    {
        RequireWindows();
        // 确定性注入沙箱创建失败：ProcessMemoryLimitBytes=0 → WindowsJobSandbox 构造抛
        // ArgumentOutOfRangeException → ShellRunner 必须 fail-closed（拒绝执行 + 审计），绝不降级直跑。
        var audit = new List<string>();
        var options = new ShellSandboxOptions
        {
            Enforce = true,
            ProcessMemoryLimitBytes = 0, // 非法值 → 创建必失败
            Audit = audit.Add,
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateRunner().RunAsync("echo should-never-run", timeoutSeconds: 30, CancellationToken.None, options));

        Assert.Contains("fail-closed", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no unsandboxed fallback", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.IsType<ArgumentOutOfRangeException>(ex.InnerException);
        var entry = Assert.Single(audit);
        Assert.Contains("fail-closed", entry, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("should-never-run", entry, StringComparison.Ordinal); // 审计含命令（可归因）
    }

    [SkippableFact]
    public async Task EnforceTrue_CreationFailed_AuditScrubsEmbeddedSecrets()
    {
        RequireWindows();
        // R3 修复（S-MED-3）：拒绝事件的审计命令文本先过 canonical 词表再截断——
        // 命令内嵌 sk-/Bearer 凭据时，审计只留 [REDACTED]，绝不回显原文。
        const string fakeSk = "sk-ABCDEFGHIJKLMNOPQRSTUVWXYZ123456";
        const string fakeBearer = "Bearer abcdef1234567890";
        var audit = new List<string>();
        var options = new ShellSandboxOptions
        {
            Enforce = true,
            ProcessMemoryLimitBytes = 0, // 非法值 → 创建必失败 → fail-closed 审计路径
            Audit = audit.Add,
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateRunner().RunAsync(
                $"curl -H \"Authorization: {fakeBearer}\" -H \"X-Key: {fakeSk}\" https://example.test",
                timeoutSeconds: 30, CancellationToken.None, options));

        var entry = Assert.Single(audit);
        Assert.Contains("fail-closed", entry, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(fakeSk, entry, StringComparison.Ordinal);
        Assert.DoesNotContain(fakeBearer, entry, StringComparison.Ordinal);
        Assert.DoesNotContain("abcdef1234567890", entry, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", entry, StringComparison.Ordinal); // 脱敏占位符在场
    }

    [SkippableFact]
    public async Task EnforceTrue_CreationFailed_WithoutAuditSink_StillFailClosed()
    {
        RequireWindows();
        // 审计出口可选（null = 只抛异常）：fail-closed 语义不依赖审计 sink 存在。
        var options = new ShellSandboxOptions { Enforce = true, ProcessMemoryLimitBytes = 0 };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateRunner().RunAsync("echo x", timeoutSeconds: 30, CancellationToken.None, options));
    }

    [SkippableFact]
    public async Task EnforceTrue_NonWindows_PlatformRefused_EndToEnd()
    {
        // 端到端非 Windows 分支：仅非 Windows 真跑（Windows CI 上诚实跳过）。
        Skip.If(OperatingSystem.IsWindows(), "非 Windows 分支用例在 Windows 上不可达，跳过");

        var audit = new List<string>();
        var options = new ShellSandboxOptions { Enforce = true, Audit = audit.Add };

        var ex = await Assert.ThrowsAsync<PlatformNotSupportedException>(() =>
            CreateRunner().RunAsync("echo x", timeoutSeconds: 30, CancellationToken.None, options));

        Assert.Contains("fail-closed", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("non-Windows", audit.Count > 0 ? audit[0] : string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SandboxUnsupportedError_PureGuard_CrossPlatformAssertable()
    {
        // 非 Windows 拒绝分支的纯函数化裁决：任何平台都能断言守卫语义（Windows-only + fail-closed）。
        var ex = ShellRunner.SandboxUnsupportedError();
        Assert.IsType<PlatformNotSupportedException>(ex);
        Assert.Contains("Windows-only", ex.Message, StringComparison.Ordinal);
        Assert.Contains("fail-closed", ex.Message, StringComparison.Ordinal);
    }

    // ---- WorkspaceToolbox 接线（run_shell 生产路径可达性）----

    [Fact]
    public async Task Toolbox_EnforceTrue_SandboxUnavailable_FailClosedSurfacesAsToolError_AuditRecorded()
    {
        // 确定性注入沙箱不可用：ProcessMemoryLimitBytes=0 → Windows 上创建必失败；
        // 非 Windows 上平台门先拒绝。两条路都必须 fail-closed，且工具箱契约把拒绝
        // 转成明确错误交还（Success=false + [sandbox] 前缀），绝不抛业务异常/降级直跑。
        var audit = new List<string>();
        var box = new WorkspaceToolbox(
            new WorkspaceContext(_dir),
            CreateRunner(),
            checkpoints: null,
            shellSandbox: new ShellSandboxOptions { Enforce = true, ProcessMemoryLimitBytes = 0, Audit = audit.Add });

        var result = await box.InvokeAsync(
            "run_shell", JsonSerializer.Serialize(new { command = "echo should-never-run" }), CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Contains("[sandbox]", result.Error, StringComparison.Ordinal);
        Assert.Contains("fail-closed", result.Error, StringComparison.OrdinalIgnoreCase);
        var entry = Assert.Single(audit); // 审计记录经注入的 options 透出（可归因）
        Assert.Contains("should-never-run", entry, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Toolbox_NullSandboxOptions_ExistingBehavior_DirectRunUnchanged()
    {
        RequireWindows();
        // 默认（不注入沙箱选项）= 现行为逐字节兼容：run_shell 直跑成功。
        var box = new WorkspaceToolbox(new WorkspaceContext(_dir), CreateRunner());

        var result = await box.InvokeAsync(
            "run_shell", JsonSerializer.Serialize(new { command = "echo toolbox-ok" }), CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Contains("toolbox-ok", result.Output, StringComparison.Ordinal);
    }
}
