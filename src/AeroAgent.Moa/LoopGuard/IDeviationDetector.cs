// Copyright (c) AeroCode
// A2 偏离检测（契约 C-LOOP）：每轮末对工具轮产物做 OnGoal/OffGoal 评估。
namespace AeroAgent.Moa.LoopGuard;

/// <summary>一轮工具循环相对任务目标的偏离判定。</summary>
public enum DeviationVerdict
{
    /// <summary>在目标轨道上（strike 计数清零）。</summary>
    OnGoal,

    /// <summary>偏离目标（strike 计数 +1；连续达到阈值触发 checkpoint 恢复 + 升级）。</summary>
    OffGoal,
}

/// <summary>一轮工具循环的偏离检测输入（助手本轮输出 + 本轮各工具结果，均为真实数据）。</summary>
/// <param name="Turn">0 起的工具轮序号。</param>
/// <param name="Anchor">任务目标锚。</param>
/// <param name="AssistantContent">本轮助手的文本输出（可为空——纯 tool_calls 轮）。</param>
/// <param name="ToolOutputs">本轮各工具调用的真实输出（顺序与 tool_calls 一致）。</param>
public sealed record DeviationInput(
    int Turn,
    GoalAnchor Anchor,
    string? AssistantContent,
    IReadOnlyList<string> ToolOutputs);

/// <summary>偏离检测结论。</summary>
/// <param name="Verdict">OnGoal / OffGoal。</param>
/// <param name="Reason">判定依据（人读；进入 strike 日志与升级上下文）。</param>
public sealed record DeviationReport(DeviationVerdict Verdict, string Reason);

/// <summary>
/// 偏离检测器（纯函数语义）。实现约束：Evaluate 不得抛出、不做 IO、不持状态（可重入）；
/// 阈值/计数归 WorkerRunner 管，检测器只对单轮给判定。
/// </summary>
public interface IDeviationDetector
{
    /// <summary>评估本轮是否偏离任务目标。</summary>
    DeviationReport Evaluate(DeviationInput input);
}
