using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using AeroAgent.Conversation.Models;
using AeroAgent.Conversation.Orchestration;
using AeroAgent.Conversation.Services;
using AeroAgent.Moa.Accounting;
using AeroAgent.Moa.Assignment;
using AeroAgent.Moa.Profiles;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using AiChatMessage = AeroCode.AI.Models.ChatMessage;
using AiChatRequest = AeroCode.AI.Models.ChatRequest;

namespace AeroAgent.Moa.Strategies;

/// <summary>一次 worker 调用的结果。RunAsync 不抛 provider 异常——成败都在结果里。</summary>
public sealed record WorkerOutcome(
    string MessageId,
    string ProviderId,
    string ModelId,
    string Content,
    bool Succeeded,
    bool Cancelled,
    string? Error,
    int TokensIn,
    int TokensOut,
    double CostUsd,
    int LatencyMs);

/// <summary>
/// MOA 各策略共用的单模型调用引擎：持久化占位消息 → 真实调用（流式/非流式）→
/// 事件发射 → 真实用量与成本落库 → 自学习统计回填。
/// 异常边界：provider 异常/取消都在此收容为 Failed/Cancelled 终态（DB 是事实源），
/// 不向策略抛出——策略按 <see cref="WorkerOutcome"/> 决定降级或中止。
/// </summary>
public sealed class WorkerRunner
{
    private readonly ISessionService _sessions;
    private readonly IModelProfileCatalog _catalog;
    private readonly ILogger<WorkerRunner> _logger;

