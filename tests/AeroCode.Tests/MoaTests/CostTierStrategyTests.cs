using System.Linq;
using AeroAgent.Moa.Assignment;
using AeroAgent.Moa.Profiles;
using AeroCode.Tests.ConversationTests;
using Xunit;

namespace AeroCode.Tests.MoaTests;

/// <summary>
/// B1 成本排序四层判定：①长上下文 → ②缓存折扣 → ③batch 兜底 → ④峰值档，
/// 顺序钉死；理由字段非空可解释；显式配置优先于默认参数。
/// </summary>
public sealed class CostTierStrategyTests : MoaTestBase
{
    [Fact]
    public void Decide_LongContext_LimitsToEligibleWindow_PicksCheapestEligible()
    {
        AddProvider("cheap-small");
        SetProfile("cheap-small", new[] { ModelStrength.General }, costPerMIn: 0.1, costPerMOut: 0.1)
            .ContextWindow = 32_000;
        AddProvider("pricey-long");
        SetProfile("pricey-long", new[] { ModelStrength.General }, costPerMIn: 3.0, costPerMOut: 3.0)
            .ContextWindow = 200_000;

        var decision = Assigner.Decide(
            new CostRequestContext { EstimatedInputTokens = 128_000 }, ModelStrength.General);

        Assert.Equal(CostTier.LongContext, decision.Tier);
        Assert.NotNull(decision.Assignment);
        Assert.Equal("pricey-long", decision.Assignment!.ProviderId); // 便宜的装不下，只能选装得下的
        Assert.Contains("长上下文", decision.Reason);
        Assert.False(string.IsNullOrWhiteSpace(decision.Reason));
    }

    [Fact]
    public void Decide_LongContext_NoEligible_FallsThroughToBaselineScore()
    {
        AddProvider("cheap-small");
        SetProfile("cheap-small", new[] { ModelStrength.General }, costPerMIn: 0.1, costPerMOut: 0.1)
            .ContextWindow = 32_000;
        AddProvider("mid");
        SetProfile("mid", new[] { ModelStrength.General }, costPerMIn: 1.0, costPerMOut: 1.0)
            .ContextWindow = 64_000;

        // 两候选都装不下 128k → ①不命中，其余层未请求 → 回落既有打分（便宜的胜出）
        var decision = Assigner.Decide(
            new CostRequestContext { EstimatedInputTokens = 128_000 }, ModelStrength.General);

        Assert.Equal(CostTier.None, decision.Tier);
        Assert.Equal("cheap-small", decision.Assignment!.ProviderId);
        Assert.Contains("四层均未命中", decision.Reason);
    }

    [Fact]
    public void Decide_LongContext_BeatsCacheDiscount_OrderPinned()
    {
        AddProvider("openai-long");
        SetProfile("openai-long", new[] { ModelStrength.General }, costPerMIn: 1.0, costPerMOut: 1.0)
            .ContextWindow = 200_000;
        AddProvider("openai-short");
        SetProfile("openai-short", new[] { ModelStrength.General }, costPerMIn: 0.2, costPerMOut: 0.2)
            .ContextWindow = 8_000;

        // 同时命中①和②：顺序钉死 ①长上下文优先（128k > 默认阈值 100k）
        var decision = Assigner.Decide(
            new CostRequestContext { EstimatedInputTokens = 128_000, CacheFriendly = true },
            ModelStrength.General);

        Assert.Equal(CostTier.LongContext, decision.Tier);
        Assert.Equal("openai-long", decision.Assignment!.ProviderId);
    }

