// Copyright (c) AeroCode
// A2 LoopGuard（R1-W1 builder-α，契约 C-LOOP）：工具轮循环的停止条件与目标锚定配置。
// 复用既有 LoopBudget（Harness/Loop/EngineeringLoop.cs）与 TurnBudget 的诚实中止语义——
// 本配置只管目标锚定注入与偏离 strike 阈值，不另造预算类型。
namespace AeroAgent.Moa.LoopGuard;

/// <summary>
/// LoopGuard 配置（组合根从 Settings 映射后经 WorkerRunner 构造参数注入）。
/// 全部成员为 init（构造后不可变）；所有项均可独立关闭（默认值 = 现行为 + 保守护栏）。
/// </summary>
public sealed class LoopGuardOptions
{
    /// <summary>每轮首注入目标锚定段（原始目标 + 最近 checkpoint 摘要 + 剩余判据）。false = 关闭注入。</summary>
    public bool GoalAnchoringEnabled { get; init; } = true;

    /// <summary>连续 OffGoal strike 达到该值即触发 checkpoint 恢复 + 升级（默认 2）。最小值 1。</summary>
    public int MaxStrikes { get; init; } = 2;

    /// <summary>锚定段中「最近 checkpoint 摘要」的最大字符数（截断加省略号）。</summary>
    public int CheckpointSummaryChars { get; init; } = 200;

    /// <summary>关闭目标锚定的预设（偏离检测仍可用——检测由注入的 IDeviationDetector 决定）。</summary>
    public static LoopGuardOptions Disabled { get; } = new() { GoalAnchoringEnabled = false };
}
