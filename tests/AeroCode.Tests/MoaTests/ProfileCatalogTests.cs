using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AeroAgent.Moa.Accounting;
using AeroAgent.Moa.Profiles;
using AeroAgent.Moa.Strategies;
using Xunit;

namespace AeroCode.Tests.MoaTests;

/// <summary>画像目录 + JSON 存储 + 成本核算 + 预算的行为测试。</summary>
public sealed class ProfileCatalogTests
{
    [Fact]
    public async Task JsonStore_RoundTrip_PreservesProfilesAndStats()
    {
        var path = Path.Combine(Path.GetTempPath(), $"moa_profiles_{Guid.NewGuid():N}.json");
        try
        {
            var store = new JsonFileProfileStore(path);
            var catalog = new ModelProfileCatalog(store);
            var profile = new ModelProfile
            {
                ProviderId = "deepseek",
                ModelId = string.Empty,
                Strengths = { ModelStrength.Code, ModelStrength.Math },
                CostPerMIn = 0.3,
                CostPerMOut = 1.2,
                SpeedTier = SpeedTier.Fast,
                ContextWindow = 65536,
            };
            profile.Stats.Record(120, failed: false);
            profile.Stats.Record(80, failed: true);
            catalog.Upsert(profile);
            await catalog.SaveAsync();

            var reloaded = new ModelProfileCatalog(new JsonFileProfileStore(path));
            await reloaded.LoadAsync();
            var loaded = reloaded.Find("deepseek", string.Empty);

            Assert.NotNull(loaded);
            Assert.Equal(new[] { ModelStrength.Code, ModelStrength.Math }, loaded!.Strengths);
            Assert.Equal(0.3, loaded.CostPerMIn);
            Assert.Equal(1.2, loaded.CostPerMOut);
            Assert.Equal(SpeedTier.Fast, loaded.SpeedTier);
            Assert.Equal(65536, loaded.ContextWindow);
            Assert.Equal(2, loaded.Stats.Calls);
            Assert.Equal(1, loaded.Stats.Failures);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task Load_SeedThenFileOverride_FileWins()
    {
        var path = Path.Combine(Path.GetTempPath(), $"moa_profiles_{Guid.NewGuid():N}.json");
        try
        {
            // 先用文件写一份覆盖画像
            var writer = new ModelProfileCatalog(new JsonFileProfileStore(path));
            writer.Upsert(new ModelProfile
            {
                ProviderId = "deepseek",
                ModelId = string.Empty,
                Strengths = { ModelStrength.Writing }, // 与种子（code/math/analysis）不同
            });
            await writer.SaveAsync();

            var reader = new ModelProfileCatalog(new JsonFileProfileStore(path));
            await reader.LoadAsync(BuiltInProfiles.Seed());

            var profile = reader.Find("deepseek", string.Empty);
            Assert.NotNull(profile);
            Assert.Equal(new[] { ModelStrength.Writing }, profile!.Strengths); // 文件覆盖种子
            Assert.NotNull(reader.Find("qwen", string.Empty)); // 其余种子仍在
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task CorruptProfilesFile_StoreReturnsNull_CatalogFallsBackToSeeds()
    {
        // P1-3 回归：画像文件由用户手工编辑，损坏不得阻塞应用启动——
        // 存储层返回 null，目录层回退内建种子。
        var path = Path.Combine(Path.GetTempPath(), $"moa_profiles_{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, "{这不是 JSON");
        try
        {
            var store = new JsonFileProfileStore(path);
            Assert.Null(await store.LoadAsync()); // 不抛 JsonException

            var catalog = new ModelProfileCatalog(store);
            await catalog.LoadAsync(BuiltInProfiles.Seed());

            Assert.NotNull(catalog.Find("deepseek", string.Empty)); // 种子铺底成功
            Assert.NotNull(catalog.Find("qwen", string.Empty));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task CorruptProfilesFile_WrongShape_ReturnsEmptyNotNull()
    {
        // 合法 JSON 但结构不符：反序列化出空画像列表（不是崩溃）。
        var path = Path.Combine(Path.GetTempPath(), $"moa_profiles_{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, "{\"profiles\":[]}");
        try
        {
            var loaded = await new JsonFileProfileStore(path).LoadAsync();
            Assert.NotNull(loaded);
            Assert.Empty(loaded!);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void GetOrAddDefault_FallsBackToProviderDefaultProfile()
    {
        var catalog = new ModelProfileCatalog();
        catalog.Upsert(new ModelProfile
        {
            ProviderId = "p1",
            ModelId = string.Empty,
            Strengths = { ModelStrength.Code },
        });

        // 具名模型没有专属画像 → 回退 provider 默认画像（而不是新建 general）
        var resolved = catalog.GetOrAddDefault("p1", "some-model");
        Assert.Equal(ModelProfile.MakeKey("p1", string.Empty), resolved.Key);
        Assert.Contains(ModelStrength.Code, resolved.Strengths);

        // 全新 provider → 创建 general 默认画像
        var fresh = catalog.GetOrAddDefault("unknown", string.Empty);
        Assert.Contains(ModelStrength.General, fresh.Strengths);
        Assert.Null(fresh.CostPerMIn); // 成本未知，不估算
    }

    [Fact]
    public void RecordUsage_UpdatesStats()
    {
        var catalog = new ModelProfileCatalog();
        catalog.RecordUsage("p", "m", 100, failed: false);
        catalog.RecordUsage("p", "m", 300, failed: true);

        var profile = catalog.Find("p", "m");
        Assert.NotNull(profile);
        Assert.Equal(2, profile!.Stats.Calls);
        Assert.Equal(0.5, profile.Stats.FailureRate, 3);
        Assert.Equal(200, profile.Stats.AvgLatencyMs, 3);
    }

    [Fact]
    public void Remove_ExistingProfile_ReturnsTrueAndProfileGone()
    {
        var catalog = new ModelProfileCatalog();
        catalog.Upsert(new ModelProfile { ProviderId = "p1", ModelId = "m1" });
        catalog.Upsert(new ModelProfile { ProviderId = "p1", ModelId = string.Empty });

        Assert.True(catalog.Remove("p1", "m1"));

        Assert.Null(catalog.Find("p1", "m1"));
        Assert.DoesNotContain(catalog.List(), p => p.Key == ModelProfile.MakeKey("p1", "m1"));
        // 同 provider 的其他画像（含默认模型画像）不受牵连
        Assert.NotNull(catalog.Find("p1", string.Empty));
    }

    [Fact]
    public void Remove_MissingProfile_ReturnsFalse()
    {
        var catalog = new ModelProfileCatalog();
        Assert.False(catalog.Remove("ghost", "none"));

        // 删掉精确画像后，GetOrAddDefault 回退链不再命中被删项而是重建默认
        catalog.Upsert(new ModelProfile { ProviderId = "p2", ModelId = "m2" });
        Assert.True(catalog.Remove("p2", "m2"));
        Assert.False(catalog.Remove("p2", "m2"));
        var recreated = catalog.GetOrAddDefault("p2", "m2");
        Assert.Contains(ModelStrength.General, recreated.Strengths);
    }
}

public sealed class CostTrackerTests
{
    [Fact]
    public void Estimate_KnownPrices_ComputesUsd()
    {
        var profile = new ModelProfile { CostPerMIn = 1.0, CostPerMOut = 4.0 };
        // 1M 输入 + 0.5M 输出 = $1 + $2 = $3
        var cost = CostTracker.Estimate(profile, 1_000_000, 500_000);
        Assert.Equal(3.0, cost!.Value, 6);
    }

    [Fact]
    public void Estimate_UnknownPrices_ReturnsNull_NeverGuesses()
    {
        Assert.Null(CostTracker.Estimate(new ModelProfile(), 100, 100));
        Assert.Null(CostTracker.Estimate(new ModelProfile { CostPerMIn = 1.0 }, 100, 100));
        Assert.Null(CostTracker.Estimate(null, 100, 100));
    }

    [Fact]
    public void Estimate_NegativeInputs_ReturnsNull()
    {
        var profile = new ModelProfile { CostPerMIn = 1.0, CostPerMOut = 1.0 };
        Assert.Null(CostTracker.Estimate(profile, -1, 10));
        Assert.Null(CostTracker.Estimate(profile, 10, -1));
    }

    [Fact]
    public void TurnBudget_Unlimited_WhenMaxNull()
    {
        var budget = new TurnBudget(null);
        Assert.True(budget.HasBudget);
        budget.AddActual(1000);
        Assert.True(budget.HasBudget);
    }

    [Fact]
    public void TurnBudget_Exceeded_ReportsHonestly()
    {
        var budget = new TurnBudget(0.01);
        Assert.True(budget.HasBudget);
        var within = budget.AddActual(0.02);
        Assert.False(within);
        Assert.False(budget.HasBudget);
        Assert.Equal(0.02, budget.SpentUsd, 6);
    }

    [Fact]
    public void TurnBudget_RejectsNonPositiveLimit()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TurnBudget(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TurnBudget(-1));
    }
}

public sealed class MoaOptionsStoreTests
{
    [Fact]
    public async Task RoundTrip_PreservesBindingsAndLimits()
    {
        var path = Path.Combine(Path.GetTempPath(), $"moa_options_{Guid.NewGuid():N}.json");
        try
        {
            var store = new JsonMoaOptionsStore(path);
            var options = new MoaOptions
            {
                DefaultStrategy = AeroAgent.Conversation.Models.OrchestrationStrategy.Ensemble,
                Router = new ModelBinding("fast", null),
                Planner = new ModelBinding("smart", "plan-model"),
                EnsembleSize = 3,
                MaxUsdPerTurn = 0.5,
                ToolsEnabled = false,
            };
            await store.SaveAsync(options);

            var loaded = await new JsonMoaOptionsStore(path).LoadAsync();
            Assert.Equal(AeroAgent.Conversation.Models.OrchestrationStrategy.Ensemble, loaded.DefaultStrategy);
            Assert.Equal("fast", loaded.Router!.ProviderId);
            Assert.Null(loaded.Router.ModelId);
            Assert.Equal("plan-model", loaded.Planner!.ModelId);
            Assert.Equal(3, loaded.EnsembleSize);
            Assert.Equal(0.5, loaded.MaxUsdPerTurn);
            Assert.False(loaded.ToolsEnabled);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task DefaultStrategy_PersistedAsStringEnum_HumanReadable()
    {
        var path = Path.Combine(Path.GetTempPath(), $"moa_options_{Guid.NewGuid():N}.json");
        try
        {
            await new JsonMoaOptionsStore(path).SaveAsync(new MoaOptions
            {
                DefaultStrategy = AeroAgent.Conversation.Models.OrchestrationStrategy.Decompose,
            });

            var json = await File.ReadAllTextAsync(path);
            Assert.Contains("\"DefaultStrategy\": \"Decompose\"", json);
            Assert.DoesNotContain("\"DefaultStrategy\": 2", json);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task CorruptFile_FallsBackToDefaults_NotCrash()
    {
        var path = Path.Combine(Path.GetTempPath(), $"moa_options_{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, "{这不是 JSON");
        try
        {
            var loaded = await new JsonMoaOptionsStore(path).LoadAsync();
            Assert.Equal(2, loaded.EnsembleSize);
            Assert.Null(loaded.MaxUsdPerTurn);
            Assert.True(loaded.ToolsEnabled);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task MissingFile_ReturnsDefaults()
    {
        var path = Path.Combine(Path.GetTempPath(), $"moa_options_{Guid.NewGuid():N}.json");
        var loaded = await new JsonMoaOptionsStore(path).LoadAsync();
        Assert.Equal(2, loaded.EnsembleSize);
        Assert.True(loaded.ToolsEnabled);
    }
}

/// <summary>
/// MoaOptions 单例并发语义回归（Reviewer-A P1 修复哨兵）。
/// </summary>
public sealed class MoaOptionsConcurrencyTests
{
    [Fact]
    public void MaxUsdPerTurn_ConcurrentReadWrite_NeverTorn()
    {
        // double? 是 hasValue+value 双字段：无锁时 UI 线程写与策略线程读交错
        // 可撕裂出 hasValue=true + 陈旧 0.0，令 TurnBudget 构造抛异常。
        // 写端在 null ↔ 0.75 间翻转；读端每次读取的值必须是"完整的 null"或
        // "完整的 0.75"，并且直接构造 TurnBudget 不抛——即策略侧真实用法。
        var options = new MoaOptions();
        var observed = new System.Collections.Concurrent.ConcurrentBag<double?>();
        // 自适应停止：采够样本即止，2s 为兜底上限。
        // 历史 flaky：固定 400ms 窗口在机器重负载（并行构建）下会被 Task.Run
        // 的调度延迟整体吃掉，读端一轮都没跑 → observed 为空而假失败；
        // 按采样数停止则只要线程被调度就必然产生样本，断言语义不变。
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var stop = new CancellationTokenSource();
        bool StopRequested() => observed.Count >= 2000 || sw.ElapsedMilliseconds >= 2000;

        var writer = Task.Run(() =>
        {
            var flag = false;
            while (!StopRequested())
            {
                options.MaxUsdPerTurn = flag ? 0.75 : null;
                flag = !flag;
            }
            stop.Cancel();
        });

        var readers = Enumerable.Range(0, 3).Select(_ => Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                var v = options.MaxUsdPerTurn;
                observed.Add(v);
                // 策略侧真实消费路径：撕裂出的 0.0 会在这里抛 ArgumentOutOfRangeException。
                var budget = new TurnBudget(v);
                Assert.True(budget.HasBudget);
            }
        })).ToArray();

        Task.WaitAll(readers.Append(writer).ToArray());

        Assert.NotEmpty(observed);
        foreach (var v in observed)
        {
            Assert.True(v is null || v == 0.75,
                $"撕裂读：观察到非法中间态 {v}（只允许完整的 null 或 0.75）");
        }
    }

    [SkippableFact]
    public async Task Load_FileLockedByOtherProcess_FallsBackToDefaults_NotCrash()
    {
        var path = Path.Combine(Path.GetTempPath(), $"moa_options_{Guid.NewGuid():N}.json");
        await new JsonMoaOptionsStore(path).SaveAsync(new MoaOptions { EnsembleSize = 4 });

        // FileShare.None 的独占锁语义仅 Windows 生效；其他平台直接读不会 IOException，如实跳过。
        using var lockStream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        bool directReadBlocked;
        try
        {
            _ = File.ReadAllText(path);
            directReadBlocked = false;
        }
        catch (IOException)
        {
            directReadBlocked = true;
        }

        Skip.If(!directReadBlocked, "当前平台不强制独占读锁，无法构造文件占用场景");

        var loaded = await new JsonMoaOptionsStore(path).LoadAsync();
        Assert.Equal(2, loaded.EnsembleSize); // 降级默认项而非抛异常炸启动
        Assert.Null(loaded.MaxUsdPerTurn);
    }
}