    [Fact]
    public void Decide_CacheDiscount_VendorMultiplier_ReordersByEffectiveCost()
    {
        AddProvider("anthropic");
        SetProfile("anthropic", new[] { ModelStrength.General }, costPerMIn: 0.25, costPerMOut: 0.25);
        AddProvider("openai");
        SetProfile("openai", new[] { ModelStrength.General }, costPerMIn: 0.5, costPerMOut: 0.5);

        // 缓存语义：anthropic 0.5×1.25=0.625 > openai 1.0×0.5=0.5 → openai 胜
        var cached = Assigner.Decide(
            new CostRequestContext { CacheFriendly = true }, ModelStrength.General);

        Assert.Equal(CostTier.CacheDiscount, cached.Tier);
        Assert.Equal("openai", cached.Assignment!.ProviderId);
        Assert.Contains("缓存折扣", cached.Reason);
        Assert.Contains("0.5×", cached.Reason);
        Assert.False(string.IsNullOrWhiteSpace(cached.Reason));
    }

    [Fact]
    public void Decide_Batch_AppliesBatchDiscount()
    {
        AddProvider("expensive");
        SetProfile("expensive", new[] { ModelStrength.General }, costPerMIn: 1.0, costPerMOut: 1.0);
        AddProvider("cheap");
        SetProfile("cheap", new[] { ModelStrength.General }, costPerMIn: 0.1, costPerMOut: 0.1);

        var decision = Assigner.Decide(
            new CostRequestContext { BatchAllowed = true }, ModelStrength.General);

        Assert.Equal(CostTier.Batch, decision.Tier);
        Assert.Equal("cheap", decision.Assignment!.ProviderId);
        Assert.Contains("batch", decision.Reason);
        Assert.False(string.IsNullOrWhiteSpace(decision.Reason));
    }

    [Fact]
    public void Decide_PeakRequested_FiresPeakTier()
    {
        AddProvider("aaa");
        SetProfile("aaa", new[] { ModelStrength.General }, costPerMIn: 1.0, costPerMOut: 1.0);
        AddProvider("zzz");
        SetProfile("zzz", new[] { ModelStrength.General }, costPerMIn: 0.2, costPerMOut: 0.2);

        var decision = Assigner.Decide(
            new CostRequestContext { PeakRequested = true }, ModelStrength.General);

        Assert.Equal(CostTier.PeakTier, decision.Tier);
        Assert.Equal("zzz", decision.Assignment!.ProviderId);
        Assert.Contains("峰值档", decision.Reason);
        Assert.False(string.IsNullOrWhiteSpace(decision.Reason));
    }

    [Fact]
    public void Decide_PeakRequested_WithCacheFriendly_CacheWinsByPinnedOrder()
    {
        AddProvider("openai");
        SetProfile("openai", new[] { ModelStrength.General }, costPerMIn: 0.5, costPerMOut: 0.5);

        // ②先于④：缓存命中即出，轮不到峰值档
        var decision = Assigner.Decide(
            new CostRequestContext { CacheFriendly = true, PeakRequested = true },
            ModelStrength.General);

        Assert.Equal(CostTier.CacheDiscount, decision.Tier);
    }

    [Fact]
    public void Decide_NoLayerFired_MatchesBaselineAssign_WithReason()
    {
        AddProvider("coder");
        SetProfile("coder", new[] { ModelStrength.Code }, costPerMIn: 5.0, costPerMOut: 5.0);
        AddProvider("generalist");
        SetProfile("generalist", new[] { ModelStrength.General });

        var decision = Assigner.Decide(new CostRequestContext(), ModelStrength.Code);
        var baseline = Assigner.Assign(ModelStrength.Code); // 现行为

        Assert.Equal(CostTier.None, decision.Tier);
        Assert.NotNull(baseline);
        Assert.Equal(baseline!.Key, decision.Assignment!.Key); // 与基线一致
        Assert.False(string.IsNullOrWhiteSpace(decision.Reason));
    }

