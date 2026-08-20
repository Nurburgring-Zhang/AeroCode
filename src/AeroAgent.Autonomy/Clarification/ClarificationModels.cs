using System;
using System.Collections.Generic;
using AeroAgent.Autonomy.Common;

namespace AeroAgent.Autonomy.Clarification;

/// <summary>
/// 歧义维度标识。ClarificationGate 逐维度评分，缺失越严重分越高。
/// 值为可读的维度名（用于问题定向与可解释性）。
/// </summary>
public static class AmbiguityDimension
{
    /// <summary>主体缺失：不知道对哪个对象/谁操作。</summary>
    public const string Subject = "subject";

    /// <summary>动作缺失：不知道要做什么操作。</summary>
    public const string Action = "action";

    /// <summary>验收标准缺失：不知道怎样算完成。</summary>
    public const string Acceptance = "acceptance";

    /// <summary>范围约束缺失：不知道边界/期限/取舍。</summary>
    public const string Scope = "scope";

    /// <summary>上下文缺失：存在未解析的指代或背景依赖。</summary>
    public const string Context = "context";
}

/// <summary>一条针对性澄清问题（绑定到触发它的歧义维度）。</summary>
public sealed record ClarificationQuestion(string Dimension, string Question);

/// <summary>
/// 澄清门评估结果。AmbiguityScore ∈ [0,1]；超过阈值时
/// <see cref="Questions"/> 给出最多 3 个针对性澄清问题，否则为空（直接放行）。
/// </summary>
public sealed record ClarificationResult
{
    /// <summary>综合歧义度（0=完全明确，1=高度模糊）。</summary>
    public required double AmbiguityScore { get; init; }

    /// <summary>是否超过阈值需要澄清。</summary>
    public required bool RequiresClarification { get; init; }

    /// <summary>针对性澄清问题（最多 3 个，按维度得分降序）；未超阈值为空。</summary>
    public IReadOnlyList<ClarificationQuestion> Questions { get; init; } = Array.Empty<ClarificationQuestion>();

    /// <summary>各维度得分明细（可解释性）。</summary>
    public IReadOnlyDictionary<string, double> DimensionScores { get; init; }
        = new Dictionary<string, double>();

    /// <summary>产出来源（启发式 / 真实 LLM）。</summary>
    public AnalysisSource Source { get; init; } = AnalysisSource.Heuristic;
}
