using System;
using System.Collections.Generic;

namespace AeroAgent.Autonomy.Mission;

/// <summary>
/// 任务执行计划的一个步骤。Planning 阶段产出（LLM 生成或确定性推导），
/// Verifying 阶段逐条对照。
/// </summary>
public sealed record PlanStep(
    string Title,
    string Description,
    string AcceptanceCriteria);

/// <summary>
/// 任务执行计划。来源如实标注（真实 LLM / 确定性推导 [DEGRADED]）。
/// </summary>
public sealed record MissionPlan
{
    /// <summary>计划步骤（至少 1 步；空计划视为 Planning 失败）。</summary>
    public IReadOnlyList<PlanStep> Steps { get; init; } = Array.Empty<PlanStep>();

    /// <summary>计划产出来源说明（"llm" 或 "heuristic-degraded"）。</summary>
    public string Source { get; init; } = "heuristic-degraded";

    /// <summary>生成时刻。</summary>
    public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>单条校验检查（对照计划与验收标准的真实证据记录）。</summary>
public sealed record VerificationCheck(
    string Name,
    bool Passed,
    string Evidence);

/// <summary>
/// Verifying 阶段结果：执行产物与计划/验收标准的对照结论。
/// 每条检查都携带真实证据（内容长度、成本、会话 Id 等），无证据不得判过。
/// </summary>
public sealed record VerificationResult
{
    /// <summary>全部检查通过才为 true。</summary>
    public bool Passed { get; init; }

    /// <summary>逐条检查与证据。</summary>
    public IReadOnlyList<VerificationCheck> Checks { get; init; } = Array.Empty<VerificationCheck>();

    /// <summary>校验结论摘要（人类可读）。</summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>校验时刻。</summary>
    public DateTime VerifiedAtUtc { get; init; } = DateTime.UtcNow;
}
