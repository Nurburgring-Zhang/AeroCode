using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using AeroAgent.Conversation.Data;
using AeroAgent.Conversation.Models;
using AeroAgent.Conversation.Orchestration;
using AeroAgent.Conversation.Services;
using AeroAgent.Moa.Assignment;
using AeroAgent.Moa.Profiles;
using AeroAgent.Moa.Strategies;
using AeroCode.Tests.ConversationTests;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AeroCode.Tests.MoaTests;

/// <summary>
/// MOA 测试共享基座：临时 SQLite 会话库 + 内存画像目录 + 可编程 provider 注册表。
/// </summary>
public abstract class MoaTestBase : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteConnection _keepAlive;

    protected ConversationDbContext Db { get; }
    protected SessionService Sessions { get; }
    protected ModelProfileCatalog Catalog { get; } = new();
    protected TestProviderRegistry Registry { get; } = new();
    protected MoaOptions Options { get; } = new();

    protected WorkerRunner Runner { get; }
    protected ModelAssigner Assigner { get; }
    protected ModelResolver Resolver { get; }

    protected MoaTestBase()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"moa_test_{Guid.NewGuid():N}.db");
        var connStr = new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString();
        _keepAlive = new SqliteConnection(connStr);
        _keepAlive.Open();
        var options = new DbContextOptionsBuilder<ConversationDbContext>()
            .UseSqlite(connStr)
            .Options;
        Db = new ConversationDbContext(options);
        Db.Database.EnsureCreated();
        Sessions = new SessionService(Db);

        Runner = new WorkerRunner(Sessions, Catalog);
        Assigner = new ModelAssigner(Registry, Catalog);
        Resolver = new ModelResolver(Registry, Catalog, Assigner);
    }

    public virtual void Dispose()
    {
        Sessions.Dispose();
        Db.Dispose();
        _keepAlive.Dispose();
        // 只清本连接串的池：ClearAllPools 是全局操作，会干扰 xUnit 并行运行的其他测试类。
        SqliteConnection.ClearPool(_keepAlive);
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    /// <summary>注册一个可编程 provider 并返回实例。</summary>
    protected ScriptedProvider AddProvider(string id)
    {
        var provider = new ScriptedProvider { ProviderId = id };
        Registry.Add(provider);
        return provider;
    }

    /// <summary>给 provider 的默认模型画像设置强项（可选价格/速度档）。</summary>
    protected ModelProfile SetProfile(
        string providerId,
        string[] strengths,
        double? costPerMIn = null,
        double? costPerMOut = null,
        SpeedTier speed = SpeedTier.Medium)
    {
        var profile = new ModelProfile
        {
            ProviderId = providerId,
            ModelId = string.Empty,
            Strengths = new List<string>(strengths),
            CostPerMIn = costPerMIn,
            CostPerMOut = costPerMOut,
            SpeedTier = speed,
        };
        Catalog.Upsert(profile);
        return profile;
    }

    protected async Task<ChatSession> NewSessionAsync(OrchestrationStrategy strategy)
    {
        var result = await Sessions.CreateSessionAsync(strategy);
        Assert.True(result.IsSuccess);
        return result.Value!;
    }

    protected ChatOrchestrationFacade MakeFacade(params IOrchestrationStrategy[] strategies)
    {
        var all = new List<IOrchestrationStrategy> { new SingleStrategy(Sessions) };
        all.AddRange(strategies);
        return new ChatOrchestrationFacade(Sessions, Registry, all);
    }

    protected static async Task<List<ChatEvent>> CollectAsync(IAsyncEnumerable<ChatEvent> events)
    {
        var list = new List<ChatEvent>();
        await foreach (var e in events)
        {
            list.Add(e);
        }

        return list;
    }
}
