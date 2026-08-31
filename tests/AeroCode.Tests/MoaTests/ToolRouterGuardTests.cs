// Copyright (c) AeroCode
// ToolRouter preCheck 守卫挂点 + 输出截断挂接的行为验证（沿用 ScriptedToolbox/ScriptedBroker 模式）。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AeroAgent.Moa.Tools;
using AeroCode.AI.Models;
using AeroCode.Harness.EventBus;
using AeroCode.Harness.Permission;
using Xunit;

namespace AeroCode.Tests.MoaTests;

/// <summary>
/// 路由守卫钉子：preCheck 只能把裁决改得更审慎（Allow→Ask→Deny 方向），
/// 绝不能越过策略的显式 Deny；截断 sink 注入后大输出真实落盘并携带引用路径。
/// </summary>
public sealed class ToolRouterGuardTests : IDisposable
{
    private readonly string _dir;

    public ToolRouterGuardTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"routerguard_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // 临时目录清理失败不影响测试结论
        }
    }

    private static ToolDefinition Def(string name) => new()
    {
        Name = name,
        Description = name,
        ParametersJsonSchema = "{\"type\":\"object\"}",
    };

    private static ToolboxRegistry RegistryWith(ScriptedToolbox box)
    {
        var registry = new ToolboxRegistry();
        registry.Register(box);
        return registry;
    }

    [Fact]
    public async Task PreCheck_Ask_EscalatesAllowToolToBroker()
    {
        // read_file 策略本为 Allow；守卫要求更审慎（如路径越界）→ 仍要问 broker。
        var policy = PermissionPolicy.CreateDefault(new EventBus());
        var box = new ScriptedToolbox("notes", Def("read_file"));
        box.SetResult("read_file", ToolInvokeResult.Ok("内容"));
        var broker = new ScriptedBroker(PermissionDecision.Allow);
        IReadOnlyDictionary<string, object?>? seen = null;
        var router = new ToolRouter(
            RegistryWith(box), policy, broker,
            preCheck: (_, args) =>
            {
                seen = args;
                return PermissionDecision.Ask;
            });

        var result = await router.InvokeAsync("read_file", "{\"path\":\"a.txt\"}", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Single(broker.Consultations); // Ask 被真实送到 broker
        Assert.Single(box.Invocations);
        Assert.NotNull(seen);
        Assert.Equal("a.txt", seen!["path"] as string); // 守卫拿到物化参数
    }

    [Fact]
    public async Task PreCheck_Ask_BrokerDenies_Rejected()
    {
        var policy = PermissionPolicy.CreateDefault(new EventBus());
        var box = new ScriptedToolbox("notes", Def("read_file"));
        box.SetResult("read_file", ToolInvokeResult.Ok("内容"));

        var router = new ToolRouter(
            RegistryWith(box), policy, new ScriptedBroker(PermissionDecision.Deny),
            preCheck: (_, _) => PermissionDecision.Ask);

        var result = await router.InvokeAsync("read_file", "{}", CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(result.Denied);
        Assert.Contains("declined", result.Output);
        Assert.Empty(box.Invocations);
    }

    [Fact]
    public async Task PreCheck_Allow_DoesNotBypassPolicyDeny()
    {
        // 守卫的 Allow 不是免检金牌：策略显式 Deny 仍然生效。
        var policy = PermissionPolicy.CreateDefault(new EventBus());
        policy.SetDefaultDecision("note_delete", PermissionDecision.Deny);
        var box = new ScriptedToolbox("notes", Def("note_delete"));
        box.SetResult("note_delete", ToolInvokeResult.Ok("deleted"));

        var router = new ToolRouter(
            RegistryWith(box), policy, new ScriptedBroker(PermissionDecision.Allow),
            preCheck: (_, _) => PermissionDecision.Allow);

        var result = await router.InvokeAsync("note_delete", "{}", CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(result.Denied);
        Assert.Contains("forbidden by policy", result.Output);
        Assert.Empty(box.Invocations);
    }

    [Fact]
    public async Task PreCheck_Null_FallsThroughToNormalPolicy()
    {
        var policy = PermissionPolicy.CreateDefault(new EventBus());
        var box = new ScriptedToolbox("notes", Def("read_file"));
        box.SetResult("read_file", ToolInvokeResult.Ok("直读"));
        var broker = new ScriptedBroker(PermissionDecision.Deny);

        var router = new ToolRouter(
            RegistryWith(box), policy, broker,
            preCheck: (_, _) => null);

        var result = await router.InvokeAsync("read_file", "{}", CancellationToken.None);

        // 守卫弃权 → 策略 Allow 直接执行，不需要 broker
        Assert.True(result.Success);
        Assert.Equal("直读", result.Output);
        Assert.Empty(broker.Consultations);
        Assert.Single(box.Invocations);
    }

    [Fact]
    public async Task PreCheck_Deny_RejectsImmediately()
    {
        var policy = PermissionPolicy.CreateDefault(new EventBus());
        var box = new ScriptedToolbox("notes", Def("read_file"));
        box.SetResult("read_file", ToolInvokeResult.Ok("x"));

        var router = new ToolRouter(
            RegistryWith(box), policy, new ScriptedBroker(PermissionDecision.Allow),
            preCheck: (_, _) => PermissionDecision.Deny);

        var result = await router.InvokeAsync("read_file", "{}", CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(result.Denied);
        Assert.Contains("forbidden by policy", result.Output);
        Assert.Empty(box.Invocations);
    }

    [Fact]
    public async Task PreCheck_Ask_ExplicitDenyTool_StaysDenied_WithoutBroker()
    {
        // 守卫升级为 Ask 后仍要过策略：策略显式 Deny 维持 Deny，绝不反问用户。
        var policy = PermissionPolicy.CreateDefault(new EventBus()); // write_plan 规则 = 显式 Deny
        var box = new ScriptedToolbox("plan", Def("write_plan"));
        box.SetResult("write_plan", ToolInvokeResult.Ok("written"));

        var router = new ToolRouter(
            RegistryWith(box), policy, new ScriptedBroker(PermissionDecision.Allow),
            preCheck: (_, _) => PermissionDecision.Ask);

        var result = await router.InvokeAsync("write_plan", "{\"content\":\"x\"}", CancellationToken.None);

        Assert.True(result.Denied);
        Assert.Empty(box.Invocations);
    }

    [Fact]
    public async Task OutputSink_LargeOutput_TruncatedAndSpilledToRealFile()
    {
        var policy = PermissionPolicy.CreateDefault(new EventBus());
        var box = new ScriptedToolbox("notes", Def("note_dump"));
        var full = string.Join("\n", Enumerable.Range(1, 3000).Select(i => $"row-{i}"));
        box.SetResult("note_dump", ToolInvokeResult.Ok(full));
        var sink = new FileToolOutputSink(Path.Combine(_dir, "outputs"));

        // note_dump 无规则（未知工具=Ask 基线）→ 需要 broker 放行才能到达截断挂点
        var router = new ToolRouter(
            RegistryWith(box), policy, new ScriptedBroker(PermissionDecision.Allow), outputSink: sink);
        var result = await router.InvokeAsync("note_dump", "{}", CancellationToken.None);

        Assert.Contains("showing first 2000 of 3000 lines", result.Output);
        var marker = "Full output saved to: ";
        var idx = result.Output.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(idx >= 0, "截断结果必须携带落盘路径");
        var path = result.Output[(idx + marker.Length)..].TrimEnd('\r', '\n');
        Assert.True(File.Exists(path));
        Assert.Equal(full, File.ReadAllText(path)); // 完整原文真实落盘
    }

    [Fact]
    public async Task OutputSink_SmallOutput_PassedThroughUntouched()
    {
        var policy = PermissionPolicy.CreateDefault(new EventBus());
        var box = new ScriptedToolbox("notes", Def("note_small"));
        box.SetResult("note_small", ToolInvokeResult.Ok("小结果"));
        var sink = new FileToolOutputSink(Path.Combine(_dir, "outputs"));

        var router = new ToolRouter(
            RegistryWith(box), policy, new ScriptedBroker(PermissionDecision.Allow), outputSink: sink);
        var result = await router.InvokeAsync("note_small", "{}", CancellationToken.None);

        Assert.Equal("小结果", result.Output);
        // 未触发截断 → 不产生任何落盘文件
        Assert.Empty(Directory.GetFiles(Path.Combine(_dir, "outputs"), "*", SearchOption.AllDirectories));
    }
}
