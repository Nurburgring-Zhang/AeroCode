// Copyright (c) AeroCode
// MCP 远程传输集成测试（批次 B G4，builder-γ）：零 mock transport——
// 本地真实 HttpListener SSE / Streamable-HTTP MCP 服务器（真实 JSON-RPC over HTTP 握手 + 工具发现/调用）；
// OAuth device-code 流打真实本地端点（RFC 8628 全流程：challenge → pending → token，真实 HTTP）；
// 真实外部端点冒烟走网络门控（env 未设 = 诚实跳过，不伪造通过）。
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using AeroCode.Mcp.Client;
using Xunit;
using Xunit.Sdk;

namespace AeroCode.Tests.McpTests;

public sealed class McpSseTransportTests
{
    // ---- 传输配置解析（TransportKind{Stdio,Sse,StreamableHttp}） ----

    [Fact]
    public void TransportKind_Resolution_StdioDefault_AndExplicitKinds()
    {
        Assert.Equal(McpTransportKind.Stdio, McpTransportFactory.ResolveKind(new McpServerConfig { Id = "a", Command = "dotnet" }));

        Assert.Equal(McpTransportKind.Sse,
            McpTransportFactory.ResolveKind(new McpServerConfig { Id = "b", Url = "http://x/sse", Transport = "sse" }));
        Assert.Equal(McpTransportKind.StreamableHttp,
            McpTransportFactory.ResolveKind(new McpServerConfig { Id = "c", Url = "http://x/mcp", Transport = "streamableHttp" }));
        Assert.Equal(McpTransportKind.StreamableHttp,
            McpTransportFactory.ResolveKind(new McpServerConfig { Id = "d", Url = "http://x/mcp" })); // url 缺省 = 当前标准
        Assert.Equal(McpTransportKind.Stdio,
            McpTransportFactory.ResolveKind(new McpServerConfig { Id = "e", Url = "http://x/mcp", Transport = "stdio" }));

        Assert.Throws<ArgumentException>(
            () => McpTransportFactory.ResolveKind(new McpServerConfig { Id = "f", Url = "http://x/mcp", Transport = "websocket" }));
    }

    [Fact]
    public async Task GatewayCtor_RemoteConfig_RequiresAbsoluteHttpUrl()
    {
        // 远程：合法 url + 无 command → 允许（不再强求 Command）
        await using var ok = new McpGateway(new McpServerConfig { Id = "remote", Url = "http://127.0.0.1:1/mcp" });
        Assert.Equal("remote", ok.ServerId);

        // 远程：相对/非 http url → 拒绝
        Assert.Throws<ArgumentException>(() => new McpGateway(new McpServerConfig { Id = "bad", Url = "ftp://x/y" }));
        Assert.Throws<ArgumentException>(() => new McpGateway(new McpServerConfig { Id = "bad2", Url = "not-a-url" }));
        // 既无 url 又无 command → 拒绝（既有行为保持）
        Assert.Throws<ArgumentException>(() => new McpGateway(new McpServerConfig { Id = "empty" }));
    }

    // ---- SSE 集成（本地真实 MCP 服务器，真实握手/发现/调用） ----

    [Fact]
    public async Task Sse_ListTools_RealHandshakeDiscoversTool()
    {
        await using var server = await LocalMcpHttpServer.StartAsync();
        var config = new McpServerConfig { Id = "sse-local", Url = server.BaseUrl + "sse", Transport = "sse" };
        await using var gateway = new McpGateway(config)
        {
            InitializationTimeout = TimeSpan.FromSeconds(10),
            CallTimeout = TimeSpan.FromSeconds(15),
        };

        var tools = await gateway.ListToolsAsync();

        var tool = Assert.Single(tools);
        Assert.Equal("echo_tool", tool.Name);
        Assert.Contains("text", tool.ParametersJsonSchema);
        Assert.NotNull(server.LastRequestedProtocolVersion); // 真实 initialize 握手发生过
    }