    public WorkerRunner(
        ISessionService sessions,
        IModelProfileCatalog catalog,
        ILogger<WorkerRunner>? logger = null)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _logger = logger ?? NullLogger<WorkerRunner>.Instance;
    }

    /// <summary>
    /// 执行一次模型调用。事件写入 <paramref name="sink"/>（可为 null = 静默执行）。
    /// </summary>
    /// <param name="ctx">编排上下文（会话/历史/provider 注册表）。</param>
    /// <param name="assignment">模型分配（provider + 模型 + 画像）。</param>
    /// <param name="role">本消息在编排中的角色（归属标注）。</param>
    /// <param name="parentMessageId">父消息 Id（归属树），可为 null。</param>
    /// <param name="label">可读标签（如子任务名），可为 null。</param>
    /// <param name="messages">发给 provider 的消息序列。</param>
    /// <param name="stream">true = 流式打字机（面向用户的最终答复）；false = 一次性（内部子任务，可拿到真实 usage）。</param>
    /// <param name="isFinal">true = 面向用户的最终答复（进入后续轮次上下文）；false = 编排中间产物（仅当轮编排与审计，不回灌历史）。</param>
    /// <param name="sink">事件写入通道，null = 静默执行。</param>
    /// <param name="budget">单轮预算；发起前检查，超限直接失败返回。</param>
    /// <param name="ct">取消令牌。</param>
    public async Task<WorkerOutcome> RunAsync(
        OrchestrationContext ctx,
        ModelAssignment assignment,
        StrategyRole role,
        string? parentMessageId,
        string? label,
        IReadOnlyList<AiChatMessage> messages,
        bool stream,
        bool isFinal,
        ChannelWriter<ChatEvent>? sink,
        TurnBudget? budget,
        CancellationToken ct)
    {
        var sessionId = ctx.Session.Id;

        // ---- 预算检查（诚实中止，不静默继续）----
        if (budget is { HasBudget: false })
        {
            var budgetError = $"budget exceeded: spent ${budget.SpentUsd:F6}, limit ${budget.MaxUsd:F6}";
            await EmitAsync(sink, new MessageFailedEvent
            {
                SessionId = sessionId,
                MessageId = string.Empty,
                Error = budgetError,
            });
            return new WorkerOutcome(string.Empty, assignment.ProviderId, assignment.ModelId,
                string.Empty, Succeeded: false, Cancelled: false, budgetError, 0, 0, 0, 0);
        }

        var provider = ctx.Providers.Get(assignment.ProviderId);

        // ---- 持久化占位消息 ----
        var message = new ChatMessage
        {
            SessionId = sessionId,
            Role = ChatRole.Assistant,
            ProviderId = assignment.ProviderId,
            ModelId = assignment.ModelId,
            OrchestrationRole = role,
            ParentMessageId = parentMessageId,
            Label = label,
            IsFinal = isFinal,
            Status = MessageStatus.Streaming,
        };
        var appended = await _sessions.AppendMessageAsync(message);
        if (!appended.IsSuccess)
        {
            var error = appended.Error ?? "persist failed";
            await EmitAsync(sink, new MessageFailedEvent
            {
                SessionId = sessionId,
                MessageId = message.Id,
                Error = error,
            });
            return new WorkerOutcome(message.Id, assignment.ProviderId, assignment.ModelId,
                string.Empty, Succeeded: false, Cancelled: false, error, 0, 0, 0, 0);
        }

        await EmitAsync(sink, new AssistantMessageStarted
        {
            SessionId = sessionId,
            MessageId = message.Id,
            ProviderId = assignment.ProviderId,
            ModelId = assignment.ModelId,
            OrchestrationRole = role,
            ParentMessageId = parentMessageId,
            Label = label,
        });

        var request = new AiChatRequest
        {
            Model = assignment.ModelId,
            Messages = messages,
            Stream = stream && provider.SupportsStreaming,
        };

        var sb = new StringBuilder();
        var sw = Stopwatch.StartNew();
        var tokensIn = 0;
        var tokensOut = 0;

        try
        {
            if (request.Stream)
            {
                await foreach (var chunk in provider.StreamChatAsync(request, ct))
                {
                    if (!string.IsNullOrEmpty(chunk.DeltaContent))
                    {
                        sb.Append(chunk.DeltaContent);
                        // 增量直接写（带 ct）：取消时立即停止泵送。
                        if (sink is not null)
                        {
                            await sink.WriteAsync(new TextDeltaEvent
                            {
                                SessionId = sessionId,
                                MessageId = message.Id,
                                Delta = chunk.DeltaContent,
                            }, ct);
                        }
                    }

                    if (!string.IsNullOrEmpty(chunk.DeltaReasoning) && sink is not null)
                    {
                        await sink.WriteAsync(new ReasoningDeltaEvent
                        {
                            SessionId = sessionId,
                            MessageId = message.Id,
                            Delta = chunk.DeltaReasoning,
                        }, ct);
                    }
                }
            }
            else
            {
                var response = await provider.ChatAsync(request, ct);
                sb.Append(response.Content);
                if (sink is not null && response.Content.Length > 0)
                {
                    await sink.WriteAsync(new TextDeltaEvent
                    {
                        SessionId = sessionId,
                        MessageId = message.Id,
                        Delta = response.Content,
                    }, ct);
                }

                if (response.Usage is not null)
                {
                    tokensIn = response.Usage.PromptTokens;
                    tokensOut = response.Usage.CompletionTokens;
                }
            }
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            var latency = (int)sw.ElapsedMilliseconds;
            message.Content = sb.ToString();
            message.Status = MessageStatus.Cancelled;
            message.LatencyMs = latency;
            await _sessions.UpdateMessageAsync(message);

            // 用户取消不是模型质量问题：不计入画像统计（否则污染失败率打分），
            // 也不产生成本（未计价不猜）。

            // 终态事件不带 ct：取消场景下也要送达。
            await EmitAsync(sink, new MessageCancelledEvent
            {
                SessionId = sessionId,
                MessageId = message.Id,
            });

            return new WorkerOutcome(message.Id, assignment.ProviderId, assignment.ModelId,
                message.Content, Succeeded: false, Cancelled: true, "cancelled by user",
                tokensIn, tokensOut, 0, latency);
        }
        catch (Exception ex)
        {
            sw.Stop();
            var latency = (int)sw.ElapsedMilliseconds;
            var error = ErrorText.Truncate(ex.Message) ?? ex.Message;
            message.Content = sb.ToString(); // 已产出的部分如实保留
            message.Status = MessageStatus.Failed;
            message.Error = error;
            message.LatencyMs = latency;
            await _sessions.UpdateMessageAsync(message);
            _catalog.RecordUsage(assignment.ProviderId, assignment.ModelId, latency, failed: true);
            await SaveCatalogQuietlyAsync();
            _logger.LogWarning(
                "worker call failed: {Provider}/{Model} after {LatencyMs}ms: {Error}",
                assignment.ProviderId, assignment.ModelId, latency, error);

            await EmitAsync(sink, new MessageFailedEvent
            {
                SessionId = sessionId,
                MessageId = message.Id,
                Error = error,
            });

            return new WorkerOutcome(message.Id, assignment.ProviderId, assignment.ModelId,
                message.Content, Succeeded: false, Cancelled: false, error,
                tokensIn, tokensOut, 0, latency);
        }

        sw.Stop();

        // ---- 正常收尾：真实用量 + 画像计价 ----
        var cost = CostTracker.Estimate(assignment.Profile, tokensIn, tokensOut) ?? 0.0;
        message.Content = sb.ToString();
        message.Status = MessageStatus.Completed;
        message.TokensIn = tokensIn;
        message.TokensOut = tokensOut;
        message.CostUsd = cost;
        message.LatencyMs = (int)sw.ElapsedMilliseconds;
        await _sessions.UpdateMessageAsync(message);

        _catalog.RecordUsage(assignment.ProviderId, assignment.ModelId, message.LatencyMs, failed: false);
        await SaveCatalogQuietlyAsync();
        budget?.AddActual(cost);

        await EmitAsync(sink, new MessageCompletedEvent
        {
            SessionId = sessionId,
            MessageId = message.Id,
            TokensIn = tokensIn,
            TokensOut = tokensOut,
            CostUsd = cost,
            LatencyMs = message.LatencyMs,
        });

        return new WorkerOutcome(message.Id, assignment.ProviderId, assignment.ModelId,
            message.Content, Succeeded: true, Cancelled: false, null,
            tokensIn, tokensOut, cost, message.LatencyMs);
    }

    /// <summary>
    /// 终态事件容错写入：消费方已停（ChannelClosed）或已取消时不抛出——
    /// 终态已落库，事件流只是通知通道。
    /// </summary>
    private static async Task EmitAsync(ChannelWriter<ChatEvent>? sink, ChatEvent ev)
    {
        if (sink is null)
        {
            return;
        }

        try
        {
            await sink.WriteAsync(ev, CancellationToken.None);
        }
        catch (ChannelClosedException)
        {
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task SaveCatalogQuietlyAsync()
    {
        try
        {
            await _catalog.SaveAsync();
        }
        catch (Exception ex)
        {
            // 统计持久化失败不影响主流程（下次保存会覆盖），但降级必须可见。
            _logger.LogWarning("[DEGRADED] failed to persist model profile stats: {Error}", ex.Message);
        }
    }
}
