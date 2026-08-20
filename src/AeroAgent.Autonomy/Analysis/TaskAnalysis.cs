using System;
using System.Collections.Generic;
using AeroAgent.Autonomy.Common;

namespace AeroAgent.Autonomy.Analysis;

/// <summary>
/// 任务类型。TaskAnalyzer 把自由文本任务归类到单一主类型；
/// 多领域显著并存时归为 <see cref="Composite"/>。
/// </summary>
public enum TaskType
{
    /// <summary>编程/代码：实现、调试、重构、测试、脚本。</summary>
    Code = 0,

    /// <summary>研究/检索：调研、查资料、文献、全网搜索。</summary>
    Research = 1,

    /// <summary>分析：总结、对比、评估、归因、报告。</summary>
    Analysis = 2,

    /// <summary>创作：文章、文案、故事、营销内容。</summary>
    Creative = 3,

    /// <summary>运维/操作：部署、监控、配置、发布、巡检。</summary>
    Ops = 4,

    /// <summary>复合型：两个及以上类型显著并存。</summary>
    Composite = 5,
}

/// <summary>能力需求的种类（任务完成需要调动的系统能力）。</summary>
public enum CapabilityKind
{
    /// <summary>需要某个技能（SkillHub 注册的 ISkill）。</summary>
    Skill = 0,

    /// <summary>需要 Harness 引擎原语（任务图/循环/规划等）。</summary>
    Harness = 1,

    /// <summary>需要外部检索（网页/搜索引擎/资料获取）。</summary>
    Retrieval = 2,

    /// <summary>需要工具执行（笔记/文件/MCP 等注册工具）。</summary>
    Tool = 3,
}

/// <summary>
/// 一条能力需求。Name 对 Skill 是技能 id（如 engineering/code-review），
/// 对 Harness 是原语名（task-graph/loop/planner），对 Retrieval/Tool 是描述名。
/// </summary>
public sealed record CapabilityNeed(CapabilityKind Kind, string Name, string Reason);

/// <summary>
/// 任务分析结果：类型 + 复杂度 + 能力需求 + 可解释依据。
/// 复杂度 1-5（1=单步琐碎，5=多领域多阶段高难度）。
/// </summary>
public sealed record TaskAnalysis
{
    /// <summary>判定的任务类型。</summary>
    public required TaskType Type { get; init; }

    /// <summary>复杂度评分（1-5）。</summary>
    public required int Complexity { get; init; }

    /// <summary>所需能力集合（技能/Harness 原语/检索/工具）。</summary>
    public IReadOnlyList<CapabilityNeed> Capabilities { get; init; } = Array.Empty<CapabilityNeed>();

    /// <summary>各类型关键词命中得分（可解释性：为什么判定为该类型）。</summary>
    public IReadOnlyDictionary<TaskType, int> TypeScores { get; init; }
        = new Dictionary<TaskType, int>();

    /// <summary>人类可读的判定理由（命中的关键特征）。</summary>
    public string Rationale { get; init; } = string.Empty;

    /// <summary>产出来源（启发式 / 真实 LLM）。</summary>
    public AnalysisSource Source { get; init; } = AnalysisSource.Heuristic;
}
