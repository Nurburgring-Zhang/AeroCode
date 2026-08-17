using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AeroCode.App.Mcp;
using AeroCode.Mcp.Client;
using Xunit;

namespace AeroCode.Tests.McpTests;

/// <summary>测试双：按脚本应答的网关（仅用于单测；E2E 用真实子进程）。</summary>
internal sealed class ScriptedMcpGateway : IMcpGateway
{
    private readonly IReadOnlyList<McpToolInfo> _tools;

    public ScriptedMcpGateway(string serverId, params McpToolInfo[] tools)
    {
        ServerId = serverId;
        _tools = tools;
    }

    public string ServerId { get; }
    public List<(string ToolName, IReadOnlyDictionary<string, object?>? Args)> Invocations { get; } = new();
    public Exception? ListToolsException { get; set; }
    public Func<string, IReadOnlyDictionary<string, object?>?, CancellationToken, Task<McpCallOutcome>>? OnCall { get; set; }
    public bool Disposed { get; private set; }

    public Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken ct = default)
        => ListToolsException is not null
            ? Task.FromException<IReadOnlyList<McpToolInfo>>(ListToolsException)
            : Task.FromResult(_tools);

    public Task<McpCallOutcome> CallToolAsync(
        string toolName, IReadOnlyDictionary<string, object?>? arguments, CancellationToken ct = default)
    {
        Invocations.Add((toolName, arguments));
        return OnCall is not null
            ? OnCall(toolName, arguments, ct)
            : Task.FromResult(new McpCallOutcome(false, $"ok:{toolName}"));
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// McpToolbox 映射与路由单测：跨服务器去重、名字清洗、超长截断、
/// 发现降级、调用路由还原与失败语义（E2E 真实进程另测）。
/// </summary>
public sealed class McpToolboxTests
{
    private static McpToolInfo Tool(string name, string? description = null)
        => new(name, description, "{\"type\":\"object\"}");

    [Fact]
    public async Task Discover_MergesGateways_WithServerIdPrefix()
    {
        var a = new ScriptedMcpGateway("notes", Tool("create"), Tool("list"));
        var b = new ScriptedMcpGateway("web", Tool("fetch"));
        var box = new McpToolbox(new IMcpGateway[] { a, b });

        await box.DiscoverAsync();

        Assert.Equal(3, box.Definitions.Count);
        Assert.Equal(
            new[] { "notes_create", "notes_list", "web_fetch" },
            box.Definitions.Select(d => d.Name).ToArray());
        Assert.All(box.Definitions, d => Assert.StartsWith("[MCP:", d.Description, StringComparison.Ordinal));
        Assert.Empty(box.DiscoveryWarnings);
    }

    [Fact]
    public async Task Discover_SanitizesIllegalCharacters()
    {
        // 服务器 Id 与远端名里的 "." 等非法字符 → 下划线（工具名只允许 [a-zA-Z0-9_-]）。
        var gw = new ScriptedMcpGateway("my.server", Tool("create.note"));
        var box = new McpToolbox(new IMcpGateway[] { gw });

        await box.DiscoverAsync();

        Assert.Equal("my_server_create_note", Assert.Single(box.Definitions).Name);
    }

    [Fact]
    public async Task Discover_NameCollision_GetsNumericSuffix()
    {
        // "a" + "b_c" 与 "a_b" + "c" 清洗后同为 "a_b_c" → 第二个加 _2。
        var g1 = new ScriptedMcpGateway("a", Tool("b_c"));
        var g2 = new ScriptedMcpGateway("a_b", Tool("c"));
        var box = new McpToolbox(new IMcpGateway[] { g1, g2 });

        await box.DiscoverAsync();

        Assert.Equal(
            new[] { "a_b_c", "a_b_c_2" },
            box.Definitions.Select(d => d.Name).OrderBy(n => n.Length).ToArray());
    }

    [Fact]
    public async Task Discover_OverLongName_TruncatedTo64()
    {
        var longRemote = new string('x', 100);
        var gw = new ScriptedMcpGateway("s", Tool(longRemote));
        var box = new McpToolbox(new IMcpGateway[] { gw });

        await box.DiscoverAsync();

        var name = Assert.Single(box.Definitions).Name;
        Assert.Equal(64, name.Length);
        Assert.True(box.TryGetRoute(name, out var serverId, out var remoteName));
        Assert.Equal("s", serverId);
        Assert.Equal(longRemote, remoteName); // 路由表保存远端原名，截断只影响本地名
    }

    [Fact]
    public async Task Discover_GatewayFailure_DegradesWithoutBlockingOthers()
    {
        var dead = new ScriptedMcpGateway("dead") { ListToolsException = new IOException("pipe broken") };
        var alive = new ScriptedMcpGateway("alive", Tool("ping"));
        var box = new McpToolbox(new IMcpGateway[] { dead, alive });

        await box.DiscoverAsync();

        Assert.Equal("alive_ping", Assert.Single(box.Definitions).Name);
        var warning = Assert.Single(box.DiscoveryWarnings);
        Assert.Contains("dead", warning);
        Assert.Contains("pipe broken", warning);
    }

    [Fact]
    public async Task Discover_CalledTwice_Throws()
    {
        var box = new McpToolbox(new IMcpGateway[] { new ScriptedMcpGateway("s") });
        await box.DiscoverAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => box.DiscoverAsync());
    }

    [Fact]
    public void Definitions_BeforeDiscover_Throws()
    {
        var box = new McpToolbox(Array.Empty<IMcpGateway>());
        Assert.Throws<InvalidOperationException>(() => box.Definitions);
    }

    [Fact]
    public async Task Invoke_RoutesToRemoteName_NotLocalName()
    {
        var gw = new ScriptedMcpGateway("srv", Tool("do.it"));
        var box = new McpToolbox(new IMcpGateway[] { gw });
        await box.DiscoverAsync();

        var result = await box.InvokeAsync("srv_do_it", "{}", CancellationToken.None);

        Assert.True(result.Success);
        var call = Assert.Single(gw.Invocations);
        Assert.Equal("do.it", call.ToolName); // 本地名还原为远端原名
    }

    [Fact]
    public async Task Invoke_UnknownTool_HonestFail()
    {
        var box = new McpToolbox(new IMcpGateway[] { new ScriptedMcpGateway("s", Tool("x")) });
        await box.DiscoverAsync();

        var result = await box.InvokeAsync("nope", "{}", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("nope", result.Error);
    }

    [Fact]
    public async Task Invoke_InvalidArgumentsJson_HonestFail()
    {
        var gw = new ScriptedMcpGateway("s", Tool("x"));
        var box = new McpToolbox(new IMcpGateway[] { gw });
        await box.DiscoverAsync();

        var result = await box.InvokeAsync("s_x", "{这不是JSON", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("JSON", result.Error);
        Assert.Empty(gw.Invocations); // 非法参数不得触达服务器
    }

    [Fact]
    public async Task Invoke_ServerReportsError_FailWithDetail()
    {
        var gw = new ScriptedMcpGateway("s", Tool("x"))
        {
            OnCall = (_, _, _) => Task.FromResult(new McpCallOutcome(true, "Error: note not found")),
        };
        var box = new McpToolbox(new IMcpGateway[] { gw });
        await box.DiscoverAsync();

        var result = await box.InvokeAsync("s_x", "{}", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("Error: note not found", result.Error);
    }

    [Fact]
    public async Task Invoke_TransportExceptionAfterRetries_HonestFail()
    {
        var gw = new ScriptedMcpGateway("s", Tool("x"))
        {
            OnCall = (_, _, _) => Task.FromException<McpCallOutcome>(new IOException("server died")),
        };
        var box = new McpToolbox(new IMcpGateway[] { gw });
        await box.DiscoverAsync();

        var result = await box.InvokeAsync("s_x", "{}", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("server died", result.Error);
    }

    [Fact]
    public async Task Invoke_Cancellation_Propagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var gw = new ScriptedMcpGateway("s", Tool("x"))
        {
            OnCall = (_, _, ct) => Task.FromCanceled<McpCallOutcome>(ct),
        };
        var box = new McpToolbox(new IMcpGateway[] { gw });
        await box.DiscoverAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => box.InvokeAsync("s_x", "{}", cts.Token));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseArguments_Empty_ReturnsNull(string? json)
        => Assert.Null(McpToolbox.ParseArguments(json!));

    [Fact]
    public void ParseArguments_Object_KeepsValues()
    {
        var args = McpToolbox.ParseArguments("{\"title\":\"hi\",\"count\":3,\"nested\":{\"a\":1}}");

        Assert.NotNull(args);
        Assert.Equal(3, args!.Count);
        Assert.Equal("hi", args["title"].ToString());
    }

    [Fact]
    public void ParseArguments_NonObjectRoot_Throws()
        => Assert.ThrowsAny<System.Text.Json.JsonException>(() => McpToolbox.ParseArguments("[1,2]"));

    [Fact]
    public async Task DisposeAsync_DisposesAllGateways()
    {
        var g1 = new ScriptedMcpGateway("a");
        var g2 = new ScriptedMcpGateway("b");
        var box = new McpToolbox(new IMcpGateway[] { g1, g2 });

        await box.DisposeAsync();

        Assert.True(g1.Disposed);
        Assert.True(g2.Disposed);
    }
}
