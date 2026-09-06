using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AeroCode.AI.Capabilities;

namespace AeroAgent.Moa.Assignment;

/// <summary>推理努力档位（B4 钉死枚举）。Standard = 现行为；Peak = 厂商最高推理档。</summary>
public enum EffortTier
{
    Standard = 0,
    Peak = 1,
}

/// <summary>effort 档位决策结果：选中的档 + 峰值档厂商 token + 可解释理由。</summary>
public sealed record EffortDecision(string ProviderId, EffortTier Tier, string? VendorToken, string Reason);

/// <summary>峰值档厂商 token 映射规则（对 providerId 不区分大小写包含匹配，先命中先用）。</summary>
public sealed record EffortTokenRule
{
    /// <summary>厂商家族匹配串（如 "openai"/"anthropic"/"claude"/"gemini"）。</summary>
    public required string Family { get; init; }

    /// <summary>峰值档对应的厂商侧 token（仅产出决策，请求字段与发送由 β/缝合消费本决策完成）。</summary>
    public required string PeakToken { get; init; }

    /// <summary>研究口径默认映射：O=xhigh / A=extended-thinking / G=deep-think。</summary>
    public static readonly IReadOnlyList<EffortTokenRule> Defaults = new[]
    {
        new EffortTokenRule { Family = "openai", PeakToken = "xhigh" },
        new EffortTokenRule { Family = "anthropic", PeakToken = "extended-thinking" },
        new EffortTokenRule { Family = "claude", PeakToken = "extended-thinking" },
        new EffortTokenRule { Family = "gemini", PeakToken = "deep-think" },
    };
}

/// <summary>
/// B4 effort 档位映射：产出「选哪个档」的决策与厂商 token 映射表。
/// fail-closed：探测接口未注入（null = 现行为）或探测返回 Missing 时一律回落 Standard，
/// 绝不凭空宣称峰值档可用；探测 Supported/Downgraded（文档化降级路径）才允许 Peak。
/// 显式规则表（构造参数）优先于内建默认映射。
/// </summary>
public sealed class EffortProfile
{
    private readonly IVendorCapabilityProbe? _probe;
    private readonly IReadOnlyList<EffortTokenRule> _rules;

    public EffortProfile(IVendorCapabilityProbe? probe = null, IReadOnlyList<EffortTokenRule>? rules = null)
    {
        _probe = probe;
        _rules = rules ?? EffortTokenRule.Defaults;
    }

    /// <summary>峰值档厂商 token 映射（O=xhigh / A=extended-thinking / G=deep-think）；无匹配返回 null。</summary>
    public string? PeakTokenFor(string providerId)
    {
        foreach (var rule in _rules)
        {
            if (providerId.Contains(rule.Family, StringComparison.OrdinalIgnoreCase))
            {
                return rule.PeakToken;
            }
        }

        return null;
    }

    /// <summary>
    /// 档位决策：未请求峰值 / 无厂商映射 / 探测未注入 / 探测 Missing → Standard（fail-closed）；
    /// 探测 Supported 或 Downgraded → Peak 并给出厂商 token。探测仅在确有映射时调用（省一次探测）。
    /// <paramref name="ct"/>（R3-δ，可选）透传给探测（取消时探测 fail-closed 收敛 Missing）。
    /// </summary>
    public async Task<EffortDecision> DecideAsync(
        string providerId, bool peakRequested, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return new EffortDecision(providerId ?? string.Empty, EffortTier.Standard, null, "providerId 为空，回落 Standard。");
        }

        if (!peakRequested)
        {
            return new EffortDecision(providerId, EffortTier.Standard, null, "未请求峰值推理档，保持 Standard（现行为）。");
        }

        var token = PeakTokenFor(providerId);
        if (token is null)
        {
            return new EffortDecision(providerId, EffortTier.Standard, null, $"请求了峰值档但无 {providerId} 的厂商 token 映射，回落 Standard。");
        }

        if (_probe is null)
        {
            return new EffortDecision(providerId, EffortTier.Standard, null, "IVendorCapabilityProbe 未注入（null=现行为），峰值档不可证实，回落 Standard。");
        }

        var state = await _probe.ProbeAsync(providerId, VendorCapability.EffortTiers, ct);
        if (state == VendorCapabilityState.Missing)
        {
            return new EffortDecision(providerId, EffortTier.Standard, null, $"探测 {providerId} 的 EffortTiers 返回 Missing（fail-closed），回落 Standard。");
        }

        return new EffortDecision(providerId, EffortTier.Peak, token, $"探测 {providerId} 的 EffortTiers 返回 {state}，映射峰值档 token「{token}」。");
    }
}
