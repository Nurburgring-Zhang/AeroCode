using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AeroAgent.Moa.Tools;
using AeroCode.AI.Models;
using AeroCode.Harness.EventBus;
using AeroCode.Harness.Permission;
using Xunit;

namespace AeroCode.Tests.MoaTests;

/// <summary>
/// 测试用工具域：按名返回预设结果，记录收到的调用（验证路由分发真实性）。
/// </summary>
internal sealed class ScriptedToolbox : IWorkerToolbox
{
    private readonly Dictionary<string, ToolInvokeResult> _results = new(StringComparer.Ordinal);

    public ScriptedToolbox(string domain, params ToolDefinition[] definitions)
    {
        Domain = domain;
        Definitions = definitions;
    }

    public string Domain { get; }
    public IReadOnlyList<ToolDefinition> Definitions { get; }
    public List<(string Name, string ArgsJson)> Invocations { get; } = new();

    /// <summary>执行前等待的毫秒数（取消测试用：把调用悬挂到取消之后）。</summary>
    public int DelayMs { get; set; }

    public void SetResult(string toolName, ToolInvokeResult result) => _results[toolName] = result;

    public async Task<ToolInvokeResult> InvokeAsync(string toolName, string argumentsJson, CancellationToken ct)
    {
        Invocations.Add((toolName, argumentsJson));
        if (DelayMs > 0)
        {
            await Task.Delay(DelayMs, ct);
        }

        return _results.GetValueOrDefault(toolName)
            ?? ToolInvokeResult.Fail($"no scripted result for '{toolName}'");
    }
}

/// <summary>测试用授权代理：按脚本返回 Allow/Deny，并记录被征求的调用。</summary>
internal sealed class ScriptedBroker : IPermissionBroker
{
    private readonly Queue<PermissionDecision> _decisions = new();

    public ScriptedBroker(params PermissionDecision[] decisions)
    {
        foreach (var d in decisions)
        {
            _decisions.Enqueue(d);
        }
    }

    public List<(string ToolName, IReadOnlyDictionary<string, object?>? Args)> Consultations { get; } = new();

    public ValueTask<PermissionDecision> ResolveAsync(
        string toolName, IReadOnlyDictionary<string, object?>? args, CancellationToken ct)
    {
        Consultations.Add((toolName, args));
        return ValueTask.FromResult(_decisions.Count > 0
            ? _decisions.Dequeue()
            : PermissionDecision.Deny); // 无脚本 = 诚实拒绝，不静默放行
    }
}

/// <summary>
/// 工具内核（注册中心 + 授权路由）行为验证：命名约束、重名防护、
/// Allow/Deny/Ask 三态裁决与参数物化（run_shell 危险模式依赖 string 取参）。
/// </summary>
public sealed class ToolKernelTests
{
    private static PermissionPolicy NewPolicy() => PermissionPolicy.CreateDefault(new EventBus());

    private static ToolDefinition Def(string name) => new()
    {
        Name = name,
        Description = name,
        ParametersJsonSchema = "{\"type\":\"object\"}",
    };

    [Fact]
    public async Task Register_ValidToolbox_DefinitionsVisibleAndInvocable()
    {
        var registry = new ToolboxRegistry();
        var box = new ScriptedToolbox("notes", Def("note_read"), Def("note_search"));
        box.SetResult("note_read", ToolInvokeResult.Ok("笔记正文"));

        registry.Register(box);

        Assert.True(registry.HasTools);
        Assert.Equal(2, registry.AllDefinitions().Count);
        var result = await registry.InvokeAsync("note_read", "{}", CancellationToken.None);
        Assert.True(result.Success);
        Assert.Equal("笔记正文", result.Output);
        Assert.Single(box.Invocations);
    }

    [Fact]
    public void Register_DuplicateToolName_Throws()
    {
        var registry = new ToolboxRegistry();
        registry.Register(new ScriptedToolbox("a", Def("same_tool")));

        var ex = Assert.Throws<ArgumentException>(
            () => registry.Register(new ScriptedToolbox("b", Def("same_tool"))));
        Assert.Contains("same_tool", ex.Message);
    }

