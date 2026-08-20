using System;

namespace AeroAgent.Autonomy.Data;

/// <summary>
/// 任务记录持久化实体（missions 表）。状态机每步推进都把产物序列化落库，
/// 数据库是事实源。复杂产物（分析/澄清/钢人/计划/执行/校验/复盘/转移轨迹）
/// 以 JSON 文本列存储——与 Conversation 的 ToolCallsJson 同约定。
/// </summary>
public class MissionRecord
{
    /// <summary>任务唯一标识（GUID 字符串）。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>原始任务文本。</summary>
    public string TaskText { get; set; } = string.Empty;

    /// <summary>当前状态（<see cref="Mission.MissionState"/>，int 落库）。</summary>
    public Mission.MissionState State { get; set; } = Mission.MissionState.Received;

    /// <summary>终局结果（<see cref="Mission.MissionOutcome"/>，int 落库）。</summary>
    public Mission.MissionOutcome Outcome { get; set; } = Mission.MissionOutcome.Pending;

    /// <summary>任务分析产物 JSON（TaskAnalysis）。</summary>
    public string? AnalysisJson { get; set; }

    /// <summary>选定的编排策略名（如 Single/Decompose）。</summary>
    public string? Strategy { get; set; }

    /// <summary>策略选择理由（可解释性）。</summary>
    public string? StrategyRationale { get; set; }

    /// <summary>澄清门产物 JSON（ClarificationResult + 应答）。</summary>
    public string? ClarificationJson { get; set; }

    /// <summary>钢人论证产物 JSON（SteelmanRecord）。</summary>
    public string? SteelmanJson { get; set; }

    /// <summary>执行计划 JSON（MissionPlan）。</summary>
    public string? PlanJson { get; set; }

    /// <summary>执行使用的对话会话 Id（MOA 编排真实落库的会话）。</summary>
    public string? SessionId { get; set; }

    /// <summary>执行结果 JSON（MissionExecutionOutcome 摘要）。</summary>
    public string? ExecutionJson { get; set; }

    /// <summary>校验结果 JSON（VerificationResult）。</summary>
    public string? VerificationJson { get; set; }

    /// <summary>复盘产物 JSON（RetrospectiveRecord）。</summary>
    public string? RetrospectiveJson { get; set; }

    /// <summary>状态转移轨迹 JSON（List&lt;MissionTransition&gt;）。</summary>
    public string? TransitionsJson { get; set; }

    /// <summary>失败时的错误信息（成功为 null）。</summary>
    public string? Error { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// 经验教训持久化实体（lessons 表）。复盘引擎把每个缺口写成一条 lesson，
/// ExperienceInjector 在后续任务构建 system prompt 时真实读取并注入。
/// </summary>
public class LessonRecord
{
    /// <summary>经验唯一标识。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>来源任务 Id。</summary>
    public string MissionId { get; set; } = string.Empty;

    /// <summary>产生该经验的阶段（如 Executing/Verifying）。</summary>
    public string Phase { get; set; } = string.Empty;

    /// <summary>缺口描述（学到的教训）。</summary>
    public string Gap { get; set; } = string.Empty;

    /// <summary>补全建议（下次怎么做）。</summary>
    public string Suggestion { get; set; } = string.Empty;

    /// <summary>严重度：info / warning / critical。</summary>
    public string Severity { get; set; } = "info";

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
