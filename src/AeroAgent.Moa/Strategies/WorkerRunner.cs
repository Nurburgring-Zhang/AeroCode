using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using AeroAgent.Conversation.Models;
using AeroAgent.Conversation.Orchestration;
using AeroAgent.Conversation.Services;
using AeroAgent.Moa.Accounting;
using AeroAgent.Moa.Assignment;
using AeroAgent.Moa.Profiles;
using AeroAgent.Moa.Tools;
using AeroCode.AI.Providers;
using AeroCode.Harness.Compaction;
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
/// 工具循环溢出检测配置（组合根从 Settings.Compaction 映射，DI 单例注入）。
/// ThresholdTokens ≤ 0 = 关闭溢出检测（不压缩，行为与批次 A 完全一致）。
/// </summary>
public sealed record CompactionGateOptions
{
    public int ThresholdTokens { get; init; }

    public static CompactionGateOptions Disabled { get; } = new() { ThresholdTokens = 0 };
}

/// <summary>MOA 各策略共用的单模型调用引擎：持久化占位消息 → 真实调用（流式/非流式）→
/// 事件发射 → 真实用量与成本落库 → 自学习统计回填。
/// 异常边界：provider 异常/取消都在此收容为 Failed/Cancelled 终态（DB 是事实源），
/// 不向策略抛出——策略按 <see cref="WorkerOutcome"/> 决定降级或中止。
/// 批次 B G2-4：工具循环带溢出检测——估算 token 超过阈值时调 Harness <see cref="Compactor"/>
/// 压缩在途上下文（Compactor 自身发布 CompactionTriggeredEvent），工具配对完整性由本类保证。
/// </summary>
public sealed class WorkerRunner
{
    /// <summary>工具循环最大轮数：防止模型无限套娃调用工具（超限诚实中止并落失败态）。</summary>
    public const int MaxToolTurns = 8;

    private readonly ISessionService _sessions;
    private readonly IModelProfileCatalog _catalog;
    private readonly ILogger<WorkerRunner> _logger;
    private readonly ToolRouter? _tools;
    private readonly MoaOptions? _options;
    private readonly Compactor? _compactor;
    private readonly CompactionGateOptions? _compaction;

