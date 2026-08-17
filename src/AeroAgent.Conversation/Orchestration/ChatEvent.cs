using AeroAgent.Conversation.Models;

namespace AeroAgent.Conversation.Orchestration;

/// <summary>
/// 编排事件流元素。UI 订阅该流实时渲染；每条事件都带 MessageId 以定位渲染目标。
/// </summary>
public abstract record ChatEvent
{
    public required string SessionId { get; init; }
    public required string MessageId { get; init; }
}

/// <summary>一条助手消息开始（占位消息已持久化，前端可先渲染气泡骨架）。</summary>
public sealed record AssistantMessageStarted : ChatEvent
{
    public required string ProviderId { get; init; }
    public required string ModelId { get; init; }
    public required StrategyRole OrchestrationRole { get; init; }
    public string? ParentMessageId { get; init; }

    /// <summary>编排子任务标签（如"候选 A"），无标签为 null。</summary>
    public string? Label { get; init; }
}

/// <summary>文本增量（流式打字机）。</summary>
public sealed record TextDeltaEvent : ChatEvent
{
    public required string Delta { get; init; }
}

/// <summary>推理/思考增量（支持的模型；前端可折叠展示）。</summary>
public sealed record ReasoningDeltaEvent : ChatEvent
{
    public required string Delta { get; init; }
}

/// <summary>消息正常结束，附带真实用量与延迟。</summary>
public sealed record MessageCompletedEvent : ChatEvent
{
    public int TokensIn { get; init; }
    public int TokensOut { get; init; }
    public double CostUsd { get; init; }
    public int LatencyMs { get; init; }
}

/// <summary>消息失败。</summary>
public sealed record MessageFailedEvent : ChatEvent
{
    public required string Error { get; init; }
}

/// <summary>消息被取消。</summary>
public sealed record MessageCancelledEvent : ChatEvent;

/// <summary>整轮对话结束（可能含多条编排消息）。</summary>
public sealed record TurnCompletedEvent : ChatEvent
{
    public required OrchestrationStrategy Strategy { get; init; }
    public required int TotalMessages { get; init; }
    public required double TotalCostUsd { get; init; }
}
