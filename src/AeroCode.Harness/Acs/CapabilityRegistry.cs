// Copyright (c) AeroCode
// CapabilityRegistry — ACS v2.3.0 能力注册表。
// 登记 agent 终端的能力清单（分层 layers + 覆盖域 coverage），
// 供能力覆盖核对（capability-coverage）与治理规则（governance）消费。
namespace AeroCode.Harness.Acs;

/// <summary>能力分层（对齐 ACS capability-registry.layers）。</summary>
public enum AcsCapabilityLayer
{
    /// <summary>Harness 层（边界/门禁）。</summary>
    Harness,

    /// <summary>Graph 层（任务组织）。</summary>
    Graph,

    /// <summary>Loop 层（循环控制）。</summary>
    Loop,

    /// <summary>Context 层（上下文构造）。</summary>
    Context,

    /// <summary>Prompt 层（提示词组织）。</summary>
    Prompt,
}

/// <summary>登记的能力项。</summary>
public sealed record AcsCapability(
    string Id,
    string Name,
    AcsCapabilityLayer Layer,
    string CoverageDomain,
    string Status);

/// <summary>
/// 能力注册表：登记/查询能力，校验覆盖域不重复登记。
/// </summary>
public sealed class CapabilityRegistry
{
    private readonly object _lock = new();
    private readonly Dictionary<string, AcsCapability> _capabilities = new(StringComparer.Ordinal);

    /// <summary>当前全部能力（只读快照）。</summary>
    public IReadOnlyList<AcsCapability> Capabilities
    {
        get { lock (_lock) return _capabilities.Values.ToArray(); }
    }

    /// <summary>登记能力。重复 id = 拒绝。</summary>
    public (bool Accepted, string? Error) Register(AcsCapability capability)
    {
        ArgumentNullException.ThrowIfNull(capability);
        lock (_lock)
        {
            if (_capabilities.ContainsKey(capability.Id))
            {
                return (false, $"能力 id 重复：{capability.Id}");
            }

            if (string.IsNullOrWhiteSpace(capability.Name) ||
                string.IsNullOrWhiteSpace(capability.CoverageDomain))
            {
                return (false, $"能力 {capability.Id} 缺名称或覆盖域");
            }

            _capabilities[capability.Id] = capability;
            return (true, null);
        }
    }

    /// <summary>按层查询能力。</summary>
    public IReadOnlyList<AcsCapability> ByLayer(AcsCapabilityLayer layer)
    {
        lock (_lock)
        {
            return _capabilities.Values.Where(c => c.Layer == layer).ToArray();
        }
    }

    /// <summary>覆盖域清单（去重）。</summary>
    public IReadOnlyList<string> CoverageDomains()
    {
        lock (_lock)
        {
            return _capabilities.Values.Select(c => c.CoverageDomain)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }

    /// <summary>清空（新任务开始）。</summary>
    public void Clear()
    {
        lock (_lock) _capabilities.Clear();
    }
}
