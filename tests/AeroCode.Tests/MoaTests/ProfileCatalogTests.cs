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
                Router = new ModelBinding("fast", null),
                Planner = new ModelBinding("smart", "plan-model"),
                EnsembleSize = 3,
                MaxUsdPerTurn = 0.5,
            };
            await store.SaveAsync(options);

            var loaded = await new JsonMoaOptionsStore(path).LoadAsync();
            Assert.Equal("fast", loaded.Router!.ProviderId);
            Assert.Null(loaded.Router.ModelId);
            Assert.Equal("plan-model", loaded.Planner!.ModelId);
            Assert.Equal(3, loaded.EnsembleSize);
            Assert.Equal(0.5, loaded.MaxUsdPerTurn);
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
    }
}
