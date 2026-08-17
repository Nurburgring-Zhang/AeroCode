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

    /// <summary>true = 工具循环中间轮（本消息携带 tool_calls，正文可能为空）；UI 据此渲染工具调用标识。</summary>
    public bool HasToolCalls { get; init; }
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

/// <summary>
/// 一次工具调用开始执行（工具结果消息已持久化为 Pending 占位，MessageId 即该消息 Id）。
/// </summary>
public sealed record ToolCallStartedEvent : ChatEvent
{
    public required string ToolCallId { get; init; }
    public required string ToolName { get; init; }

    /// <summary>模型给出的参数 JSON（原样，供 UI 展示"模型要干什么"）。</summary>
    public string? ArgumentsJson { get; init; }

    /// <summary>所属助手 tool_calls 轮的消息 Id（归属树父级）。</summary>
    public string? ParentMessageId { get; init; }
}

/// <summary>
/// 一次工具调用执行结束。Success = 真实执行成功；Denied = 被授权策略拒绝；
/// 两者皆 false = 执行失败（未知工具/域内报错）。完整输出在对应消息正文，事件只带摘要预览。
/// </summary>
public sealed record ToolCallCompletedEvent : ChatEvent
{
    public required string ToolCallId { get; init; }
    public required string ToolName { get; init; }
    public required bool Success { get; init; }
    public bool Denied { get; init; }

    /// <summary>输出预览（过长已截断；全文以消息正文为准）。</summary>
    public string? OutputPreview { get; init; }

    public int LatencyMs { get; init; }
}

/// <summary>整轮对话结束（可能含多条编排消息）。</summary>
public sealed record TurnCompletedEvent : ChatEvent
{
    public required OrchestrationStrategy Strategy { get; init; }
    public required int TotalMessages { get; init; }
    public required double TotalCostUsd { get; init; }
}
