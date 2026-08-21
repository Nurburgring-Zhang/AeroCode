using System;
using System.Collections.Generic;

namespace AeroAgent.Autonomy.Learning;

/// <summary>
/// 经验三分存储的种类（P6-T3 核心分类）：
/// 事实 = 环境/配置类稳定知识；轨迹 = 任务执行轨迹摘要；方法 = 有效做法。
/// </summary>
public enum ExperienceKind
{
    /// <summary>事实：环境/配置/版本/路径类稳定知识（长期有效）。</summary>
    Fact = 0,

    /// <summary>轨迹：一次任务执行的轨迹摘要（发生了什么、结果如何）。</summary>
    Trajectory = 1,

    /// <summary>方法：被验证有效（或复盘得出）的做法（下次怎么做）。</summary>
    Method = 2,
}

/// <summary>
/// 经验的生效状态（写入与生效分离）：
/// 新写入 = Pending（本次会话不可见）；下次会话构建 prompt 时激活为 Effective；
/// 被 prompt 真实消费后标记 Applied（持续有效，可继续注入）。
/// </summary>
public enum ExperienceStatus
{
    /// <summary>已写入未生效：本次会话的 prompt 不包含该经验。</summary>
    Pending = 0,

    /// <summary>已生效：下一次（及以后）构建 prompt 时会被注入。</summary>
    Effective = 1,

    /// <summary>已被至少一次 prompt 构建真实消费（仍然有效、继续注入）。</summary>
    Applied = 2,
}

/// <summary>经验持久化实体（experiences 表）。数据库是事实源，md 日志是人类可读副本。</summary>
public class ExperienceEntity
{
    /// <summary>经验唯一标识。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>经验种类（int 落库）。</summary>
    public ExperienceKind Kind { get; set; } = ExperienceKind.Method;

    /// <summary>生效状态（int 落库）。</summary>
    public ExperienceStatus Status { get; set; } = ExperienceStatus.Pending;

    /// <summary>经验标题（一行摘要）。</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>经验正文（完整内容）。</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// 来源键（唯一索引，幂等去重）：lesson:{lessonId} / trajectory:{missionId} /
    /// correction:{ruleId} / manual:{guid}。同一来源重复同步不会产生重复经验。
    /// </summary>
    public string SourceKey { get; set; } = string.Empty;

    /// <summary>来源任务 Id（可空：非任务来源的经验为 null）。</summary>
    public string? SourceMissionId { get; set; }

    /// <summary>来源阶段（可空：如 Executing/Verifying）。</summary>
    public string? SourcePhase { get; set; }

    /// <summary>标签 JSON 数组（可空）。</summary>
    public string? TagsJson { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>激活（Pending→Effective）时刻；未激活为 null。</summary>
    public DateTime? ActivatedAtUtc { get; set; }

    /// <summary>首次被 prompt 消费时刻；未消费为 null。</summary>
    public DateTime? AppliedAtUtc { get; set; }
}

/// <summary>
/// RSI L1 输出修正规则持久化实体（correction_rules 表）：
/// 失败任务复盘缺口 → 针对性修正规则；L2 把未沉淀的规则提升为 methods 经验。
/// </summary>
public class CorrectionRuleEntity
{
    /// <summary>规则唯一标识。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>来源任务 Id。</summary>
    public string MissionId { get; set; } = string.Empty;

    /// <summary>缺口描述（复盘原文）。</summary>
    public string GapDescription { get; set; } = string.Empty;

    /// <summary>修正规则文本（L1 真实生成的"若再遇到→这样做"规则）。</summary>
    public string RuleText { get; set; } = string.Empty;

    /// <summary>严重度（info/warning/critical，来自复盘缺口）。</summary>
    public string Severity { get; set; } = "info";

    /// <summary>是否已被 L2 沉淀为 methods 经验（防重复沉淀）。</summary>
    public bool Promoted { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>技能治理标记持久化实体（skill_flags 表）：低成功率技能的降级标记等。</summary>
public class SkillFlagEntity
{
    /// <summary>被标记的技能 Id（主键，一个技能一条标记）。</summary>
    public string SkillId { get; set; } = string.Empty;

    /// <summary>标记类型（如 degraded）。</summary>
    public string Flag { get; set; } = string.Empty;

    /// <summary>标记理由（真实统计数据：调用次数/成功率）。</summary>
    public string Reason { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>脱离 EF 跟踪的经验领域对象（读取方拿到的是副本，不是跟踪实体）。</summary>
public sealed record ExperienceEntry
{
    /// <summary>经验唯一标识。</summary>
    public required string Id { get; init; }

    /// <summary>经验种类。</summary>
    public required ExperienceKind Kind { get; init; }

    /// <summary>生效状态。</summary>
    public required ExperienceStatus Status { get; init; }

    /// <summary>经验标题。</summary>
    public required string Title { get; init; }

    /// <summary>经验正文。</summary>
    public required string Content { get; init; }

    /// <summary>来源键（幂等去重依据）。</summary>
    public required string SourceKey { get; init; }

    /// <summary>来源任务 Id（可空）。</summary>
    public string? SourceMissionId { get; init; }

    /// <summary>来源阶段（可空）。</summary>
    public string? SourcePhase { get; init; }

    /// <summary>标签列表。</summary>
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    public DateTime CreatedAtUtc { get; init; }

    /// <summary>激活时刻（未激活为 null）。</summary>
    public DateTime? ActivatedAtUtc { get; init; }

    /// <summary>首次被 prompt 消费时刻（未消费为 null）。</summary>
    public DateTime? AppliedAtUtc { get; init; }
}

/// <summary>脱离 EF 跟踪的修正规则领域对象。</summary>
public sealed record CorrectionRule
{
    /// <summary>规则唯一标识。</summary>
    public required string Id { get; init; }

    /// <summary>来源任务 Id。</summary>
    public required string MissionId { get; init; }

    /// <summary>缺口描述（复盘原文）。</summary>
    public required string GapDescription { get; init; }

    /// <summary>修正规则文本。</summary>
    public required string RuleText { get; init; }

    /// <summary>严重度（info/warning/critical）。</summary>
    public required string Severity { get; init; }

    /// <summary>是否已被 L2 沉淀为 methods 经验。</summary>
    public required bool Promoted { get; init; }

    public DateTime CreatedAtUtc { get; init; }
}
