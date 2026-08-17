using System;
using AeroAgent.Moa.Assignment;
using AeroAgent.Moa.Profiles;
using AeroCode.AI.Providers;

namespace AeroAgent.Moa.Strategies;

/// <summary>
/// 角色模型解析器：显式绑定（MoaOptions）优先，绑定缺失/未配置时
/// 回退 ModelAssigner 按画像自动分配。两级都不成则返回 null（调用方如实报告）。
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

    public ModelAssignment? Resolve(ModelBinding? binding, string strength, SpeedTier? preferSpeed = null)
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

        return _assigner.Assign(strength, null, preferSpeed);
    }
}
