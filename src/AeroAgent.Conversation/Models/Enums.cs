using System;

namespace AeroAgent.Conversation.Models;

/// <summary>
/// 编排策略。统一对话可选用的 MOA 调度方式。
/// </summary>
public enum OrchestrationStrategy
{
    /// <summary>单模型直连（默认/用户指定模型）。</summary>
    Single = 0,
    /// <summary>路由：快速模型分类任务后路由到最优模型。</summary>
    Router = 1,
    /// <summary>分工：planner 拆子任务 DAG，按画像并行分配，synthesizer 聚合。</summary>
    Decompose = 2,
    /// <summary>集成：多模型并行作答，judge 裁决/合成。</summary>
    Ensemble = 3,
    /// <summary>流水线：起草→评审→修订顺序接力。</summary>
    Pipeline = 4,
}

/// <summary>
/// 消息角色。
/// </summary>
public enum ChatRole
{
    User = 0,
    Assistant = 1,
    System = 2,
    Tool = 3,
}

/// <summary>
/// MOA 策略角色（消息归属标注）。标识某条助手消息在编排中承担的职责。
/// </summary>
public enum StrategyRole
{
    /// <summary>非编排产物（Single 策略的普通回复）。</summary>
    None = 0,
    Router = 1,
    Planner = 2,
    Worker = 3,
    Judge = 4,
    Synthesizer = 5,
}

/// <summary>
/// 消息处理状态。
/// </summary>
public enum MessageStatus
{
    Pending = 0,
    Streaming = 1,
    Completed = 2,
    Failed = 3,
    Cancelled = 4,
    /// <summary>部分完成（MOA 子任务降级但整体有产出）。</summary>
    Degraded = 5,
}
