using System;
using System.Collections.Generic;
using System.Linq;
using AeroAgent.Moa.Profiles;

namespace AeroAgent.Moa.Assignment;

/// <summary>成本排序判定命中了哪一层（B1 四层顺序钉死：①长上下文 → ②缓存折扣 → ③batch 兜底 → ④峰值档）。</summary>
public enum CostTier
{
    /// <summary>无成本层命中：按既有强项/速度/可靠性/成本打分选出（现行为）。</summary>
    None = 0,

    /// <summary>①长上下文优先：估算输入超过阈值，限定 ContextWindow 装得下的候选。</summary>
    LongContext = 1,

    /// <summary>②缓存折扣：请求含可缓存前缀，按厂商缓存系数折后成本排序。</summary>
    CacheDiscount = 2,

    /// <summary>③batch 兜底：允许批量（非实时），按 batch 折扣排序。</summary>
    Batch = 3,

    /// <summary>④峰值档：请求峰值推理，按峰值溢价计入成本排序。</summary>
    PeakTier = 4,
}

/// <summary>请求侧上下文：驱动四层判定的输入（全部为请求事实，不含密钥）。</summary>
public sealed record CostRequestContext
{
    /// <summary>估算输入 token 数；null = 未知（①层不触发）。</summary>
    public int? EstimatedInputTokens { get; init; }

    /// <summary>请求是否含稳定可缓存前缀（②层开关）。</summary>
    public bool CacheFriendly { get; init; }

    /// <summary>是否允许 batch（非实时，③层开关）。</summary>
    public bool BatchAllowed { get; init; }

    /// <summary>是否请求峰值推理档（④层开关；峰值档"实际可用性"由 B4 EffortProfile 经探测裁决）。</summary>
    public bool PeakRequested { get; init; }
}

/// <summary>厂商缓存折扣系数规则（对 providerId 做不区分大小写包含匹配，先命中先用）。</summary>
public sealed record VendorCacheRule
{
    /// <summary>厂商家族匹配串（如 "anthropic"/"claude"/"gemini"/"openai"）。</summary>
    public required string Family { get; init; }

    /// <summary>缓存场景下单位成本倍率：&lt;1 = 折扣（G/O），&gt;1 = 写溢价（A）。</summary>
    public required double CostMultiplier { get; init; }

    /// <summary>口径说明（进理由字段，保证可解释）。</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>研究口径默认：A 缓存写 1.25×（1h 档 2.0×，排序取保守低档）/ G implicit 共享前缀 75% 折扣（付 25%）/ O off-peak 0.5×。</summary>
    public static readonly IReadOnlyList<VendorCacheRule> Defaults = new[]
    {
        new VendorCacheRule
        {
            Family = "anthropic",
            CostMultiplier = 1.25,
            Label = "A(Anthropic) 缓存写 5m 档 1.25×（1h 档 2.0×，排序取保守低档）",
        },
        new VendorCacheRule { Family = "claude", CostMultiplier = 1.25, Label = "A(Claude) 缓存写 5m 档 1.25×" },
        new VendorCacheRule { Family = "gemini", CostMultiplier = 0.25, Label = "G(Gemini) implicit 共享前缀 75% 折扣" },
        new VendorCacheRule { Family = "openai", CostMultiplier = 0.5, Label = "O(OpenAI) off-peak 0.5×" },
    };
}

/// <summary>四层判定的阈值与价格参数（可配置数据；默认 = 研究口径）。</summary>
public sealed record CostTierOptions
{
    /// <summary>①长上下文 token 阈值（估算输入超过才触发）。</summary>
    public int LongContextTokenThreshold { get; init; } = 100_000;

    /// <summary>②各厂商缓存折扣系数（显式配置整体替换默认表）。</summary>
    public IReadOnlyList<VendorCacheRule> CacheRules { get; init; } = VendorCacheRule.Defaults;

    /// <summary>③batch 折扣倍率（OpenAI batch 半价口径）。</summary>
    public double BatchDiscountMultiplier { get; init; } = 0.5;

