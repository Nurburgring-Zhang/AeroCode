using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AeroAgent.Conversation.Data;
using AeroAgent.Conversation.Models;
using AeroAgent.Conversation.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AeroCode.Tests.ConversationTests;

/// <summary>
/// SessionService 真实 SQLite 持久化测试（每个用例独立数据库文件，进程级真实读写）。
/// </summary>
public sealed class SessionServiceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteConnection _keepAlive;
    private readonly ConversationDbContext _db;
    private readonly SessionService _svc;

    public SessionServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"conv_test_{Guid.NewGuid():N}.db");
        var connStr = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
        }.ToString();
        // 保持一条常驻连接，避免 SQLite 文件在连接全部关闭后被 EF 池化逻辑误判。
        _keepAlive = new SqliteConnection(connStr);
        _keepAlive.Open();
        var options = new DbContextOptionsBuilder<ConversationDbContext>()
            .UseSqlite(connStr)
            .Options;
        _db = new ConversationDbContext(options);
        _db.Database.EnsureCreated();
        _svc = new SessionService(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        _keepAlive.Dispose();
        // EF Core 会池化 SQLite 连接；不清池则文件仍被占用无法删除。
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    [Fact]
    public async Task CreateSession_PersistsWithDefaults()
    {
        var result = await _svc.CreateSessionAsync();
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.StartsWith("新会话 ", result.Value!.Title);
        Assert.Equal(OrchestrationStrategy.Single, result.Value.Strategy);

        // 真实读回
        var readBack = await _svc.GetSessionAsync(result.Value.Id);
        Assert.True(readBack.IsSuccess);
        Assert.Equal(result.Value.Id, readBack.Value!.Id);
    }

    [Fact]
    public async Task CreateSession_WithExplicitFields_Persists()
    {
        var result = await _svc.CreateSessionAsync(
            OrchestrationStrategy.Decompose, "openrouter", "gpt-4o", "我的会话");
        Assert.True(result.IsSuccess);
        Assert.Equal("我的会话", result.Value!.Title);
        Assert.Equal(OrchestrationStrategy.Decompose, result.Value.Strategy);
        Assert.Equal("openrouter", result.Value.PreferredProviderId);
        Assert.Equal("gpt-4o", result.Value.PreferredModel);
    }

    [Fact]
    public async Task ListSessions_ExcludesSoftDeleted_OrdersPinnedFirst()
    {
        var a = (await _svc.CreateSessionAsync(title: "A")).Value!;
        var b = (await _svc.CreateSessionAsync(title: "B")).Value!;
        var c = (await _svc.CreateSessionAsync(title: "C")).Value!;

        Assert.True((await _svc.TogglePinAsync(b.Id)).IsSuccess);
        Assert.True((await _svc.DeleteSessionAsync(c.Id)).IsSuccess);

        var list = (await _svc.ListSessionsAsync()).Value!;
        Assert.Equal(2, list.Count);
        Assert.Equal("B", list[0].Title);   // 置顶优先
        Assert.Equal("A", list[1].Title);
        Assert.DoesNotContain(list, s => s.Title == "C");
    }

    [Fact]
    public async Task DeleteThenRestore_RoundTrips()
    {
        var s = (await _svc.CreateSessionAsync(title: "X")).Value!;
        Assert.True((await _svc.DeleteSessionAsync(s.Id)).IsSuccess);
        Assert.Empty((await _svc.ListSessionsAsync()).Value!);
        Assert.True((await _svc.RestoreSessionAsync(s.Id)).IsSuccess);
        Assert.Single((await _svc.ListSessionsAsync()).Value!);
    }

    [Fact]
    public async Task RenameSession_UpdatesTitle()
    {
        var s = (await _svc.CreateSessionAsync()).Value!;
        var renamed = await _svc.RenameSessionAsync(s.Id, "改名了");
        Assert.True(renamed.IsSuccess);
        Assert.Equal("改名了", renamed.Value!.Title);

        var bad = await _svc.RenameSessionAsync(s.Id, "   ");
        Assert.False(bad.IsSuccess);
    }

    [Fact]
    public async Task SetStrategy_UpdatesPreferences()
    {
        var s = (await _svc.CreateSessionAsync()).Value!;
        var updated = await _svc.SetStrategyAsync(s.Id, OrchestrationStrategy.Ensemble, "deepseek", "deepseek-chat");
        Assert.True(updated.IsSuccess);
        Assert.Equal(OrchestrationStrategy.Ensemble, updated.Value!.Strategy);
        Assert.Equal("deepseek", updated.Value.PreferredProviderId);
    }

    [Fact]
    public async Task AppendMessage_FirstUserMessage_BecomesTitle()
    {
        var s = (await _svc.CreateSessionAsync()).Value!;
        var msg = new ChatMessage
        {
            SessionId = s.Id,
            Role = ChatRole.User,
            Content = "这是第一条用户消息，用来测试自动标题",
            Status = MessageStatus.Completed,
        };
        Assert.True((await _svc.AppendMessageAsync(msg)).IsSuccess);

        var session = (await _svc.GetSessionAsync(s.Id)).Value!;
        Assert.Equal("这是第一条用户消息，用来测试自动标题", session.Title[..Math.Min(40, session.Title.Length)]);
    }

    [Fact]
    public async Task AppendMessage_LongFirstMessage_TruncatesTitle()
    {
        var s = (await _svc.CreateSessionAsync()).Value!;
        var longText = new string('长', 100);
        var msg = new ChatMessage
        {
            SessionId = s.Id,
            Role = ChatRole.User,
            Content = longText,
            Status = MessageStatus.Completed,
        };
        Assert.True((await _svc.AppendMessageAsync(msg)).IsSuccess);
        var session = (await _svc.GetSessionAsync(s.Id)).Value!;
        Assert.Equal(41, session.Title.Length); // 40 字 + 省略号
        Assert.EndsWith("…", session.Title);
    }

    [Fact]
    public async Task GetMessages_ReturnsChronological()
    {
        var s = (await _svc.CreateSessionAsync()).Value!;
        for (var i = 0; i < 3; i++)
        {
            await _svc.AppendMessageAsync(new ChatMessage
            {
                SessionId = s.Id,
                Role = i % 2 == 0 ? ChatRole.User : ChatRole.Assistant,
                Content = $"msg-{i}",
                Status = MessageStatus.Completed,
                CreatedAtUtc = DateTime.UtcNow.AddSeconds(i),
            });
        }

        var messages = (await _svc.GetMessagesAsync(s.Id)).Value!;
        Assert.Equal(3, messages.Count);
        Assert.Equal(new[] { "msg-0", "msg-1", "msg-2" }, messages.Select(m => m.Content));
    }

    [Fact]
    public async Task UpdateMessage_PersistsTerminalState()
    {
        var s = (await _svc.CreateSessionAsync()).Value!;
        var msg = new ChatMessage
        {
            SessionId = s.Id,
            Role = ChatRole.Assistant,
            Content = "",
            Status = MessageStatus.Streaming,
        };
        await _svc.AppendMessageAsync(msg);

        msg.Content = "完整回答";
        msg.Status = MessageStatus.Completed;
        msg.TokensIn = 10;
        msg.TokensOut = 20;
        msg.CostUsd = 0.001;
        msg.LatencyMs = 123;
        Assert.True((await _svc.UpdateMessageAsync(msg)).IsSuccess);

        var reloaded = (await _svc.GetMessagesAsync(s.Id)).Value!.Single();
        Assert.Equal("完整回答", reloaded.Content);
        Assert.Equal(MessageStatus.Completed, reloaded.Status);
        Assert.Equal(10, reloaded.TokensIn);
        Assert.Equal(20, reloaded.TokensOut);
        Assert.Equal(123, reloaded.LatencyMs);
    }

    [Fact]
    public async Task AppendMessage_UnknownSession_Fails()
    {
        var result = await _svc.AppendMessageAsync(new ChatMessage
        {
            SessionId = "nonexistent",
            Content = "hi",
        });
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task GetSession_UnknownId_Fails()
    {
        var result = await _svc.GetSessionAsync("no-such-id");
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task SessionRestart_DataSurvives()
    {
        // 模拟进程重启：释放当前 context，用同一文件新建 context 读取。
        var s = (await _svc.CreateSessionAsync(title: "持久化")).Value!;
        await _svc.AppendMessageAsync(new ChatMessage
        {
            SessionId = s.Id,
            Role = ChatRole.User,
            Content = "重启前写入",
            Status = MessageStatus.Completed,
        });

        var connStr = _keepAlive.ConnectionString;
        var options = new DbContextOptionsBuilder<ConversationDbContext>()
            .UseSqlite(connStr)
            .Options;
        await using var db2 = new ConversationDbContext(options);
        var svc2 = new SessionService(db2);

        var sessions = (await svc2.ListSessionsAsync()).Value!;
        Assert.Contains(sessions, x => x.Title == "持久化");
        var messages = (await svc2.GetMessagesAsync(s.Id)).Value!;
        Assert.Single(messages);
        Assert.Equal("重启前写入", messages[0].Content);
    }
}
