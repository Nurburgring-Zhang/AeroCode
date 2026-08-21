using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AeroAgent.Autonomy.Analysis;
using AeroAgent.Conversation.Models;
using AeroCode.Skills.AutoCreate;
using AeroCode.Skills.Models;

namespace AeroAgent.Autonomy.Learning;

/// <summary>
/// RSI L3 可调优参数集（真实对应既有组件的阈值）：
/// <list type="bullet">
/// <item><see cref="DecomposeComplexityThreshold"/> 对应 StrategySelector 的 Decompose 升级阈值
/// （既有实现是编译期常量，调优结果以快照/当前参数文件形式落盘，供组合根接线时消费）；</item>
/// <item><see cref="ClarificationThreshold"/> 对应 ClarificationGate 的歧义阈值
/// （该门本身就接受运行时 threshold 参数，调优结果可直接应用）。</item>
/// </list>
/// </summary>
public sealed record RsiParameterSet
{
    /// <summary>复杂度达到该值时升级为 Decompose（StrategySelector 规则的可调版本）。</summary>
    public double DecomposeComplexityThreshold { get; init; } = 4.0;

    /// <summary>综合歧义度达到该值时触发澄清（ClarificationGate 阈值）。</summary>
    public double ClarificationThreshold { get; init; } = 0.45;

    /// <summary>参数集标签（来源说明：default / 变异描述 / 人工）。</summary>
    public string Label { get; init; } = "default";
}

/// <summary>
/// held-out 验证样本：一条带标注的历史任务特征。标注可为空（该维度不参与评分）——
/// 只拿真实可证明的标签评分，绝不虚构标签。
/// </summary>
public sealed record RsiHeldOutSample
{
    /// <summary>任务类型（来自历史任务分析）。</summary>
    public required TaskType Type { get; init; }

    /// <summary>复杂度评分 1-5（来自历史任务分析）。</summary>
    public required int Complexity { get; init; }

    /// <summary>综合歧义度 0-1（来自历史澄清门评估）。</summary>
    public required double AmbiguityScore { get; init; }

    /// <summary>被证明成功的编排策略（任务成功时 = 所用策略；失败任务为 null = 该维度不评分）。</summary>
    public OrchestrationStrategy? SuccessfulStrategy { get; init; }

    /// <summary>澄清是否被证明有帮助（null = 证据不足，该维度不评分）。</summary>
    public bool? ClarificationHelped { get; init; }

    /// <summary>样本出处说明（可追溯性，如 mission id + 终局）。</summary>
    public string Provenance { get; init; } = string.Empty;
}

/// <summary>一个变异候选的评估结果。</summary>
public sealed record RsiCandidateResult(
    RsiParameterSet Parameters,
    double Accuracy,
    bool PassedGate,
    string Note);

/// <summary>一轮 RSI L3 自调优的完整结果（变异→评估→决策全程留痕）。</summary>
public sealed record RsiRoundResult
{
    /// <summary>轮次标识（时间戳派生，唯一）。</summary>
    public required string RoundId { get; init; }

    /// <summary>基线参数（本轮开始时生效的参数）。</summary>
    public required RsiParameterSet Baseline { get; init; }

    /// <summary>基线在 held-out 集上的准确率。</summary>
    public required double BaselineAccuracy { get; init; }

    /// <summary>全部候选的评估结果（留痕用，按生成顺序）。</summary>
    public IReadOnlyList<RsiCandidateResult> Candidates { get; init; } = Array.Empty<RsiCandidateResult>();

    /// <summary>是否应用了新参数（true = 有候选过 gate 且已生效 + 旧参数已快照）。</summary>
    public required bool Applied { get; init; }

    /// <summary>被应用的候选（未应用为 null）。</summary>
    public RsiCandidateResult? AppliedCandidate { get; init; }

    /// <summary>旧参数快照文件路径（Applied=true 时非 null，回退的来源）。</summary>
    public string? SnapshotPath { get; init; }

    /// <summary>决策理由（应用/拒绝都如实说明）。</summary>
    public required string Reason { get; init; }

    /// <summary>本轮留痕日志文件。</summary>
    public required string LogPath { get; init; }
}

/// <summary>RSI 回退结果。</summary>
public sealed record RsiRollbackResult(
    bool Success,
    RsiParameterSet? RestoredParameters,
    string? SnapshotPath,
    string Reason);

/// <summary>RSI L1 修正规则记录 + L2 沉淀的结果统计。</summary>
public sealed record RsiCompositeTierResult(
    int CorrectionRulesRecorded,
    int MethodsPromoted);

/// <summary>创造档（生成新技能）提案结果。</summary>
public sealed record RsiSkillProposalResult(
    bool Approved,
    bool Created,
    Skill? Skill,
    string Reason);

/// <summary>
/// 创造档批准方抽象。安全约定：创造档（经 SkillCreator 生成新技能）必须经批准方
/// 明确批准；默认实现 <see cref="DenyAllSkillApproval"/> 一律不批准（安全档语义）。
/// </summary>
public interface ISkillApproval
{
    /// <summary>对一个技能创建提案做出批准决定（true = 批准创建）。</summary>
    Task<bool> ApproveAsync(AutoCreateCandidate candidate, CancellationToken ct = default);
}

/// <summary>默认批准方：一律不批准（创造档默认关闭，测试/组合根可注入真实批准方）。</summary>
public sealed class DenyAllSkillApproval : ISkillApproval
{
    /// <inheritdoc />
    public Task<bool> ApproveAsync(AutoCreateCandidate candidate, CancellationToken ct = default)
        => Task.FromResult(false);
}

/// <summary>RSI L3 一轮的可选项。</summary>
public sealed class RsiRoundOptions
{
    /// <summary>
    /// 准确率下限（gate 双条件之一：候选准确率必须 ≥ 基线 且 ≥ 本下限）。
    /// 默认 0.6——低于该准确率的参数集无论相对基线如何都不允许生效。
    /// </summary>
    public double MinAccuracyFloor { get; init; } = 0.6;

    /// <summary>
    /// true = 候选必须严格优于基线（准确率相等不应用）；
    /// false（默认）= 按门禁字面语义"不低于当前"，相等也过 gate。
    /// </summary>
    public bool RequireStrictImprovement { get; init; }
}
