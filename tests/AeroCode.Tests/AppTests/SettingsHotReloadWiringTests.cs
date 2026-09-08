// Copyright (c) AeroCode
// R4 γ-2 热重载接线哨兵 + 前置能力测试：SettingsService.SettingsChanged 事件必须有生产消费方
// （F-H1 教训：注册≠可达）——组合根订阅并把 sandbox.enforce 变更同步进共享 ShellSandboxOptions
// （S-LOW-6 enforce 快照语义修复）。App.axaml.cs 属 App 头工程组合根，此处以源码扫描守门
// （与 MissionForegroundAndroidContractTests 同款 FindRepoRoot 模式）。
using AeroAgent.Moa.Tools.Workspace;
using Xunit;

namespace AeroCode.Tests.AppTests;

public sealed class SettingsHotReloadWiringTests
{
    [Fact]
    public void ShellSandboxOptions_EnforceMutableAtRuntime()
    {
        // γ-2 前置能力：Enforce 可写（非 init-only），组合根订阅 SettingsChanged 后
        // 运行时更新同一实例即可被 ShellRunner/GitWorkflow 逐次调用读取。
        var options = new ShellSandboxOptions { Enforce = false };
        options.Enforce = true;
        Assert.True(options.Enforce);
    }

    [SkippableFact]
    public void CompositionRoot_SubscribesSettingsChanged_SyncsSandboxEnforce()
    {
        var repoRoot = FindRepoRoot();
        Skip.If(repoRoot is null, "测试程序集目录向上未找到 AeroCode.sln——无源码树环境跳过");

        var source = File.ReadAllText(Path.Combine(repoRoot!, "src", "AeroCode.App", "App.axaml.cs"));

        // 订阅存在：SettingsChanged 事件在组合根被消费（事件面非死代码）。
        Assert.Contains("settings.SettingsChanged += ", source, StringComparison.Ordinal);
        // 同步目标：共享 ShellSandboxOptions 实例的 Enforce 被更新（run_shell 与 git 同时生效）。
        Assert.Contains("shellSandboxOptions.Enforce = ", source, StringComparison.Ordinal);
        // 变更可观测：热重载经审计日志留痕。
        Assert.Contains("[settings-hotreload]", source, StringComparison.Ordinal);
    }

    /// <summary>与 MissionForegroundAndroidContractTests 同款定位：自测试输出目录向上找解决方案根。</summary>
    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AeroCode.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName;
    }
}