    [Fact]
    public async Task Sse_CallTool_ReturnsRealEchoResult()
    {
        await using var server = await LocalMcpHttpServer.StartAsync();
        var config = new McpServerConfig { Id = "sse-call", Url = server.BaseUrl + "sse", Transport = "sse" };
        await using var gateway = new McpGateway(config)
        {
            InitializationTimeout = TimeSpan.FromSeconds(10),
            CallTimeout = TimeSpan.FromSeconds(15),
        };

        var outcome = await gateway.CallToolAsync(
            "echo_tool",
            new Dictionary<string, object?> { ["text"] = "hello-over-sse" });

        Assert.False(outcome.IsError);
        Assert.Contains("echo: hello-over-sse", outcome.Text);
        Assert.True(server.ToolCallCount >= 1);
    }

    [Fact]
    public async Task Sse_ConfiguredHeaders_AreForwardedOnGetAndPost()
    {
        await using var server = await LocalMcpHttpServer.StartAsync();
        var config = new McpServerConfig
        {
            Id = "sse-headers",
            Url = server.BaseUrl + "sse",
            Transport = "sse",
            Headers = new Dictionary<string, string> { ["X-Api-Key"] = "key-123", ["Authorization"] = "Bearer static-token" },
        };
        await using var gateway = new McpGateway(config)
        {
            InitializationTimeout = TimeSpan.FromSeconds(10),
            CallTimeout = TimeSpan.FromSeconds(15),
        };

        await gateway.ListToolsAsync();

        // SSE 建流的 GET 与消息 POST 都必须带配置头（否则配置静默失效——诚实失败）
        Assert.All(server.Requests, r => Assert.Equal("key-123", r.Header("X-Api-Key")));
        Assert.Contains(server.Requests, r => r.Method == "GET" && r.Path == "/sse" && r.Header("Authorization") == "Bearer static-token");
        Assert.Contains(server.Requests, r => r.Method == "POST" && r.Path == "/message" && r.Header("Authorization") == "Bearer static-token");
    }

    // ---- Streamable HTTP 集成 ----

    [Fact]
    public async Task StreamableHttp_ListTools_And_CallTool_JsonResponses()
    {
        await using var server = await LocalMcpHttpServer.StartAsync();
        var config = new McpServerConfig { Id = "sh-local", Url = server.BaseUrl + "mcp" }; // 缺省 streamableHttp
        await using var gateway = new McpGateway(config)
        {
            InitializationTimeout = TimeSpan.FromSeconds(10),
            CallTimeout = TimeSpan.FromSeconds(15),
        };

        var tools = await gateway.ListToolsAsync();
        Assert.Equal("echo_tool", Assert.Single(tools).Name);

        var outcome = await gateway.CallToolAsync("echo_tool", new Dictionary<string, object?> { ["text"] = "via-streamable" });
        Assert.False(outcome.IsError);
        Assert.Contains("echo: via-streamable", outcome.Text);
    }

    // ---- OAuth Device-Code 流：本地真实端点全流程 ----

    [Fact]
    public async Task DeviceCodeFlow_LocalRealEndpoints_PendingThenToken_ThenCachedInMemoryOnly()
    {
        await using var server = await LocalMcpHttpServer.StartAsync();
        DeviceCodeChallenge? shown = null;
        var provider = new DeviceCodeTokenProvider(
            new Uri(server.BaseUrl + "device/authorize"),
            new Uri(server.BaseUrl + "token"),
            clientId: "aerocode-test-client",
            httpClient: server.CreateClient(),
            onChallenge: c => { shown = c; return Task.CompletedTask; });

        var token1 = await provider.GetAccessTokenAsync();

        // 真实端点交互：challenge 下发（用户码回调）→ token 端点先 authorization_pending 后成功
        Assert.NotNull(shown);
        Assert.Equal("WDVX-MJQW", shown!.UserCode);
        Assert.StartsWith("http://127.0.0.1", shown.VerificationUri);
        Assert.Equal(1, server.DeviceAuthHits);
        Assert.True(server.TokenHits >= 2, $"pending must be retried, token hits = {server.TokenHits}");
        Assert.Equal("at-local-xyz", token1);
        Assert.Equal("at-local-xyz", provider.LastGrant!.AccessToken);

        // 缓存语义：第二次调用不打任何端点（token 只在内存）
        var token2 = await provider.GetAccessTokenAsync();
        Assert.Equal("at-local-xyz", token2);
        Assert.Equal(1, server.DeviceAuthHits);
        Assert.True(server.TokenHits >= 2);
    }

