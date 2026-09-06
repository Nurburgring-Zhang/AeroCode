using System;
using AeroAgent.Moa.Assignment;
using AeroAgent.Moa.Profiles;
using AeroCode.AI.Providers;

namespace AeroAgent.Moa.Strategies;

/// <summary>
/// 角色模型解析器：显式绑定（MoaOptions）优先，绑定缺失/未配置时
/// 回退 ModelAssigner 按画像自动分配。两级都不成则返回 null（调用方如实报告）。
/// B1 成本排序：request 上下文（可选）原样透传给 ModelAssigner 四层判定；null = 现行为。
/// </summary>
public sealed class ModelResolver
{
    private readonly IProviderRegistry _registry;
    private readonly IModelProfileCatalog _catalog;
    private readonly ModelAssigner _assigner;

    public ModelResolver(IProviderRegistry registry, IModelProfileCatalog catalog, ModelAssigner assigner)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _assigner = assigner ?? throw new ArgumentNullException(nameof(assigner));
    }

    public ModelAssignment? Resolve(
        ModelBinding? binding,
        string strength,
        SpeedTier? preferSpeed = null,
        CostRequestContext? request = null,
        CostTierOptions? options = null)
    {
        if (binding is not null &&
            _registry.TryGetConfig(binding.ProviderId, out var config))
        {
            var modelId = string.IsNullOrWhiteSpace(binding.ModelId)
                ? config.DefaultModel
                : binding.ModelId!;
            if (!string.IsNullOrEmpty(modelId))
            {
                return new ModelAssignment(
                    binding.ProviderId, modelId,
                    _catalog.GetOrAddDefault(binding.ProviderId, modelId));
            }
        }

        return _assigner.Assign(strength, null, preferSpeed, request, options);
    }

    /// <summary>
    /// 同 <see cref="Resolve"/>，但返回带命中层与理由的完整判定（理由字段的消费口）。
    /// 显式绑定生效时不走四层判定（Tier=None + 理由说明）；无 request 时按空上下文走打分回落。
    /// </summary>
    public CostTierDecision? ResolveDetailed(
        ModelBinding? binding,
        string strength,
        SpeedTier? preferSpeed = null,
        CostRequestContext? request = null,
        CostTierOptions? options = null)
    {
        if (binding is not null &&
            _registry.TryGetConfig(binding.ProviderId, out var config))
        {
            var modelId = string.IsNullOrWhiteSpace(binding.ModelId)
                ? config.DefaultModel
                : binding.ModelId!;
            if (!string.IsNullOrEmpty(modelId))
            {
                return new CostTierDecision(
                    new ModelAssignment(
                        binding.ProviderId, modelId,
                        _catalog.GetOrAddDefault(binding.ProviderId, modelId)),
                    CostTier.None,
                    "显式绑定生效：未走成本排序四层判定。");
            }
        }

        return _assigner.Decide(request ?? new CostRequestContext(), strength, null, preferSpeed, options);
    }
}