    /// <summary>④峰值档溢价倍率；研究口径未给数值，默认 1.0 = 不扭曲排序、仅标注口径（可配置上调）。</summary>
    public double PeakPremiumMultiplier { get; init; } = 1.0;

    public static CostTierOptions Default { get; } = new();
}

/// <summary>四层判定的候选输入：分配候选 + 既有打分（作同成本平局裁决）+ 该层折后单位成本（/M token）。</summary>
public sealed record CostCandidate(ModelAssignment Assignment, double Score, double? EffectiveCost);

/// <summary>单层判定结果：是否命中 + 命中时的最优候选与理由。</summary>
public sealed record TierVerdict(CostTier Tier, bool Applied, ModelAssignment? Pick, string Reason)
{
    public static TierVerdict Miss(CostTier tier, string reason) => new(tier, false, null, reason);

    public static TierVerdict Hit(CostTier tier, ModelAssignment pick, string reason) =>
        new(tier, true, pick, reason);
}

/// <summary>成本排序选模（B1）结果：选出的候选 + 命中层 + 可解释理由（选了哪层、为什么）。</summary>
public sealed record CostTierDecision(ModelAssignment? Assignment, CostTier Tier, string Reason);

/// <summary>
/// B1 成本排序四层判定。顺序钉死：①长上下文优先 → ②缓存折扣 → ③batch 兜底 → ④峰值档，
/// 首个命中的层决定选择与理由；四层都未命中时由调用方回落既有打分行为。
/// 每层都是纯函数：同样的输入永远得到同样的判定，可独立单测。
/// </summary>
public static class CostTiers
{
    /// <summary>①长上下文优先：估算输入超阈值时，限定 ContextWindow 装得下的候选，按成本排序。</summary>
    public static TierVerdict LongContext(
        IReadOnlyList<CostCandidate> candidates, CostRequestContext request, CostTierOptions options)
    {
        var tokens = request.EstimatedInputTokens;
        if (tokens is not { } t)
        {
            return TierVerdict.Miss(CostTier.LongContext, "估算输入 token 未知，①长上下文层不触发。");
        }

        if (t <= options.LongContextTokenThreshold)
        {
            return TierVerdict.Miss(
                CostTier.LongContext,
                $"估算输入 {t} token ≤ 长上下文阈值 {options.LongContextTokenThreshold}，①层不触发。");
        }

        var eligible = candidates.Where(c => c.Assignment.Profile.ContextWindow >= t).ToList();
        if (eligible.Count == 0)
        {
            return TierVerdict.Miss(
                CostTier.LongContext,
                $"长上下文命中（{t} > {options.LongContextTokenThreshold}）但无候选 ContextWindow≥{t}，落至下层判定。");
        }

        var pick = ByCostOrder(eligible).First();
        return TierVerdict.Hit(
            CostTier.LongContext,
            pick.Assignment,
            $"①长上下文优先：估算输入 {t} token > 阈值 {options.LongContextTokenThreshold}，" +
            $"限定 ContextWindow≥{t} 的 {eligible.Count} 个候选，按成本排序选出 {pick.Assignment.Key}{CostNote(pick)}。");
    }

    /// <summary>②缓存折扣：请求含可缓存前缀时，按厂商缓存系数折后成本排序。</summary>
    public static TierVerdict CacheDiscount(
        IReadOnlyList<CostCandidate> candidates, CostRequestContext request, CostTierOptions options)
    {
        if (!request.CacheFriendly)
        {
            return TierVerdict.Miss(CostTier.CacheDiscount, "请求无可缓存前缀，②缓存折扣层不触发。");
        }

        var priced = candidates
            .Select(c => c with
            {
                EffectiveCost = CostTiers.UnitCost(c.Assignment.Profile) is { } cost
                    ? cost * (MatchRule(options.CacheRules, c.Assignment.ProviderId)?.CostMultiplier ?? 1.0)
                    : null,
            })
            .ToList();

        var pick = ByCostOrder(priced).First();
        var rule = MatchRule(options.CacheRules, pick.Assignment.ProviderId);
        var basis = rule is null
            ? "无匹配厂商缓存系数，按基准成本"
            : $"命中 {rule.Label}（{rule.CostMultiplier}×）";
        return TierVerdict.Hit(
            CostTier.CacheDiscount,
            pick.Assignment,
            $"②缓存折扣：请求含可缓存前缀，{pick.Assignment.Key} {basis}，按折后成本排序胜出{CostNote(pick)}。");
    }