    [Fact]
    public async Task TokenProvider_AuthorizationHeader_InjectedIntoRemoteRequests()
    {
        await using var server = await LocalMcpHttpServer.StartAsync();
        var provider = new DeviceCodeTokenProvider(
            new Uri(server.BaseUrl + "device/authorize"),
            new Uri(server.BaseUrl + "token"),
            clientId: "aerocode-test-client",
            httpClient: server.CreateClient());

        var config = new McpServerConfig
        {
            Id = "sse-oauth",
            Url = server.BaseUrl + "sse",
            Transport = "sse",
        };
        await using var gateway = new McpGateway(config, tokenProvider: provider)
        {
            InitializationTimeout = TimeSpan.FromSeconds(10),
            CallTimeout = TimeSpan.FromSeconds(15),
        };

        await gateway.ListToolsAsync();

        var authHeaders = server.Requests.Select(r => r.Header("Authorization")).Where(a => a is not null).ToList();
        Assert.NotEmpty(authHeaders);
        Assert.All(authHeaders, a => Assert.Equal("Bearer at-local-xyz", a));
    }

    // ---- 真实外部端点冒烟（网络门控：env 未设 = 诚实跳过） ----

    [SkippableFact]
    public async Task DeviceCodeFlow_RealEndpoint_NetworkGatedSmoke()
    {
        var deviceEndpoint = Environment.GetEnvironmentVariable("AEROCODE_MCP_OAUTH_DEVICE_ENDPOINT");
        var tokenEndpoint = Environment.GetEnvironmentVariable("AEROCODE_MCP_OAUTH_TOKEN_ENDPOINT");
        var clientId = Environment.GetEnvironmentVariable("AEROCODE_MCP_OAUTH_CLIENT_ID");
        Skip.IfNot(
            !string.IsNullOrWhiteSpace(deviceEndpoint) && !string.IsNullOrWhiteSpace(tokenEndpoint) && !string.IsNullOrWhiteSpace(clientId),
            "网络门控：未设置 AEROCODE_MCP_OAUTH_DEVICE_ENDPOINT / TOKEN_ENDPOINT / CLIENT_ID（真实 OAuth 端点冒烟默认跳过，如实标注）");

        var provider = new DeviceCodeTokenProvider(new Uri(deviceEndpoint!), new Uri(tokenEndpoint!), clientId!);
        var challenge = await provider.StartAuthorizationAsync(); // 阶段一：真实端点真实应答
        Assert.False(string.IsNullOrWhiteSpace(challenge.UserCode), "real endpoint must return a user_code");
        Assert.False(string.IsNullOrWhiteSpace(challenge.VerificationUri), "real endpoint must return a verification_uri");
    }
}

/// <summary>捕获的请求快照（路径 + 关注头 + 方法）。</summary>
public sealed class RequestSnapshot
{
    public string Method { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public Dictionary<string, string> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public string? Header(string name)
        => Headers.TryGetValue(name, out var v) ? v : null;
}

/// <summary>
/// 本地真实 MCP 服务器（HttpListener，零 mock）：
/// - GET /sse        ：旧式 HTTP+SSE——先推 endpoint 事件，再以 message 事件回 JSON-RPC 响应；
/// - POST /message   ：接收 JSON-RPC 请求/通知（202 Accepted），响应经 SSE 流回；
/// - POST /mcp       ：Streamable HTTP——单请求单 JSON 响应（application/json + Mcp-Session-Id）；
/// - POST /device/authorize + /token：RFC 8628 设备授权端点（首次 authorization_pending，之后发 token）。
/// 工具面：echo_tool(text) → "echo: {text}"。
/// </summary>
public sealed class LocalMcpHttpServer : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<string> _sseOutbox = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentQueue<RequestSnapshot> _requests = new();

