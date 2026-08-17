using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using AeroAgent.Conversation.Data;
using AeroAgent.Conversation.Models;
using AeroAgent.Conversation.Orchestration;
using AeroAgent.Conversation.Services;
using AeroCode.AI.Configuration;
using AeroCode.AI.Models;
using AeroCode.AI.Providers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using AiChatMessage = AeroCode.AI.Models.ChatMessage;

namespace AeroCode.Tests.ConversationTests;

/// <summary>
/// 测试用 provider：可编程的流式/非流式行为（正常/抛错/慢速），
/// 用于验证编排层的事件流与持久化逻辑。
/// </summary>
public sealed class ScriptedProvider : IAiProvider
{
    public string ProviderId { get; init; } = "scripted";
    public string DisplayName => "Scripted";
    public ProviderKind Kind => ProviderKind.OpenAICompatible;
    public bool SupportsStreaming { get; init; } = true;
    public bool SupportsToolCalling => false;
    public bool SupportsThinking => false;

    public string[] Deltas { get; set; } = Array.Empty<string>();
    public Exception? ThrowDuringStream { get; set; }
    public int DelayMsPerChunk { get; set; }
    public string NonStreamContent { get; set; } = "";
    public UsageInfo? NonStreamUsage { get; set; }
    public List<AiChatMessage>? LastRequestMessages { get; private set; }

    /// <summary>按调用次序出队的非流式响应（MOA 多阶段测试用）；空则回退 NonStreamContent。</summary>
    public Queue<string> ChatQueue { get; } = new();

    /// <summary>ChatAsync（非流式）抛出的异常。</summary>
    public Exception? ThrowDuringChat { get; set; }

    public Task<ChatResponse> ChatAsync(ChatRequest request, CancellationToken ct = default)
    {
        LastRequestMessages = request.Messages.ToList();
        if (ThrowDuringChat is not null)
        {
            throw ThrowDuringChat;
        }

        var content = ChatQueue.Count > 0 ? ChatQueue.Dequeue() : NonStreamContent;
        return Task.FromResult(new ChatResponse
        {
            Id = "resp-1",
            Model = request.Model,
            Content = content,
            FinishReason = "stop",
            Usage = NonStreamUsage,
        });
    }

    public async IAsyncEnumerable<ChatChunk> StreamChatAsync(
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        LastRequestMessages = request.Messages.ToList();
        foreach (var delta in Deltas)
        {
            ct.ThrowIfCancellationRequested();
            if (DelayMsPerChunk > 0)
            {
                await Task.Delay(DelayMsPerChunk, ct);
            }

            yield return new ChatChunk { DeltaContent = delta };
        }

        if (ThrowDuringStream is not null)
        {
            throw ThrowDuringStream;
        }

        yield return new ChatChunk { FinishReason = "stop" };
    }

    public Task<bool> HealthCheckAsync(CancellationToken ct = default) => Task.FromResult(true);
}

/// <summary>绕过 ProviderFactory 的测试桩：按 id 返回注入的 provider。</summary>
public sealed class TestProviderRegistry : IProviderRegistry
{
    private readonly Dictionary<string, IAiProvider> _providers = new();
    public string DefaultProviderId { get; set; } = "scripted";

    public void Add(IAiProvider provider) => _providers[provider.ProviderId] = provider;

    public IAiProvider Get(string id) => _providers[id];

    public IEnumerable<string> ListConfiguredIds() => _providers.Keys;

    public bool TryGetConfig(string providerId, [NotNullWhen(true)] out ProviderConfig? config)
    {
        if (_providers.ContainsKey(providerId))
        {
            config = new ProviderConfig
            {
                Id = providerId,
                DisplayName = providerId,
                DefaultModel = "scripted-model",
            };
            return true;
        }

        config = null;
        return false;
    }
}

