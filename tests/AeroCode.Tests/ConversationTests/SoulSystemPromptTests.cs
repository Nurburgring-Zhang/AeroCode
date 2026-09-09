// Copyright (c) AeroCode
// SOUL / 长系统提示词回归：SOUL.md 装载（含 ≥2万字无截断）、system 消息前置、
// 门面端到端注入（用户消息体不被污染）、内置预设资源与安装落盘。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AeroAgent.Conversation.Data;
using AeroAgent.Conversation.Models;
using AeroAgent.Conversation.Orchestration;
using AeroAgent.Conversation.Services;
using AeroCode.App.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AeroCode.Tests.ConversationTests;

/// <summary>InstructionLoader 的 SOUL.md 装载行为（真实临时文件）。</summary>
public sealed class SoulLoaderTests : IDisposable
{
    private readonly string _dir;

    public SoulLoaderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"soul_{Guid.NewGuid():N}");
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

    [Fact]
    public void Loader_SoulOnly_HasAnyTrue_AndSoulBlockEmitted()
    {
        File.WriteAllText(Path.Combine(_dir, "SOUL.md"), "我是 AeroCode 的人格基线。");
        var loader = new InstructionLoader(_dir, workspaceRoot: null);

        Assert.True(loader.HasAny);
        var text = loader.Load();
        Assert.Contains("<soul source=\"global soul\">", text);
        Assert.Contains("我是 AeroCode 的人格基线。", text);
        Assert.Equal(Path.Combine(_dir, "SOUL.md"), loader.EffectiveSoulFile());
    }

    [Fact]
    public void Loader_SoulEmittedBeforeInstructions()
    {
        File.WriteAllText(Path.Combine(_dir, "SOUL.md"), "SOUL-MARKER");
        File.WriteAllText(Path.Combine(_dir, "AGENTS.md"), "INSTRUCTION-MARKER");
        var loader = new InstructionLoader(_dir, _dir);

        var merged = loader.Load();
        Assert.True(
            merged.IndexOf("SOUL-MARKER", StringComparison.Ordinal)
            < merged.IndexOf("INSTRUCTION-MARKER", StringComparison.Ordinal),
            "SOUL 段必须先于 instructions 段（人格基线优先于项目指令）");
    }

    [Fact]
    public void Loader_ProjectSoul_TakesPrecedenceInEffectiveFile()
    {
        var globalDir = Path.Combine(_dir, "global");
        var projDir = Path.Combine(_dir, "proj");
        Directory.CreateDirectory(globalDir);
        Directory.CreateDirectory(projDir);
        File.WriteAllText(Path.Combine(globalDir, "SOUL.md"), "GLOBAL-SOUL");
        File.WriteAllText(Path.Combine(projDir, "SOUL.md"), "PROJECT-SOUL");

        var loader = new InstructionLoader(globalDir, projDir);

        Assert.Equal(Path.Combine(projDir, "SOUL.md"), loader.EffectiveSoulFile());
        var merged = loader.Load();
        Assert.Contains("GLOBAL-SOUL", merged);
        Assert.Contains("PROJECT-SOUL", merged);
    }

    [Fact]
    public void Loader_LongSoul_30kChars_NoTruncation()
    {
        // 用户要求：2 万字以上长系统提示词必须全文承载。此处用 3 万字符实证。
        var longSoul = string.Concat(Enumerable.Range(0, 30_000).Select(i => (char)('A' + (i % 26))));
        File.WriteAllText(Path.Combine(_dir, "SOUL.md"), longSoul);
        var loader = new InstructionLoader(_dir, workspaceRoot: null);

        var merged = loader.Load();

        Assert.Contains(longSoul, merged); // 全文原样在输出中（无截断/改写）
        var (globalChars, projectChars) = loader.SoulStats();
        Assert.Equal(30_000, globalChars);
        Assert.Equal(0, projectChars);
    }

    [Fact]
    public void Loader_NoSoul_SoulStatsZero_EffectiveNull()
    {
        var loader = new InstructionLoader(_dir, _dir);

        Assert.Null(loader.EffectiveSoulFile());
        var (globalChars, projectChars) = loader.SoulStats();
        Assert.Equal(0, globalChars);
        Assert.Equal(0, projectChars);
        Assert.False(loader.HasAny);
    }
}

/// <summary>HistoryMapper 的 system 消息前置行为（纯函数）。</summary>
public sealed class HistoryMapperSystemPromptTests
{
    private static List<ChatMessage> UserHistory() => new()
    {
        new ChatMessage { Role = ChatRole.User, Content = "你好", Status = MessageStatus.Completed },
    };