    [Fact]
    public void Decide_ExplicitOptions_OverrideDefaultThreshold()
    {
        AddProvider("mid");
        SetProfile("mid", new[] { ModelStrength.General }, costPerMIn: 1.0, costPerMOut: 1.0)
            .ContextWindow = 8_000;

        // 默认阈值 100k：2k 不触发①
        var withDefaults = Assigner.Decide(
            new CostRequestContext { EstimatedInputTokens = 2_000 }, ModelStrength.General);
        Assert.Equal(CostTier.None, withDefaults.Tier);

        // 显式配置阈值 1k：同样的 2k 触发①（显式配置优先于默认）
        var explicitOptions = Assigner.Decide(
            new CostRequestContext { EstimatedInputTokens = 2_000 },
            ModelStrength.General,
            options: new CostTierOptions { LongContextTokenThreshold = 1_000 });
        Assert.Equal(CostTier.LongContext, explicitOptions.Tier);
        Assert.Equal("mid", explicitOptions.Assignment!.ProviderId);
    }

    [Fact]
    public void Decide_ExplicitCacheRules_OverrideDefaultTable()
    {
        AddProvider("acme");
        SetProfile("acme", new[] { ModelStrength.General }, costPerMIn: 1.0, costPerMOut: 1.0);
        AddProvider("openai");
        SetProfile("openai", new[] { ModelStrength.General }, costPerMIn: 0.5, costPerMOut: 0.5);

        // 默认表：openai 折后 0.5 < acme 2.0（无规则）→ openai 胜
        var withDefaults = Assigner.Decide(
            new CostRequestContext { CacheFriendly = true }, ModelStrength.General);
        Assert.Equal("openai", withDefaults.Assignment!.ProviderId);

        // 显式规则表整体替换默认表：acme 0.1× → 0.2 < openai 0.5 → acme 胜
        var withCustom = Assigner.Decide(
            new CostRequestContext { CacheFriendly = true },
            ModelStrength.General,
            options: new CostTierOptions
            {
                CacheRules = new[]
                {
                    new VendorCacheRule { Family = "acme", CostMultiplier = 0.1, Label = "acme 超折" },
                },
            });
        Assert.Equal(CostTier.CacheDiscount, withCustom.Tier);
        Assert.Equal("acme", withCustom.Assignment!.ProviderId);
        Assert.Contains("acme 超折", withCustom.Reason);
    }

    [Fact]
    public void Decide_NoCandidates_ReturnsNullAssignment_WithReason()
    {
        var decision = Assigner.Decide(
            new CostRequestContext { PeakRequested = true }, ModelStrength.General);

        Assert.Null(decision.Assignment);
        Assert.Equal(CostTier.None, decision.Tier);
        Assert.False(string.IsNullOrWhiteSpace(decision.Reason));
    }

    [Fact]
    public void Decide_CacheFriendly_UnknownCosts_StillDecidesByScore()
    {
        AddProvider("aaa");
        SetProfile("aaa", new[] { ModelStrength.General }); // 成本未知
        AddProvider("bbb");
        SetProfile("bbb", new[] { ModelStrength.General }); // 成本未知

        var decision = Assigner.Decide(
            new CostRequestContext { CacheFriendly = true }, ModelStrength.General);

        // ②命中但成本全未知：不凭空估算，按既有得分/字典序裁决，理由如实说明
        Assert.Equal(CostTier.CacheDiscount, decision.Tier);
        Assert.Equal("aaa", decision.Assignment!.ProviderId);
        Assert.Contains("成本未知", decision.Reason);
    }

    [Fact]
    public void MatchRule_FamilyMatch_CaseInsensitive_FirstMatchWins()
    {
        var rules = new[]
        {
            new VendorCacheRule { Family = "claude", CostMultiplier = 1.25 },
            new VendorCacheRule { Family = "open", CostMultiplier = 0.4 },
        };

        Assert.Equal(1.25, CostTiers.MatchRule(rules, "My-Claude-Proxy")!.CostMultiplier);
        Assert.Equal(0.4, CostTiers.MatchRule(rules, "OPENAI")!.CostMultiplier);
        Assert.Null(CostTiers.MatchRule(rules, "deepseek"));
    }
}