    /// <summary>③batch 兜底：允许批量（非实时）时按 batch 折扣排序。</summary>
    public static TierVerdict Batch(
        IReadOnlyList<CostCandidate> candidates, CostRequestContext request, CostTierOptions options)
    {
        if (!request.BatchAllowed)
        {
            return TierVerdict.Miss(CostTier.Batch, "请求不允许 batch，③层不触发。");
        }

        var priced = candidates
            .Select(c => c with
            {
                EffectiveCost = CostTiers.UnitCost(c.Assignment.Profile) is { } cost
                    ? cost * options.BatchDiscountMultiplier
                    : null,
            })
            .ToList();

        var pick = ByCostOrder(priced).First();
        return TierVerdict.Hit(
            CostTier.Batch,
            pick.Assignment,
            $"③batch 兜底：允许批量（非实时），按 batch 折扣 {options.BatchDiscountMultiplier}× " +
            $"折后成本排序选出 {pick.Assignment.Key}{CostNote(pick)}。");
    }

    /// <summary>④峰值档：请求峰值推理时按峰值溢价计入成本排序（溢价默认 1.0 = 仅标注口径）。</summary>
    public static TierVerdict Peak(
        IReadOnlyList<CostCandidate> candidates, CostRequestContext request, CostTierOptions options)
    {
        if (!request.PeakRequested)
        {
            return TierVerdict.Miss(CostTier.PeakTier, "未请求峰值推理档，④层不触发。");
        }

        var priced = candidates
            .Select(c => c with
            {
                EffectiveCost = CostTiers.UnitCost(c.Assignment.Profile) is { } cost
                    ? cost * options.PeakPremiumMultiplier
                    : null,
            })
            .ToList();

        var pick = ByCostOrder(priced).First();
        return TierVerdict.Hit(
            CostTier.PeakTier,
            pick.Assignment,
            $"④峰值档：请求峰值推理，溢价 {options.PeakPremiumMultiplier}× 计入成本排序，" +
            $"选出 {pick.Assignment.Key}{CostNote(pick)}（峰值档实际可用性由 EffortProfile 探测裁决）。");
    }

    /// <summary>按 providerId 匹配厂商规则（不区分大小写包含匹配，先命中先用；无匹配 null）。</summary>
    public static VendorCacheRule? MatchRule(IReadOnlyList<VendorCacheRule> rules, string providerId)
    {
        foreach (var rule in rules)
        {
            if (providerId.Contains(rule.Family, StringComparison.OrdinalIgnoreCase))
            {
                return rule;
            }
        }

        return null;
    }

    /// <summary>画像已知价格时给出单位参考成本（输入价+输出价，/M token），否则 null。</summary>
    internal static double? UnitCost(ModelProfile profile) =>
        profile.CostPerMIn is { } i && profile.CostPerMOut is { } o ? i + o : null;

    /// <summary>
    /// 成本排序：已知折后成本升序在前，未知成本排后（成本排序无法裁决未知价）；
    /// 同成本按既有得分降序，再按 providerId/modelId 字典序，保证确定性。
    /// </summary>
    private static IOrderedEnumerable<CostCandidate> ByCostOrder(IEnumerable<CostCandidate> candidates) =>
        candidates
            .OrderBy(c => c.EffectiveCost ?? double.PositiveInfinity)
            .ThenByDescending(c => c.Score)
            .ThenBy(c => c.Assignment.ProviderId, StringComparer.Ordinal)
            .ThenBy(c => c.Assignment.ModelId, StringComparer.Ordinal);

    private static string CostNote(CostCandidate pick) =>
        pick.EffectiveCost is { } cost
            ? $"（折后单位成本 {cost:0.###}/M token）"
            : "（成本未知，按既有得分/字典序裁决）";
}
