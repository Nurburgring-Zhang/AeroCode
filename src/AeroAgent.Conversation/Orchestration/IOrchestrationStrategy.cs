using System.Collections.Generic;
using System.Threading;
using AeroCode.AI.Providers;
using AeroAgent.Conversation.Models;

namespace AeroAgent.Conversation.Orchestration;

/// <summary>
/// 一次编排执行的上下文。历史消息已按时间升序，最后一条是本轮用户消息。
/// </summary>
public sealed record OrchestrationContext
{
    public required ChatSession Session { get; init; }
    public required IReadOnlyList<ChatMessage> History { get; init; }
    public required string UserMessageId { get; init; }
    public required IProviderRegistry Providers { get; init; }
    public CancellationToken CancellationToken { get; init; }
}

/// <summary>
/// 编排策略契约。Phase 1 仅 Single；Phase 2 的 Router/Decompose/Ensemble/Pipeline
/// 实现同一契约，门面按会话策略路由。
/// </summary>
public interface IOrchestrationStrategy
{
    OrchestrationStrategy Kind { get; }

    /// <summary>执行编排并产出事件流。实现方负责产出消息的持久化。</summary>
    IAsyncEnumerable<ChatEvent> ExecuteAsync(OrchestrationContext context);
}