    public WorkerRunner(
        ISessionService sessions,
        IModelProfileCatalog catalog,
        ILogger<WorkerRunner>? logger = null,
        ToolRouter? tools = null,
        MoaOptions? options = null,
        Compactor? compactor = null,
        CompactionGateOptions? compaction = null)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _logger = logger ?? NullLogger<WorkerRunner>.Instance;
        _tools = tools;
        _options = options;
        _compactor = compactor;
        _compaction = compaction;
    }

    /// <summary>执行一次模型调用。事件写入 <paramref name="sink"/>（可为 null = 静默执行）。</summary>
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

        // ---- 工具循环：注册中心有工具且未禁用时走非流式多轮（需要完整 tool_calls 才能配对执行）。
        // 占位消息承载最终答复；中间轮次（助手 tool_calls + tool 结果）逐条真实落库。
        // stream 参数在此路径被忽略——工具轮没有打字机，这是 Phase 3 的既定取舍。----
        if (_options?.ToolsEnabled is not false && _tools is { HasTools: true })
        {
            return await RunToolLoopAsync(ctx, assignment, role, message, provider, messages, sink, budget, ct);
        }

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
    /// 工具循环：非流式多轮。每轮把上一轮的助手 tool_calls + tool 结果追加进上下文再问模型，
    /// 直到模型不再请求工具（得到最终答复）或达到 <see cref="MaxToolTurns"/>（诚实中止）。
    /// 占位消息承载最终答复；中间轮次逐条落库（IsFinal == false，HistoryMapper 按配对规则回灌）。
    /// 成本核算：每轮 API 调用各记各的 usage/cost（消息行为事实源），outcome 汇总。
    /// </summary>
    private async Task<WorkerOutcome> RunToolLoopAsync(
        OrchestrationContext ctx,
        ModelAssignment assignment,
        StrategyRole role,
        ChatMessage finalMessage,
        IAiProvider provider,
        IReadOnlyList<AiChatMessage> messages,
        ChannelWriter<ChatEvent>? sink,
        TurnBudget? budget,
        CancellationToken ct)
    {
        var sessionId = ctx.Session.Id;
        var definitions = _tools!.Definitions;
        var conversation = new List<AiChatMessage>(messages);
        var runSw = Stopwatch.StartNew();
        var tokensIn = 0;
        var tokensOut = 0;
        var totalCost = 0.0;
        ChatMessage? inFlightToolMessage = null;

        try
        {
            for (var turn = 0; ; turn++)
            {
                if (turn >= MaxToolTurns)
                {
                    var error = $"tool-call loop exceeded the limit ({MaxToolTurns} turns), aborted";
                    _logger.LogWarning(
                        "tool loop aborted: {Provider}/{Model} hit {MaxTurns} turns",
                        assignment.ProviderId, assignment.ModelId, MaxToolTurns);
                    return await FailRunAsync(sink, finalMessage, assignment, error,
                        tokensIn, tokensOut, (int)runSw.ElapsedMilliseconds, countInStats: true,
                        budget, totalCost);
                }

                // ---- 溢出检测（G2-4）：估算 token 超阈值 → Harness Compactor 压缩在途上下文 ----
                conversation = CompactIfOverflowing(conversation);

                var turnSw = Stopwatch.StartNew();
                var request = new AiChatRequest
                {
                    Model = assignment.ModelId,
                    Messages = conversation,
                    Tools = definitions,
                    Stream = false, // 工具轮必须非流式：拿到完整 tool_calls 才能配对执行
                };
                var response = await provider.ChatAsync(request, ct);
                var turnLatency = (int)turnSw.ElapsedMilliseconds;

                var turnTokensIn = 0;
                var turnTokensOut = 0;
                if (response.Usage is not null)
                {
                    turnTokensIn = response.Usage.PromptTokens;
                    turnTokensOut = response.Usage.CompletionTokens;
                }

                tokensIn += turnTokensIn;
                tokensOut += turnTokensOut;
                var turnCost = CostTracker.Estimate(assignment.Profile, turnTokensIn, turnTokensOut) ?? 0.0;
                totalCost += turnCost;

                if (response.ToolCalls.Count == 0)
                {
                    // ---- 最终答复：写回占位消息，按常规收尾 ----
                    runSw.Stop();
                    finalMessage.Content = response.Content;
                    finalMessage.Status = MessageStatus.Completed;
                    finalMessage.TokensIn = turnTokensIn;
                    finalMessage.TokensOut = turnTokensOut;
                    finalMessage.CostUsd = turnCost;
                    finalMessage.LatencyMs = turnLatency;
                    await _sessions.UpdateMessageAsync(finalMessage);

                    if (sink is not null && response.Content.Length > 0)
                    {
                        await sink.WriteAsync(new TextDeltaEvent
                        {
                            SessionId = sessionId,
                            MessageId = finalMessage.Id,
                            Delta = response.Content,
                        }, ct);
                    }

                    _catalog.RecordUsage(assignment.ProviderId, assignment.ModelId, turnLatency, failed: false);
                    await SaveCatalogQuietlyAsync();
                    budget?.AddActual(totalCost);

                    await EmitAsync(sink, new MessageCompletedEvent
                    {
                        SessionId = sessionId,
                        MessageId = finalMessage.Id,
                        TokensIn = turnTokensIn,
                        TokensOut = turnTokensOut,
                        CostUsd = turnCost,
                        LatencyMs = turnLatency,
                    });

                    return new WorkerOutcome(finalMessage.Id, assignment.ProviderId, assignment.ModelId,
                        finalMessage.Content, Succeeded: true, Cancelled: false, null,
                        tokensIn, tokensOut, totalCost, (int)runSw.ElapsedMilliseconds);
                }

                // ---- 工具轮：助手 tool_calls 消息落库（IsFinal == false，仅供配对回灌）----
                var turnMessage = new ChatMessage
                {
                    SessionId = sessionId,
                    Role = ChatRole.Assistant,
                    ProviderId = assignment.ProviderId,
                    ModelId = assignment.ModelId,
                    OrchestrationRole = role,
                    ParentMessageId = finalMessage.Id,
                    Label = finalMessage.Label,
                    Content = response.Content,
                    ToolCallsJson = JsonSerializer.Serialize(response.ToolCalls),
                    IsFinal = false,
                    Status = MessageStatus.Completed,
                    TokensIn = turnTokensIn,
                    TokensOut = turnTokensOut,
                    CostUsd = turnCost,
                    LatencyMs = turnLatency,
                };
                var appendedTurn = await _sessions.AppendMessageAsync(turnMessage);
                if (!appendedTurn.IsSuccess)
                {
                    return await FailRunAsync(sink, finalMessage, assignment,
                        appendedTurn.Error ?? "persist failed",
                        tokensIn, tokensOut, (int)runSw.ElapsedMilliseconds, countInStats: true,
                        budget, totalCost);
                }

                await EmitAsync(sink, new AssistantMessageStarted
                {
                    SessionId = sessionId,
                    MessageId = turnMessage.Id,
                    ProviderId = assignment.ProviderId,
                    ModelId = assignment.ModelId,
                    OrchestrationRole = role,
                    ParentMessageId = finalMessage.Id,
                    Label = finalMessage.Label,
                    HasToolCalls = true,
                });
                if (sink is not null && response.Content.Length > 0)
                {
                    await sink.WriteAsync(new TextDeltaEvent
                    {
                        SessionId = sessionId,
                        MessageId = turnMessage.Id,
                        Delta = response.Content,
                    }, ct);
                }

                await EmitAsync(sink, new MessageCompletedEvent
                {
                    SessionId = sessionId,
                    MessageId = turnMessage.Id,
                    TokensIn = turnTokensIn,
                    TokensOut = turnTokensOut,
                    CostUsd = turnCost,
                    LatencyMs = turnLatency,
                });

                conversation.Add(new AiChatMessage
                {
                    Role = "assistant",
                    Content = response.Content,
                    ToolCalls = response.ToolCalls,
                });

                // ---- 逐个执行工具调用：先裁决后执行，结果如实回传模型 ----
                foreach (var call in response.ToolCalls)
                {
                    var toolMessage = new ChatMessage
                    {
                        SessionId = sessionId,
                        Role = ChatRole.Tool,
                        ProviderId = assignment.ProviderId,
                        ModelId = assignment.ModelId,
                        OrchestrationRole = role,
                        ParentMessageId = turnMessage.Id,
                        Name = call.FunctionName,
                        ToolCallId = call.Id,
                        IsFinal = false,
                        Status = MessageStatus.Pending,
                    };
                    var appendedTool = await _sessions.AppendMessageAsync(toolMessage);
                    if (!appendedTool.IsSuccess)
                    {
                        return await FailRunAsync(sink, finalMessage, assignment,
                            appendedTool.Error ?? "persist failed",
                            tokensIn, tokensOut, (int)runSw.ElapsedMilliseconds, countInStats: true,
                            budget, totalCost);
                    }

                    inFlightToolMessage = toolMessage;
                    await EmitAsync(sink, new ToolCallStartedEvent
                    {
                        SessionId = sessionId,
                        MessageId = toolMessage.Id,
                        ToolCallId = call.Id,
                        ToolName = call.FunctionName,
                        ArgumentsJson = call.ArgumentsJson,
                        ParentMessageId = turnMessage.Id,
                    });

                    var toolSw = Stopwatch.StartNew();
                    var result = await _tools.InvokeAsync(call.FunctionName, call.ArgumentsJson, ct);
                    toolSw.Stop();

                    toolMessage.Content = result.Output;
                    toolMessage.Status = result.Success ? MessageStatus.Completed : MessageStatus.Degraded;
                    toolMessage.Error = result.Success ? null : result.Error;
                    toolMessage.LatencyMs = (int)toolSw.ElapsedMilliseconds;
                    await _sessions.UpdateMessageAsync(toolMessage);
                    inFlightToolMessage = null;

                    await EmitAsync(sink, new ToolCallCompletedEvent
                    {
                        SessionId = sessionId,
                        MessageId = toolMessage.Id,
                        ToolCallId = call.Id,
                        ToolName = call.FunctionName,
                        Success = result.Success,
                        Denied = result.Denied,
                        OutputPreview = ErrorText.Truncate(result.Output),
                        LatencyMs = toolMessage.LatencyMs,
                    });

                    conversation.Add(new AiChatMessage
                    {
                        Role = "tool",
                        Content = result.Output,
                        Name = call.FunctionName,
                        ToolCallId = call.Id,
                    });
                }
            }
        }
        catch (OperationCanceledException)
        {
            runSw.Stop();
            var latency = (int)runSw.ElapsedMilliseconds;

            // 进行中的工具消息与最终答复都落取消态（DB 是事实源，不留 Pending 僵尸）。
            if (inFlightToolMessage is not null)
            {
                inFlightToolMessage.Status = MessageStatus.Cancelled;
                inFlightToolMessage.LatencyMs = latency;
                await _sessions.UpdateMessageAsync(inFlightToolMessage);
            }

            finalMessage.Status = MessageStatus.Cancelled;
            finalMessage.LatencyMs = latency;
            await _sessions.UpdateMessageAsync(finalMessage);

            // 用户取消不是模型质量问题：不计入画像统计，也不产生成本。
            await EmitAsync(sink, new MessageCancelledEvent
            {
                SessionId = sessionId,
                MessageId = finalMessage.Id,
            });

            return new WorkerOutcome(finalMessage.Id, assignment.ProviderId, assignment.ModelId,
                string.Empty, Succeeded: false, Cancelled: true, "cancelled by user",
                tokensIn, tokensOut, 0, latency);
        }
        catch (Exception ex)
        {
            runSw.Stop();
            var error = ErrorText.Truncate(ex.Message) ?? ex.Message;
            _logger.LogWarning(
                "tool loop failed: {Provider}/{Model} after {LatencyMs}ms: {Error}",
                assignment.ProviderId, assignment.ModelId, (int)runSw.ElapsedMilliseconds, error);
            return await FailRunAsync(sink, finalMessage, assignment, error,
                tokensIn, tokensOut, (int)runSw.ElapsedMilliseconds, countInStats: true,
                budget, totalCost);
        }
    }

    /// <summary>
    /// 溢出检测 + 压缩（G2-4）。未装配压缩器/阈值为 0 时原样返回（行为不变）。
    /// 达到阈值 → Compactor（TruncateOldest）压缩 → 头部修复保证 tool 配对完整
    /// （Compactor 逐条丢最旧消息可能拆散 assistant tool_calls 与 tool 应答对，
    /// 请求序列非法）；压缩是设计内行为（非降级），Compactor 自身发布 CompactionTriggeredEvent。
    /// internal for tests（Reviewer-H P1-1 零覆盖修复）：经 InternalsVisibleTo 直测。
    /// </summary>
    internal List<AiChatMessage> CompactIfOverflowing(List<AiChatMessage> conversation)
    {
        var threshold = _compaction?.ThresholdTokens ?? 0;
        if (_compactor is null || threshold <= 0)
        {
            return conversation;
        }

        var estimated = EstimateTokens(conversation);
        if (estimated < threshold)
        {
            return conversation;
        }

        try
        {
            var result = _compactor.Compact(conversation, threshold);
            if (!result.DidCompact)
            {
                return conversation;
            }

            var repaired = DropBrokenToolPairsAtHead(result.Messages);
            _logger.LogInformation(
                "tool-loop context compacted: {Original}→{Compacted} tokens (threshold {Threshold}), {Dropped} broken pair heads dropped",
                result.OriginalTokens, result.CompactedTokens, threshold,
                result.Messages.Count - repaired.Count);
            return repaired;
        }
        catch (Exception ex)
        {
            // 压缩失败不中止对话循环：下一轮按未压缩上下文继续（超限风险由 provider 端显式报错兜底）。
            _logger.LogWarning(
                "[DEGRADED] tool-loop compaction failed, continuing uncompacted: {Error}", ex.Message);
            return conversation;
        }
    }

    /// <summary>4 字符 ≈ 1 token 的既有口径；tool_calls 按函数名+参数 JSON 估算。</summary>
    internal static int EstimateTokens(IReadOnlyList<AiChatMessage> messages)
    {
        var total = 0;
        foreach (var m in messages)
        {
            total += TokenCounter.ApproxTokens(m.Content);
            if (m.ToolCalls is { Count: > 0 })
            {
                foreach (var call in m.ToolCalls)
                {
                    total += TokenCounter.ApproxTokens(call.FunctionName + call.ArgumentsJson);
                }
            }
        }

        return total;
    }

    /// <summary>
    /// 头部修复：从头丢弃不满足配对自洽的消息，直到找到一个合法边界——
    /// 该边界之后（含）不存在"tool 应答缺 assistant 携带方"或"assistant 携带的 tool_call 缺应答"。
    /// 至少保留一条消息（不可能发生全丢：Compactor 保留带保证尾部完整）。
    /// </summary>
    internal static List<AiChatMessage> DropBrokenToolPairsAtHead(IReadOnlyList<AiChatMessage> messages)
    {
        var start = 0;
        while (start < messages.Count && !IsSelfConsistentFrom(messages, start))
        {
            start++;
        }

        if (start >= messages.Count)
        {
            // 理论不可达的兜底：至少保留最后一条（最新上下文不能全丢）。
            return new List<AiChatMessage> { messages[^1] };
        }

        return messages.Skip(start).ToList();
    }

    /// <summary>从 <paramref name="start"/> 起，消息序列是否满足 tool 配对自洽（每个 tool 应答在携带方之后，每个 tool_call 有应答）。</summary>
    private static bool IsSelfConsistentFrom(IReadOnlyList<AiChatMessage> messages, int start)
    {
        for (var i = start; i < messages.Count; i++)
        {
            var m = messages[i];
            var isToolResponse = string.Equals(m.Role, "tool", StringComparison.OrdinalIgnoreCase);
            if (isToolResponse)
            {
                // tool 应答的 assistant 携带方必须在本段内（start..i-1 中存在含该 ToolCallId 的消息）。
                var partnerFound = false;
                for (var j = start; j < i; j++)
                {
                    if (HasToolCallWithId(messages[j], m.ToolCallId))
                    {
                        partnerFound = true;
                        break;
                    }
                }

                if (!partnerFound)
                {
                    return false;
                }
            }
            else if (m.ToolCalls is { Count: > 0 })
            {
                // assistant 携带的每个 tool_call 都必须在本段内（i..end）有应答。
                foreach (var call in m.ToolCalls)
                {
                    var answered = false;
                    for (var j = i + 1; j < messages.Count; j++)
                    {
                        if (string.Equals(messages[j].Role, "tool", StringComparison.OrdinalIgnoreCase)
                            && string.Equals(messages[j].ToolCallId, call.Id, StringComparison.Ordinal))
                        {
                            answered = true;
                            break;
                        }
                    }

                    if (!answered)
                    {
                        return false;
                    }
                }
            }
        }

        return true;
    }

    private static bool HasToolCallWithId(AiChatMessage message, string? toolCallId)
    {
        if (message.ToolCalls is null || toolCallId is null)
        {
            return false;
        }

        foreach (var call in message.ToolCalls)
        {
            if (string.Equals(call.Id, toolCallId, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 失败收尾：最终答复落 Failed 终态 + 画像统计（可选）+ 终态事件 + outcome。
    /// 预算纪律：工具循环中途失败时，此前各轮 API 调用的成本已真实发生且逐条落库，
    /// 必须同样记入 TurnBudget（spentUsd）——否则同轮后续 worker/judge 会再次花满预算，
    /// 静默突破用户单轮上限。outcome.CostUsd 如实返回累计成本而非 0。
    /// </summary>
    private async Task<WorkerOutcome> FailRunAsync(
        ChannelWriter<ChatEvent>? sink,
        ChatMessage finalMessage,
        ModelAssignment assignment,
        string error,
        int tokensIn,
        int tokensOut,
        int latencyMs,
        bool countInStats,
        TurnBudget? budget,
        double spentUsd)
    {
        finalMessage.Status = MessageStatus.Failed;
        finalMessage.Error = error;
        finalMessage.TokensIn = tokensIn;
        finalMessage.TokensOut = tokensOut;
        finalMessage.LatencyMs = latencyMs;
        await _sessions.UpdateMessageAsync(finalMessage);

        if (countInStats)
        {
            _catalog.RecordUsage(assignment.ProviderId, assignment.ModelId, latencyMs, failed: true);
            await SaveCatalogQuietlyAsync();
        }

        if (spentUsd > 0)
        {
            budget?.AddActual(spentUsd);
        }

        await EmitAsync(sink, new MessageFailedEvent
        {
            SessionId = finalMessage.SessionId,
            MessageId = finalMessage.Id,
            Error = error,
        });

        return new WorkerOutcome(finalMessage.Id, assignment.ProviderId, assignment.ModelId,
            string.Empty, Succeeded: false, Cancelled: false, error,
            tokensIn, tokensOut, spentUsd, latencyMs);
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
