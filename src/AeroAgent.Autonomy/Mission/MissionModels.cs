using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AeroAgent.Autonomy.Analysis;
using AeroAgent.Conversation.Models;

namespace AeroAgent.Autonomy.Mission;

/// <summary>
/// 任务（Mission）状态机的状态。线性推进：
/// Received → Analyzed → Clarification → Steelman → Planning → Executing →
/// Verifying → Retrospective → ExperienceWritten。
/// 执行失败不跳过复盘——Executing 异常仍推进到 Retrospective（复盘记录失败）。
/// </summary>
public enum MissionState
{
    /// <summary>已接收任务文本，尚未分析。</summary>
    Received = 0,

    /// <summary>任务分析完成（类型/复杂度/能力需求）。</summary>
    Analyzed = 1,

    /// <summary>澄清门评估完成（可能附带澄清问答）。</summary>
    Clarification = 2,

    /// <summary>钢人论证完成。</summary>
    Steelman = 3,

    /// <summary>规划完成（产出执行计划）。</summary>
    Planning = 4,

    /// <summary>经 MOA 编排执行中/执行完成。</summary>
    Executing = 5,

    /// <summary>校验完成（对照计划与验收标准）。</summary>
    Verifying = 6,

    /// <summary>复盘完成（逐阶段对照 + 缺口清单 + 补全建议）。</summary>
    Retrospective = 7,

    /// <summary>经验已写入（lessons 落库 + 复盘 md 落盘），终态。</summary>
    ExperienceWritten = 8,
}

/// <summary>任务终局结果（与状态正交：状态描述推进到哪一步，结果描述成败）。</summary>
public enum MissionOutcome
{
    /// <summary>尚未到终局。</summary>
    Pending = 0,

    /// <summary>执行成功且校验通过。</summary>
    Succeeded = 1,

    /// <summary>执行失败或校验不通过。</summary>
    Failed = 2,

    /// <summary>被取消。</summary>
    Cancelled = 3,
}

/// <summary>一次状态转移的留痕：从哪到哪、何时、携带的产物摘要。</summary>
public sealed record MissionTransition(
    MissionState From,
    MissionState To,
    DateTime AtUtc,
    string Artifact);

/// <summary>
/// 任务执行上下文（交给 <see cref="IMissionExecutor"/> 的输入）。
/// </summary>
public sealed record MissionExecutionContext(
    string MissionId,
    string EffectiveTaskText,
    string SystemPrompt,
    OrchestrationStrategy Strategy,
    TaskAnalysis Analysis);

/// <summary>
/// 任务执行结果（执行器返回，成败都在结果里，不抛 provider 异常）。
/// </summary>
public sealed record MissionExecutionOutcome(
    bool Succeeded,
    bool Cancelled,
    string FinalContent,
    string? Error,
    string? SessionId,
    int AssistantMessages,
    double TotalCostUsd);

/// <summary>
/// 任务执行器抽象。生产实现 <see cref="MoaMissionExecutor"/> 经
/// AeroAgent.Moa 现有编排真实发起；抽象出来便于测试注入受控执行路径。
/// </summary>
public interface IMissionExecutor
{
    Task<MissionExecutionOutcome> ExecuteAsync(MissionExecutionContext context, CancellationToken ct);
}

/// <summary>
/// 澄清问题的应答方抽象。Interactive 场景由 UI/远端实现；
/// AutoApprove 场景不使用（澄清问题连同假设一并记录）。
/// </summary>
public interface IClarificationResponder
{
    /// <summary>
    /// 按序回答澄清问题。返回数量可少于问题数（未回答的记为缺口）。
    /// </summary>
    Task<IReadOnlyList<string>> AnswerAsync(
        IReadOnlyList<Clarification.ClarificationQuestion> questions,
        CancellationToken ct);
}

/// <summary>
/// 单次任务运行的可选配置。全部有默认值——无参运行即 AutoApprove 全自主链路。
/// </summary>
public sealed class MissionRunOptions
{
    /// <summary>钢人论证模式（默认自动批准，无人值守全自主）。</summary>
    public Steelman.SteelmanMode SteelmanMode { get; init; } = Steelman.SteelmanMode.AutoApprove;

    /// <summary>澄清阈值（默认 <see cref="Clarification.ClarificationGate.DefaultThreshold"/>）。</summary>
    public double ClarificationThreshold { get; init; } = Clarification.ClarificationGate.DefaultThreshold;

    /// <summary>注入 system prompt 的最近经验条数。</summary>
    public int MaxLessonsInjected { get; init; } = 5;

    /// <summary>显式策略覆盖；null = 由 TaskAnalysis 驱动自动选择（G1 的默认语义）。</summary>
    public OrchestrationStrategy? StrategyOverride { get; init; }

    /// <summary>Interactive 钢人模式的应答方；null 时如实降级 AutoApprove。</summary>
    public Steelman.ISteelmanResponder? SteelmanResponder { get; init; }

    /// <summary>澄清问题应答方；null 时澄清问题连同假设记录后继续。</summary>
    public IClarificationResponder? ClarificationResponder { get; init; }
}
