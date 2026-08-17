using System.Linq;
using AeroAgent.Moa.Profiles;
using AeroCode.Tests.ConversationTests;
using Xunit;

namespace AeroCode.Tests.MoaTests;

/// <summary>模型分配器：强项匹配 / 速度偏好 / 成本偏好 / 回退排除 / 空候选。</summary>
public sealed class ModelAssignerTests : MoaTestBase
{
    [Fact]
    public void Assign_StrengthMatch_BeatsGeneralFallback()
    {
        AddProvider("generalist");
        SetProfile("generalist", new[] { ModelStrength.General });
        AddProvider("coder");
        SetProfile("coder", new[] { ModelStrength.Code });

        var assignment = Assigner.Assign(ModelStrength.Code);

        Assert.NotNull(assignment);
        Assert.Equal("coder", assignment!.ProviderId);
        Assert.Equal("scripted-model", assignment.ModelId); // TestProviderRegistry 的默认模型
    }

    [Fact]
    public void Assign_ExcludedKeys_FallsBackToNextCandidate()
    {
        AddProvider("coder");
        SetProfile("coder", new[] { ModelStrength.Code });
        AddProvider("generalist");
        SetProfile("generalist", new[] { ModelStrength.General });

        var first = Assigner.Assign(ModelStrength.Code);
        Assert.Equal("coder", first!.ProviderId);

        var fallback = Assigner.Assign(ModelStrength.Code, new[] { first.Key });
        Assert.NotNull(fallback);
        Assert.Equal("generalist", fallback!.ProviderId);

        var none = Assigner.Assign(ModelStrength.Code, new[] { first.Key, fallback.Key });
        Assert.Null(none);
    }

    [Fact]
    public void Assign_SpeedPreference_BoostsFastTier()
    {
        // 两个同为 general 强项的 provider，一个 Fast 一个 Slow。
        AddProvider("slow");
        SetProfile("slow", new[] { ModelStrength.General }, speed: SpeedTier.Slow);
        AddProvider("fast");
        SetProfile("fast", new[] { ModelStrength.General }, speed: SpeedTier.Fast);

        var assignment = Assigner.Assign(ModelStrength.General, preferSpeed: SpeedTier.Fast);
        Assert.Equal("fast", assignment!.ProviderId);
    }

    [Fact]
    public void Assign_CheaperKnownCost_Wins_Tie()
    {
        AddProvider("cheap");
        SetProfile("cheap", new[] { ModelStrength.General }, costPerMIn: 0.1, costPerMOut: 0.2);
        AddProvider("pricey");
        SetProfile("pricey", new[] { ModelStrength.General }, costPerMIn: 5.0, costPerMOut: 15.0);

        var assignment = Assigner.Assign(ModelStrength.General);
        Assert.Equal("cheap", assignment!.ProviderId);
    }

    [Fact]
    public void Assign_UnknownCost_NotPenalized()
    {
        // 一个已知价、一个未知价，同强项：未知价不奖不罚，
        // 平分时按 providerId 字典序确定——"aaa" 在前。
        AddProvider("aaa");
        SetProfile("aaa", new[] { ModelStrength.General }); // 未知价
        AddProvider("zzz");
        SetProfile("zzz", new[] { ModelStrength.General }, costPerMIn: 1, costPerMOut: 1);

        var assignment = Assigner.Assign(ModelStrength.General);
        Assert.Equal("aaa", assignment!.ProviderId);
    }

    [Fact]
    public void Assign_NoConfiguredProviders_ReturnsNull()
    {
        Assert.Null(Assigner.Assign(ModelStrength.Code));
        Assert.Empty(Assigner.RankCandidates(ModelStrength.Code));
    }

    [Fact]
    public void RankCandidates_DeterministicOrder_OnEqualScores()
    {
        AddProvider("b");
        SetProfile("b", new[] { ModelStrength.General });
        AddProvider("a");
        SetProfile("a", new[] { ModelStrength.General });
        AddProvider("c");
        SetProfile("c", new[] { ModelStrength.General });

        var ranked = Assigner.RankCandidates(ModelStrength.General);
        Assert.Equal(new[] { "a", "b", "c" }, ranked.Select(r => r.ProviderId).ToArray());
    }

    [Fact]
    public void Assign_ExplicitModelProfile_IsCandidate()
    {
        AddProvider("p");
        SetProfile("p", new[] { ModelStrength.General });
        // 用户显式添加的具名模型画像（code 强项）也进入候选集。
        Catalog.Upsert(new ModelProfile
        {
            ProviderId = "p",
            ModelId = "special-code-model",
            Strengths = { ModelStrength.Code },
        });

        var assignment = Assigner.Assign(ModelStrength.Code);
        Assert.NotNull(assignment);
        Assert.Equal("p", assignment!.ProviderId);
        Assert.Equal("special-code-model", assignment.ModelId);
    }
}