    [Fact]
    public void SystemPrompt_PrependedAsFirstSystemMessage()
    {
        var mapped = HistoryMapper.ToProviderMessages(UserHistory(), "SOUL-CONTEXT");

        Assert.Equal(2, mapped.Count);
        Assert.Equal("system", mapped[0].Role);
        Assert.Equal("SOUL-CONTEXT", mapped[0].Content);
        Assert.Equal("user", mapped[1].Role);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NullOrBlankSystemPrompt_NoSystemMessage(string? systemPrompt)
    {
        var mapped = HistoryMapper.ToProviderMessages(UserHistory(), systemPrompt);

        var single = Assert.Single(mapped);
        Assert.Equal("user", single.Role);
    }
}

/// <summary>
/// 门面端到端：SOUL.md → 独立 system 消息进 provider 请求；
/// 用户消息体与 DB 持久化内容绝不含 SOUL（长文无历史膨胀）。
/// </summary>
public sealed class SoulFacadeIntegrationTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;
    private readonly SqliteConnection _keepAlive;
    private readonly ConversationDbContext _db;
    private readonly SessionService _sessions;

    public SoulFacadeIntegrationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"soul_facade_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);

        _dbPath = Path.Combine(Path.GetTempPath(), $"soul_facade_{Guid.NewGuid():N}.db");
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
        SqliteConnection.ClearPool(_keepAlive);
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }

        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // 临时目录清理失败不影响测试结论
        }
    }

    private static async Task DrainAsync(IAsyncEnumerable<ChatEvent> events)
    {
        await foreach (var _ in events)
        {
            // 消费事件流直至完成
        }
    }

    [Fact]
    public async Task Send_WithSoulFile_ProviderGetsSystemMessage_UserMessageClean()
    {
        File.WriteAllText(Path.Combine(_dir, "SOUL.md"), "SOUL-E2E-MARKER 人格基线");
        var provider = new ScriptedProvider { Deltas = new[] { "ok" } };
        var registry = new TestProviderRegistry();
        registry.Add(provider);
        var facade = new ChatOrchestrationFacade(
            _sessions, registry,
            new IOrchestrationStrategy[] { new SingleStrategy(_sessions) },
            instructions: new InstructionLoader(_dir, workspaceRoot: null));

        var session = (await _sessions.CreateSessionAsync()).Value!;
        await DrainAsync(facade.SendAsync(session.Id, "用户原话"));

        // provider 收到的请求：首条为 system 消息且携带 SOUL 全文
        Assert.NotNull(provider.LastRequestMessages);
        Assert.Equal("system", provider.LastRequestMessages![0].Role);
        Assert.Contains("SOUL-E2E-MARKER 人格基线", provider.LastRequestMessages[0].Content);

        // 落库的用户消息必须是原话——SOUL 不进用户消息体（长文无历史膨胀）
        var messages = (await _sessions.GetMessagesAsync(session.Id)).Value!;
        var user = messages.Single(m => m.Role == ChatRole.User);
        Assert.Equal("用户原话", user.Content);
        Assert.DoesNotContain("SOUL-E2E-MARKER", user.Content);
    }

    [Fact]
    public async Task Send_WithoutLoader_NoSystemMessage()
    {
        var provider = new ScriptedProvider { Deltas = new[] { "ok" } };
        var registry = new TestProviderRegistry();
        registry.Add(provider);
        var facade = new ChatOrchestrationFacade(
            _sessions, registry,
            new IOrchestrationStrategy[] { new SingleStrategy(_sessions) });

        var session = (await _sessions.CreateSessionAsync()).Value!;
        await DrainAsync(facade.SendAsync(session.Id, "你好"));

        Assert.NotNull(provider.LastRequestMessages);
        Assert.Equal("user", provider.LastRequestMessages![0].Role);
    }
}

/// <summary>内置长系统提示词预设：嵌入资源真实存在（≥2万字）且安装落盘逐字一致。</summary>
public sealed class SoulPresetsTests : IDisposable
{
    private readonly string _dir;

    public SoulPresetsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"soul_preset_{Guid.NewGuid():N}");
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

    [Fact]
    public void BuiltinContent_Exists_AndExceeds20kChars()
    {
        var content = SoulPresets.GetBuiltinContent();

        Assert.NotNull(content);
        Assert.True(content!.Length >= 20_000,
            $"内置预设必须承载 2 万字以上长系统提示词，实际 {content.Length} 字符");
        Assert.Equal(content.Length, SoulPresets.BuiltinCharCount);
    }

    [Fact]
    public void InstallBuiltin_WritesFileVerbatim()
    {
        var target = Path.Combine(_dir, "SOUL.md");

        var chars = SoulPresets.InstallBuiltin(target);

        Assert.True(File.Exists(target));
        var written = File.ReadAllText(target);
        Assert.Equal(chars, written.Length);
        Assert.Equal(SoulPresets.GetBuiltinContent(), written);
    }
}