    public string BaseUrl { get; private set; } = string.Empty;
    public string? LastRequestedProtocolVersion { get; private set; }
    private int _toolCallCount;
    private int _deviceAuthHits;
    private int _tokenHits;
    public int ToolCallCount => _toolCallCount;
    public int DeviceAuthHits => _deviceAuthHits;
    public int TokenHits => _tokenHits;
    public IReadOnlyCollection<RequestSnapshot> Requests => _requests.ToList();

    public static async Task<LocalMcpHttpServer> StartAsync()
    {
        var server = new LocalMcpHttpServer();
        // 端口探测：TcpListener 抢一个空闲端口再交给 HttpListener（本地测试可接受的窄竞态）
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        server.BaseUrl = $"http://127.0.0.1:{port}/";
        server._listener.Prefixes.Add(server.BaseUrl);
        server._listener.Start();
        _ = Task.Run(server.AcceptLoopAsync);
        await Task.Delay(50); // 让 accept 循环就位
        return server;
    }

    /// <summary>测试用 HttpClient（直连本地端点；不共用默认连接池里的代理）。</summary>
    public HttpClient CreateClient() => new() { BaseAddress = new Uri(BaseUrl) };

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { _listener.Close(); } catch { /* 已关 */ }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync();
            }
            catch
            {
                return; // listener 已关
            }

            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            var req = ctx.Request;
            var path = req.Url!.AbsolutePath;
            _requests.Enqueue(new RequestSnapshot
            {
                Method = req.HttpMethod,
                Path = path,
                Headers = req.Headers.AllKeys.Where(k => k is not null).ToDictionary(k => k!, k => req.Headers[k]!),
            });

            switch (req.HttpMethod, path)
            {
                case ("GET", "/sse"):
                    await ServeSseAsync(ctx);
                    break;
                case ("POST", "/message"):
                    await ServeMessagePostAsync(ctx);
                    break;
                case ("POST", "/mcp"):
                    await ServeStreamablePostAsync(ctx);
                    break;
                case ("DELETE", "/mcp"):
                    ctx.Response.StatusCode = 200;
                    ctx.Response.Close();
                    break;
                case ("POST", "/device/authorize"):
                    Interlocked.Increment(ref _deviceAuthHits);
                    await WriteJsonAsync(ctx, 200, new JsonObject
                    {
                        ["device_code"] = "dc-local-1",
                        ["user_code"] = "WDVX-MJQW",
                        ["verification_uri"] = BaseUrl + "activate",
                        ["verification_uri_complete"] = BaseUrl + "activate?code=WDVX-MJQW",
                        ["expires_in"] = 600,
                        ["interval"] = 1,
                    });
                    break;
                case ("POST", "/token"):
                    Interlocked.Increment(ref _tokenHits);
                    if (Volatile.Read(ref _tokenHits) == 1)
                    {
                        // RFC 8628：用户尚未授权 → authorization_pending（400 + error 字段）
                        await WriteJsonAsync(ctx, 400, new JsonObject { ["error"] = "authorization_pending" });
                    }
                    else
                    {
                        await WriteJsonAsync(ctx, 200, new JsonObject
                        {
                            ["access_token"] = "at-local-xyz",
                            ["token_type"] = "bearer",
                            ["expires_in"] = 3600,
                        });
                    }

                    break;
                default:
                    ctx.Response.StatusCode = 404;
                    ctx.Response.Close();
                    break;
            }
        }
        catch
        {
            try { ctx.Response.Close(); } catch { /* 客户端已断 */ }
        }
    }

    // ---- 旧式 HTTP+SSE：GET /sse 保持长连，推送 endpoint + message 事件 ----

    private async Task ServeSseAsync(HttpListenerContext ctx)
    {
        var resp = ctx.Response;
        resp.ContentType = "text/event-stream";
        resp.SendChunked = true;
        try
        {
            using var writer = new StreamWriter(resp.OutputStream, new UTF8Encoding(false)) { AutoFlush = true };
            await writer.WriteAsync($"event: endpoint\ndata: /message?sid={Guid.NewGuid():N}\n\n");
            await foreach (var message in _sseOutbox.Reader.ReadAllAsync(_cts.Token))
            {
                await writer.WriteAsync($"event: message\ndata: {message}\n\n");
            }
        }
        catch
        {
            // 客户端断开/取消——SSE 流自然结束
        }
        finally
        {
            try { resp.Close(); } catch { /* 已断 */ }
        }
    }

    private async Task ServeMessagePostAsync(HttpListenerContext ctx)
    {
        var body = await ReadBodyAsync(ctx.Request);
        ctx.Response.StatusCode = 202;
        ctx.Response.ContentLength64 = 0;
        ctx.Response.Close();

        var response = BuildJsonRpcResponse(body);
        if (response is not null)
        {
            _sseOutbox.Writer.TryWrite(response.ToJsonString());
        }
    }

    // ---- Streamable HTTP：POST /mcp 单请求单 JSON 响应 ----

    private async Task ServeStreamablePostAsync(HttpListenerContext ctx)
    {
        var body = await ReadBodyAsync(ctx.Request);
        var response = BuildJsonRpcResponse(body);
        if (response is null)
        {
            ctx.Response.StatusCode = 202; // 通知：无响应体
            ctx.Response.ContentLength64 = 0;
            ctx.Response.Close();
            return;
        }

        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "application/json";
        ctx.Response.Headers["Mcp-Session-Id"] = "local-session-1";
        var bytes = Encoding.UTF8.GetBytes(response.ToJsonString());
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.Close();
    }

    // ---- JSON-RPC 处理（initialize / tools/list / tools/call） ----

    private JsonObject? BuildJsonRpcResponse(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (!root.TryGetProperty("id", out var idEl) || idEl.ValueKind == JsonValueKind.Null)
        {
            return null; // 通知（如 notifications/initialized）：无需响应
        }

        var method = root.GetProperty("method").GetString();
        JsonNode? result;
        switch (method)
        {
            case "initialize":
                LastRequestedProtocolVersion = root.GetProperty("params").GetProperty("protocolVersion").GetString();
                result = new JsonObject
                {
                    ["protocolVersion"] = LastRequestedProtocolVersion, // 回显客户端版本：必然在受支持集合内
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                    ["serverInfo"] = new JsonObject { ["name"] = "local-test-server", ["version"] = "1.0.0" },
                };
                break;
            case "tools/list":
                result = new JsonObject
                {
                    ["tools"] = new JsonArray(new JsonObject
                    {
                        ["name"] = "echo_tool",
                        ["description"] = "Echoes the provided text back",
                        ["inputSchema"] = JsonNode.Parse(
                            """{"type":"object","properties":{"text":{"type":"string","description":"text to echo"}},"required":["text"]}"""),
                    }),
                };
                break;
            case "tools/call":
                Interlocked.Increment(ref _toolCallCount);
                var text = root.GetProperty("params").GetProperty("arguments").GetProperty("text").GetString();
                result = new JsonObject
                {
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = $"echo: {text}" }),
                    ["isError"] = false,
                };
                break;
            default:
                return new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = JsonNode.Parse(idEl.GetRawText()),
                    ["error"] = new JsonObject { ["code"] = -32601, ["message"] = $"Method not found: {method}" },
                };
        }

        return new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = JsonNode.Parse(idEl.GetRawText()),
            ["result"] = result,
        };
    }

    private static async Task<string> ReadBodyAsync(HttpListenerRequest request)
    {
        using var reader = new StreamReader(request.InputStream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    private static async Task WriteJsonAsync(HttpListenerContext ctx, int status, JsonObject payload)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        var bytes = Encoding.UTF8.GetBytes(payload.ToJsonString());
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.Close();
    }
}