    [Theory]
    [InlineData("has.dot")]       // 点号非法（MCP 前缀必须用下划线）
    [InlineData("has space")]
    [InlineData("")]
    [InlineData("tool_name_that_is_deliberately_padded_beyond_the_sixty_four_char_limit")]
    public void Register_InvalidToolName_Throws(string badName)
    {
        var registry = new ToolboxRegistry();
        var ex = Assert.Throws<ArgumentException>(
            () => registry.Register(new ScriptedToolbox("bad", Def(badName))));
        Assert.Contains(badName, ex.Message);
    }

    [Fact]
    public async Task Register_InvalidName_DoesNotPartiallyRegister()
    {
        // 先全量校验再落库：失败的工具箱不能留下半成品注册。
        var registry = new ToolboxRegistry();
        Assert.Throws<ArgumentException>(() =>
            registry.Register(new ScriptedToolbox("mixed", Def("good_tool"), Def("bad.name"))));

        Assert.False(registry.HasTools);
        Assert.Empty(registry.AllDefinitions());
        var miss = await registry.InvokeAsync("good_tool", "{}", CancellationToken.None);
        Assert.False(miss.Success);
    }

    [Fact]
    public void Unregister_RemovesDomainTools()
    {
        var registry = new ToolboxRegistry();
        registry.Register(new ScriptedToolbox("notes", Def("note_read")));
        registry.Register(new ScriptedToolbox("mcp_a", Def("mcp_tool")));

        Assert.True(registry.Unregister("notes"));
        Assert.False(registry.Unregister("notes")); // 幂等：二次移除返回 false

        var definitions = registry.AllDefinitions();
        var name = Assert.Single(definitions).Name;
        Assert.Equal("mcp_tool", name);
    }

