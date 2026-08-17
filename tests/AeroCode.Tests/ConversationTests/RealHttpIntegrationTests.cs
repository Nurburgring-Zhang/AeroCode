using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AeroAgent.Conversation.Data;
using AeroAgent.Conversation.Models;
using AeroAgent.Conversation.Orchestration;
using AeroAgent.Conversation.Services;
using AeroCode.AI.Configuration;
using AeroCode.AI.Providers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AeroCode.Tests.ConversationTests;

/// <summary>
/// 真实 HTTP 集成测试：本地起一个 OpenAI 兼容 SSE 服务器（HttpListener，
/// 真实 socket/真实 HTTP 协议），ProviderFactory 走真实 HttpClient 请求，
/// 验证 统一对话门面 → SingleStrategy → OpenAICompatibleProvider → SSE 解析
/// → 持久化 的全链路。无进程内假实现。
/// </summary>
public sealed class RealHttpIntegrationTests : IDisposable
{
    private readonly HttpListener _listener;
    private readonly string _baseUrl;
    private readonly CancellationTokenSource _serverCts = new();
    private readonly Task _serverTask;
    private readonly List<string> _receivedBodies = new();

    private readonly string _dbPath;
    private readonly SqliteConnection _keepAlive;
    private readonly ConversationDbContext _db;
    private readonly SessionService _sessions;

    public RealHttpIntegrationTests()
    {
        // ---- 找一个空闲端口起真实 HTTP 服务 ----
        var port = GetFreePort();
        _baseUrl = $"http://127.0.0.1:{port}/";
        _listener = new HttpListener();
        _listener.Prefixes.Add(_baseUrl);
        _listener.Start();
        _serverTask = Task.Run(ServeLoopAsync);

        _dbPath = Path.Combine(Path.GetTempPath(), $"conv_http_{Guid.NewGuid():N}.db");
        var connStr = new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString();
        _keepAlive = new SqliteConnection(connStr);
        _keepAlive.Open();
        var options = new DbContextOptionsBuilder<ConversationDbContext>()
            .UseSqlite(connStr)
            .Options;
        _db = new ConversationDbContext(options);
        _db.Database.EnsureCreated();
        _sessions = new SessionService(_db);
    }

    private static int GetFreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private async Task ServeLoopAsync()
    {
        while (!_serverCts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync();
            }
            catch (Exception)
            {
                return; // listener 已停止
            }

            using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
            _receivedBodies.Add(await reader.ReadToEndAsync());

            // OpenAI 兼容 SSE：三块内容 + [DONE]
            var payload = string.Join(
                "\n\n",
                "data: {\"id\":\"c1\",\"choices\":[{\"delta\":{\"content\":\"集成\"}}]}",
                "data: {\"id\":\"c1\",\"choices\":[{\"delta\":{\"content\":\"链路\"}}]}",
                "data: {\"id\":\"c1\",\"choices\":[{\"delta\":{\"content\":\"打通\"}}]}",
                "data: [DONE]") + "\n\n";
            var bytes = Encoding.UTF8.GetBytes(payload);
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "text/event-stream";
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
    }

    public void Dispose()
    {
        _serverCts.Cancel();
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch
        {
            // 忽略关闭竞态
        }

        _db.Dispose();
        _keepAlive.Dispose();
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    private ChatOrchestrationFacade MakeFacade()
    {
        var aiOptions = new AIOptions
        {
            DefaultProviderId = "mockhttp",
            Providers =
            {
                new ProviderConfig
                {
                    Id = "mockhttp",
                    DisplayName = "Mock HTTP",
                    Kind = "OpenAICompatible",
                    BaseUrl = $"{_baseUrl}v1",
                    DefaultModel = "mock-model",
                    RequiresApiKey = false,
                    SupportsStreaming = true,
                    TimeoutSeconds = 30,
                },
            },
        };
        var factory = new ProviderFactory(aiOptions, NullLoggerFactory.Instance);
        return new ChatOrchestrationFacade(
            _sessions, factory,
            new IOrchestrationStrategy[] { new SingleStrategy(_sessions) });
    }

    [Fact]
    public async Task Send_ThroughRealHttp_StreamsAndPersists()
    {
        var facade = MakeFacade();
        var session = (await _sessions.CreateSessionAsync()).Value!;

        var events = new List<ChatEvent>();
        await foreach (var ev in facade.SendAsync(session.Id, "你好"))
        {
            events.Add(ev);
        }

        // 事件序列正确
        Assert.IsType<AssistantMessageStarted>(events[0]);
        var deltas = events.OfType<TextDeltaEvent>().Select(d => d.Delta).ToArray();
        Assert.Equal(new[] { "集成", "链路", "打通" }, deltas);
        Assert.IsType<MessageCompletedEvent>(events[^2]);
        Assert.IsType<TurnCompletedEvent>(events[^1]);

        // 持久化正确
        var messages = (await _sessions.GetMessagesAsync(session.Id)).Value!;
        var assistant = messages.Single(m => m.Role == ChatRole.Assistant);
        Assert.Equal("集成链路打通", assistant.Content);
        Assert.Equal(MessageStatus.Completed, assistant.Status);
        Assert.Equal("mockhttp", assistant.ProviderId);
        Assert.Equal("mock-model", assistant.ModelId);

        // 真实 HTTP 请求体到达服务器，且包含用户消息与历史
        Assert.Single(_receivedBodies);
        using var doc = System.Text.Json.JsonDocument.Parse(_receivedBodies[0]);
        var root = doc.RootElement;
        Assert.Equal("mock-model", root.GetProperty("model").GetString());
        var jsonMessages = root.GetProperty("messages");
        var last = jsonMessages[jsonMessages.GetArrayLength() - 1];
        Assert.Equal("user", last.GetProperty("role").GetString());
        Assert.Equal("你好", last.GetProperty("content").GetString());
    }

    [Fact]
    public async Task Send_MultiTurn_SendsFullHistory()
    {
        var facade = MakeFacade();
        var session = (await _sessions.CreateSessionAsync()).Value!;

        await foreach (var _ in facade.SendAsync(session.Id, "第一问"))
        {
        }

        await foreach (var _ in facade.SendAsync(session.Id, "第二问"))
        {
        }

        // 第二次请求体应包含两轮历史
        Assert.Equal(2, _receivedBodies.Count);
        using var doc = System.Text.Json.JsonDocument.Parse(_receivedBodies[1]);
        var messages = doc.RootElement.GetProperty("messages");
        var contents = messages.EnumerateArray()
            .Select(m => m.GetProperty("content").GetString())
            .ToArray();
        Assert.Contains("第一问", contents);
        Assert.Contains("集成链路打通", contents); // 第一轮助手回复进历史
        Assert.Contains("第二问", contents);
    }
}