/// <summary>
/// 编排门面 + Single 策略的行为测试。直接驱动生产
/// <see cref="ChatOrchestrationFacade"/> 与 <see cref="SingleStrategy"/>，
/// provider 用可编程的 <see cref="ScriptedProvider"/>。
/// </summary>
public sealed class OrchestrationTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteConnection _keepAlive;
    private readonly ConversationDbContext _db;
    private readonly SessionService _sessions;

    public OrchestrationTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"orch_test_{Guid.NewGuid():N}.db");
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

    public void Dispose()
    {
        _db.Dispose();
        _keepAlive.Dispose();
        // EF Core 会池化 SQLite 连接；不清池则文件仍被占用无法删除。
        // 只清本测试连接串的池——ClearAllPools 是全局操作，
        // xUnit 并行跑其他测试类时会把别人在用的池一并清掉（偶发失败根因）。
        SqliteConnection.ClearPool(_keepAlive);
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    private static ChatOrchestrationFacade MakeFacade(
        SessionService sessions, TestProviderRegistry registry)
        => new(sessions, registry, new IOrchestrationStrategy[]
        {
            new SingleStrategy(sessions),
        });

    private static async Task<List<ChatEvent>> CollectAsync(
        IAsyncEnumerable<ChatEvent> events)
    {
        var list = new List<ChatEvent>();
        await foreach (var e in events)
        {
            list.Add(e);
        }

        return list;
    }

    [Fact]
    public async Task Send_StreamsDeltas_AndPersistsCompletedMessage()
    {
        var provider = new ScriptedProvider { Deltas = new[] { "你好", "，", "世界" } };
        var registry = new TestProviderRegistry();
        registry.Add(provider);
        var facade = MakeFacade(_sessions, registry);

        var session = (await _sessions.CreateSessionAsync()).Value!;
        var events = await CollectAsync(facade.SendAsync(session.Id, "在吗？"));

        // 事件序列：Started → 3×TextDelta → Completed → TurnCompleted
        Assert.IsType<AssistantMessageStarted>(events[0]);
        Assert.Equal(3, events.OfType<TextDeltaEvent>().Count());
        Assert.Equal("你好，世界", string.Concat(events.OfType<TextDeltaEvent>().Select(d => d.Delta)));
        Assert.IsType<MessageCompletedEvent>(events[^2]);
        Assert.IsType<TurnCompletedEvent>(events[^1]);

        // 持久化验证：用户消息 + 助手消息
        var messages = (await _sessions.GetMessagesAsync(session.Id)).Value!;
        Assert.Equal(2, messages.Count);
        Assert.Equal(ChatRole.User, messages[0].Role);
        Assert.Equal(ChatRole.Assistant, messages[1].Role);
        Assert.Equal("你好，世界", messages[1].Content);
        Assert.Equal(MessageStatus.Completed, messages[1].Status);
        Assert.Equal("scripted", messages[1].ProviderId);
        Assert.Equal("scripted-model", messages[1].ModelId);
    }

    [Fact]
    public async Task Send_ProviderThrows_PersistsFailedMessage()
    {
        var provider = new ScriptedProvider
        {
            Deltas = new[] { "部分" },
            ThrowDuringStream = new InvalidOperationException("上游 500"),
        };
        var registry = new TestProviderRegistry();
        registry.Add(provider);
        var facade = MakeFacade(_sessions, registry);

        var session = (await _sessions.CreateSessionAsync()).Value!;
        var events = await CollectAsync(facade.SendAsync(session.Id, "测试失败"));

        Assert.Contains(events, e => e is MessageFailedEvent f && f.Error.Contains("上游 500"));
        Assert.IsType<TurnCompletedEvent>(events[^1]);

        var messages = (await _sessions.GetMessagesAsync(session.Id)).Value!;
        var assistant = messages.Single(m => m.Role == ChatRole.Assistant);
        Assert.Equal(MessageStatus.Failed, assistant.Status);
        Assert.Contains("上游 500", assistant.Error);
        Assert.Equal("部分", assistant.Content); // 已流出的部分如实保留
    }

    [Fact]
    public async Task Send_Cancelled_PersistsCancelledMessage()
    {
        var provider = new ScriptedProvider
        {
            Deltas = new[] { "1", "2", "3", "4", "5" },
            DelayMsPerChunk = 50,
        };
        var registry = new TestProviderRegistry();
        registry.Add(provider);
        var facade = MakeFacade(_sessions, registry);

        var session = (await _sessions.CreateSessionAsync()).Value!;
        using var cts = new CancellationTokenSource();

        var collected = new List<ChatEvent>();
        await foreach (var ev in facade.SendAsync(session.Id, "慢任务", cts.Token))
        {
            collected.Add(ev);
            if (ev is TextDeltaEvent && collected.OfType<TextDeltaEvent>().Count() >= 2)
            {
                cts.Cancel();
            }
        }

        Assert.Contains(collected, e => e is MessageCancelledEvent);
        var messages = (await _sessions.GetMessagesAsync(session.Id)).Value!;
        var assistant = messages.Single(m => m.Role == ChatRole.Assistant);
        Assert.Equal(MessageStatus.Cancelled, assistant.Status);
    }

    [Fact]
    public async Task Send_HistoryPassedToProvider_InOrder()
    {
        var provider = new ScriptedProvider { Deltas = new[] { "ok" } };
        var registry = new TestProviderRegistry();
        registry.Add(provider);
        var facade = MakeFacade(_sessions, registry);

        var session = (await _sessions.CreateSessionAsync()).Value!;
        await CollectAsync(facade.SendAsync(session.Id, "第一问"));
        await CollectAsync(facade.SendAsync(session.Id, "第二问"));

        // 第二次请求应携带完整历史：user1, assistant1, user2
        Assert.NotNull(provider.LastRequestMessages);
        var roles = provider.LastRequestMessages!.Select(m => m.Role).ToArray();
        Assert.Equal(new[] { "user", "assistant", "user" }, roles);
        Assert.Equal("第二问", provider.LastRequestMessages[2].Content);
    }

    [Fact]
    public async Task Send_UnknownSession_YieldsFailure()
    {
        var registry = new TestProviderRegistry();
        registry.Add(new ScriptedProvider());
        var facade = MakeFacade(_sessions, registry);

        var events = await CollectAsync(facade.SendAsync("no-such-session", "hi"));
        Assert.Single(events);
        Assert.IsType<MessageFailedEvent>(events[0]);
    }

    [Fact]
    public async Task Send_EmptyText_YieldsFailure()
    {
        var registry = new TestProviderRegistry();
        registry.Add(new ScriptedProvider());
        var facade = MakeFacade(_sessions, registry);
        var session = (await _sessions.CreateSessionAsync()).Value!;

        var events = await CollectAsync(facade.SendAsync(session.Id, "   "));
        Assert.Single(events);
        Assert.IsType<MessageFailedEvent>(events[0]);
    }

    [Fact]
    public async Task Send_NonStreamingProvider_PersistsUsage()
    {
        var provider = new ScriptedProvider
        {
            SupportsStreaming = false,
            NonStreamContent = "非流式回答",
            NonStreamUsage = new UsageInfo { PromptTokens = 7, CompletionTokens = 3, TotalTokens = 10 },
        };
        var registry = new TestProviderRegistry();
        registry.Add(provider);
        var facade = MakeFacade(_sessions, registry);
        var session = (await _sessions.CreateSessionAsync()).Value!;

        var events = await CollectAsync(facade.SendAsync(session.Id, "hi"));

        var completed = events.OfType<MessageCompletedEvent>().Single();
        Assert.Equal(7, completed.TokensIn);
        Assert.Equal(3, completed.TokensOut);

        var assistant = (await _sessions.GetMessagesAsync(session.Id)).Value!
            .Single(m => m.Role == ChatRole.Assistant);
        Assert.Equal("非流式回答", assistant.Content);
        Assert.Equal(7, assistant.TokensIn);
    }
}
