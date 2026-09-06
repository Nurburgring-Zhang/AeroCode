// Copyright (c) AeroCode
// AdvisorAutoAdoptGate（批次 C 安全切片，R3 波次 γ builder-γ）：advisor 低风险自动采纳的
// 收紧裁决（纯函数、无 IO，可独立单测）。仅在显式启用收紧开关时被消费（组合根接线）；
// 开关关闭 = 既有自动采纳行为逐字节不变。
namespace AeroAgent.Moa.Safety;

/// <summary>收紧裁决。</summary>
public enum AutoAdoptDecision
{
    /// <summary>允许自动采纳（args 无修改，或被脱敏参数全部在白名单内）。</summary>
    Allow,
    /// <summary>拒绝自动采纳 → 转既有审批路径（弹窗人工裁决；不静默丢弃、不静默放行）。</summary>
    FallThroughToApproval,
}

/// <summary>自动采纳收紧裁决结果。</summary>
/// <param name="Decision">裁决。</param>
/// <param name="Reason">裁决理由（进审计日志与弹窗 advisorNote；只含参数名，不含敏感原文）。</param>
public sealed record AutoAdoptGateResult(AutoAdoptDecision Decision, string Reason)
{
    /// <summary>是否允许自动采纳。</summary>
    public bool Allow => Decision == AutoAdoptDecision.Allow;
}

/// <summary>
/// 自动采纳收紧门（批次 C 契约）：advisor 自动采纳仅在「args 无修改」或
/// 「修改通过脱敏 + 白名单校验」时执行；否则转既有审批路径（不静默丢弃）。
/// 白名单最小化：允许"被脱敏后仍参与自动采纳判定"的参数名集合；默认空集 =
/// 任何被脱敏参数都转人工审批。注意：收紧逻辑只在显式启用新开关时生效，
/// 默认（开关关闭）行为与现状逐字节一致（现状无白名单概念，故白名单不参与默认路径）。
/// </summary>
public static class AdvisorAutoAdoptGate
{
    /// <summary>
    /// 裁决。<paramref name="modifiedArgsWhitelist"/> 为 null 或空 = 空集（被脱敏即转人工）。
    /// </summary>
    public static AutoAdoptGateResult Evaluate(
        AdvisorArgsSanitizationReport? report, IReadOnlySet<string>? modifiedArgsWhitelist)
    {
        // args 无修改（或无参数）：不存在"基于被篡改/被脱敏视图做自动采纳"的风险，按现行为采纳。
        if (report is null || !report.Modified)
        {
            return new AutoAdoptGateResult(AutoAdoptDecision.Allow, "args unmodified by sanitization");
        }

        // 修改（发生脱敏）且全部被脱敏参数在白名单内：修改通过脱敏 + 白名单校验 → 允许。
        if (modifiedArgsWhitelist is { Count: > 0 } &&
            report.ModifiedParameterNames.All(modifiedArgsWhitelist.Contains))
        {
            return new AutoAdoptGateResult(
                AutoAdoptDecision.Allow,
                $"redacted args passed whitelist ({string.Join(", ", report.ModifiedParameterNames)})");
        }

        // 其余：转既有审批路径（不静默丢弃）——理由进 advisorNote，人工裁决。
        var names = report.ModifiedParameterNames.Count > 0
            ? string.Join(", ", report.ModifiedParameterNames)
            : "(unresolved)";
        return new AutoAdoptGateResult(
            AutoAdoptDecision.FallThroughToApproval,
            $"args contained sensitive material and were redacted ({names}); auto-adopt refused, human approval required");
    }
}
