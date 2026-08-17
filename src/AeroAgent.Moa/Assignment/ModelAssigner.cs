using System;
using System.Collections.Generic;
using System.Linq;
using AeroAgent.Moa.Profiles;
using AeroCode.AI.Providers;

namespace AeroAgent.Moa.Assignment;

/// <summary>一次模型分配结果：provider + 已解析的具体模型 + 画像。</summary>
public sealed record ModelAssignment(string ProviderId, string ModelId, ModelProfile Profile)
{
    public string Key => $"{ProviderId}::{ModelId}";
}

/// <summary>
/// 模型分配器：把"需要的强项"映射到已配置的最优模型。
/// 候选集 = 每个已配置 provider 的默认模型 + 画像目录中该 provider 的具名模型。
/// 打分 = 强项匹配 &gt; 速度偏好 &gt; 可靠性/延迟（自学习）&gt; 成本（仅已知价格参与）。
/// 同分按 providerId/modelId 字典序，保证确定性。
/// </summary>
public sealed class ModelAssigner
{
    private readonly IProviderRegistry _registry;
    private readonly IModelProfileCatalog _catalog;

    public ModelAssigner(IProviderRegistry registry, IModelProfileCatalog catalog)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    /// <summary>
    /// 按强项对候选模型排序（最优在前）。
    /// </summary>
    /// <param name="strength">需要的强项（<see cref="ModelStrength"/>）。</param>
    /// <param name="excludedKeys">排除的 provider::model 键（回退链用）。</param>
    /// <param name="preferSpeed">速度偏好（router/judge 场景偏好 Fast）。</param>
    public IReadOnlyList<ModelAssignment> RankCandidates(
        string strength,
        IReadOnlyCollection<string>? excludedKeys = null,
        SpeedTier? preferSpeed = null)
    {
        var target = ModelStrength.Normalize(strength);
        var excluded = excludedKeys ?? Array.Empty<string>();
        var candidates = EnumerateCandidates()
            .Where(c => !excluded.Contains(c.Key))
            .ToList();

        // 成本归一化只在"已知价格"的候选间进行；未知价格不奖不罚。
        var knownCosts = candidates
            .Select(c => KnownUnitCost(c.Profile))
            .Where(c => c is not null)
            .Select(c => c!.Value)
            .ToList();
        var maxCost = knownCosts.Count > 0 ? knownCosts.Max() : 0.0;
        var maxLatency = candidates.Count > 0 ? candidates.Max(c => c.Profile.Stats.AvgLatencyMs) : 0.0;

        var scored = candidates
            .Select(c => (Candidate: c, Score: Score(c, target, preferSpeed, maxCost, maxLatency)))
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Candidate.ProviderId, StringComparer.Ordinal)
            .ThenBy(x => x.Candidate.ModelId, StringComparer.Ordinal)
            .Select(x => x.Candidate)
            .ToList();

        return scored;
    }

    /// <summary>取最优候选；无候选返回 null（调用方如实报告）。</summary>
    public ModelAssignment? Assign(
        string strength,
        IReadOnlyCollection<string>? excludedKeys = null,
        SpeedTier? preferSpeed = null) =>
        RankCandidates(strength, excludedKeys, preferSpeed).FirstOrDefault();

    private IEnumerable<ModelAssignment> EnumerateCandidates()
    {
        foreach (var providerId in _registry.ListConfiguredIds())
        {
            if (!_registry.TryGetConfig(providerId, out var config))
            {
                continue;
            }

            // 每个已配置 provider 的默认模型都是候选。
            var defaultModel = config.DefaultModel;
            if (!string.IsNullOrEmpty(defaultModel))
            {
                yield return new ModelAssignment(
                    providerId, defaultModel, _catalog.GetOrAddDefault(providerId, defaultModel));
            }

            // 画像目录中该 provider 的具名模型（用户显式添加的）也是候选。
            foreach (var profile in _catalog.List()
                         .Where(p => p.ProviderId == providerId && p.ModelId.Length > 0))
            {
                yield return new ModelAssignment(providerId, profile.ModelId, profile);
            }
        }
    }

    private static double Score(
        ModelAssignment candidate,
        string targetStrength,
        SpeedTier? preferSpeed,
        double maxKnownCost,
        double maxLatency)
    {
        var profile = candidate.Profile;
        var strengths = profile.Strengths.Select(ModelStrength.Normalize).ToHashSet();
        var score = 0.0;

        if (strengths.Contains(targetStrength))
        {
            score += 100;
        }
        else if (strengths.Contains(ModelStrength.General))
        {
            score += 25; // general 画像可兜底任何任务
        }

        if (preferSpeed is { } prefer)
        {
            if (profile.SpeedTier == prefer)
            {
                score += 40;
            }
            else if (prefer == SpeedTier.Fast && profile.SpeedTier == SpeedTier.Slow)
            {
                score -= 20;
            }
        }

        // 可靠性与延迟：样本足够才参与（避免冷启动误判）。
        if (profile.Stats.Calls >= 3)
        {
            score -= 30 * profile.Stats.FailureRate;
            if (maxLatency > 0)
            {
                score += 10 * (1 - profile.Stats.AvgLatencyMs / maxLatency);
            }
        }

        // 成本：仅已知价格参与，越便宜越优。
        if (maxKnownCost > 0 && KnownUnitCost(profile) is { } cost)
        {
            score += 15 * (1 - cost / maxKnownCost);
        }

        return score;
    }

    /// <summary>画像已知价格时给出单位参考成本（输入价+输出价），否则 null。</summary>
    private static double? KnownUnitCost(ModelProfile profile) =>
        profile.CostPerMIn is { } i && profile.CostPerMOut is { } o ? i + o : null;
}