    [Fact]
    public async Task InvokeAsync_UnknownTool_HonestFailure()
    {
        var registry = new ToolboxRegistry();
        registry.Register(new ScriptedToolbox("notes", Def("note_read")));

        var result = await registry.InvokeAsync("no_such_tool", "{}", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("no_such_tool", result.Output);
        Assert.False(result.Denied);
    }

    [Fact]
    public async Task Router_AllowPolicy_InvokesToolbox()
    {
        var policy = NewPolicy();
        policy.SetRule(new ToolPermissionRule
        {
            ToolName = "note_read",
            DefaultDecision = PermissionDecision.Allow,
        });
        var box = new ScriptedToolbox("notes", Def("note_read"));
        box.SetResult("note_read", ToolInvokeResult.Ok("内容"));
        var registry = new ToolboxRegistry();
        registry.Register(box);

        var router = new ToolRouter(registry, policy);
        var result = await router.InvokeAsync("note_read", "{}", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("内容", result.Output);
        Assert.Single(box.Invocations);
    }

    [Fact]
    public async Task Router_DenyPolicy_NeverInvokesToolbox()
    {
        var policy = NewPolicy();
        policy.SetDefaultDecision("note_delete", PermissionDecision.Deny);
        var box = new ScriptedToolbox("notes", Def("note_delete"));
        box.SetResult("note_delete", ToolInvokeResult.Ok("deleted"));
        var registry = new ToolboxRegistry();
        registry.Register(box);

        var router = new ToolRouter(registry, policy, new ScriptedBroker(PermissionDecision.Allow));
        var result = await router.InvokeAsync("note_delete", "{}", CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(result.Denied);
        Assert.Contains("Permission denied", result.Output);
        Assert.Empty(box.Invocations); // 拒绝在执行之前，工具根本没跑
    }

    [Fact]
    public async Task Router_AskPolicy_BrokerAllows_Invokes()
    {
        var policy = NewPolicy(); // 未知工具默认 Ask
        var box = new ScriptedToolbox("notes", Def("note_write"));
        box.SetResult("note_write", ToolInvokeResult.Ok("saved"));
        var registry = new ToolboxRegistry();
        registry.Register(box);
        var broker = new ScriptedBroker(PermissionDecision.Allow);

        var router = new ToolRouter(registry, policy, broker);
        var result = await router.InvokeAsync("note_write", "{\"title\":\"x\"}", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Single(broker.Consultations);
        Assert.Equal("note_write", broker.Consultations[0].ToolName);
    }

    [Fact]
    public async Task Router_AskPolicy_BrokerDenies_DoesNotInvoke()
    {
        var policy = NewPolicy();
        var box = new ScriptedToolbox("notes", Def("note_write"));
        box.SetResult("note_write", ToolInvokeResult.Ok("saved"));
        var registry = new ToolboxRegistry();
        registry.Register(box);

        var router = new ToolRouter(registry, policy, new ScriptedBroker(PermissionDecision.Deny));
        var result = await router.InvokeAsync("note_write", "{}", CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(result.Denied);
        Assert.Contains("declined", result.Output);
        Assert.Empty(box.Invocations);
    }

    [Fact]
    public async Task Router_AskPolicy_NoBroker_HonestDeny()
    {
        // 无授权代理（如后台任务）时绝不静默放行。
        var policy = NewPolicy();
        var box = new ScriptedToolbox("notes", Def("note_write"));
        box.SetResult("note_write", ToolInvokeResult.Ok("saved"));
        var registry = new ToolboxRegistry();
        registry.Register(box);

        var router = new ToolRouter(registry, policy, broker: null);
        var result = await router.InvokeAsync("note_write", "{}", CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(result.Denied);
        Assert.Contains("no authorization broker", result.Output);
        Assert.Empty(box.Invocations);
    }

    [Fact]
    public async Task Router_MaterializesArgs_RunShellOverrideSeesCommandString()
    {
        // run_shell 的危险模式 Override 依赖 args["command"] as string——
        // 参数必须从 JSON 物化为真实字符串，否则 Override 永远拿不到命令。
        var policy = NewPolicy();
        var box = new ScriptedToolbox("shell", Def("run_shell"));
        box.SetResult("run_shell", ToolInvokeResult.Ok("ok"));
        var registry = new ToolboxRegistry();
        registry.Register(box);
        var broker = new ScriptedBroker(PermissionDecision.Deny);

        var router = new ToolRouter(registry, policy, broker);

        // 安全命令：Override 直接 Allow，不需要问 broker
        var safe = await router.InvokeAsync("run_shell", "{\"command\":\"git status\"}", CancellationToken.None);
        Assert.True(safe.Success);

        // 危险命令：Override 升级为 Ask → broker 裁决
        var dangerous = await router.InvokeAsync("run_shell", "{\"command\":\"rm -rf /\"}", CancellationToken.None);
        Assert.False(dangerous.Success);
        Assert.True(dangerous.Denied);
        var consult = Assert.Single(broker.Consultations);
        Assert.Equal("run_shell", consult.ToolName);
        Assert.Equal("rm -rf /", consult.Args!["command"] as string);
    }

    [Fact]
    public void MaterializeArgs_Variants_UnwrapCorrectly()
    {
        var dict = ToolRouter.MaterializeArgs(
            "{\"s\":\"文本\",\"i\":42,\"f\":3.5,\"t\":true,\"n\":null,\"arr\":[1,2],\"obj\":{\"k\":1}}");

        Assert.NotNull(dict);
        Assert.Equal("文本", dict!["s"]);
        Assert.Equal(42L, dict["i"]);
        Assert.Equal(3.5, dict["f"]);
        Assert.Equal(true, dict["t"]);
        Assert.Null(dict["n"]);
        Assert.Equal("[1,2]", dict["arr"]);
        Assert.Equal("{\"k\":1}", dict["obj"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]   // 非对象根
    [InlineData("\"str\"")]
    public void MaterializeArgs_InvalidInput_ReturnsNull(string? input)
    {
        Assert.Null(ToolRouter.MaterializeArgs(input));
    }
}
