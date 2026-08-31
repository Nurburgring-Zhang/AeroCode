// Copyright (c) AeroCode
// ExpertsStrategy — 专家团编排策略（批次 B G2-2，builder-δ）。
// 真实调用 moa-gateway-pro /v1/moa/execute（references/critics/consensus 多专家编排）。
// 诚实语义（与 GatewayOrchestrationFacade 的静默回退不同——这里是用户显式选择的策略）：
// 网关探活/执行失败 → 持久化 Failed 助手消息 + MessageFailedEvent（原因如实透出 UI），
// 绝不回退到进程内策略冒充专家团结果；成功 → 产物落库（mock/兜底信封如实标 Degraded）。
using System;
using System.Collections.Generic;
using System.Threading;
using AeroAgent.Conversation.Models;
using AeroAgent.Conversation.Orchestration;
using AeroAgent.Conversation.Services;
using AeroAgent.Moa.Gateway;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AeroAgent.Moa.Strategies;

public sealed class ExpertsStrategy : IOrchestrationStrategy
{
    /// <summary>产物消息的 provider 标识（与 GatewayOrchestrationFacade 同口径）。</summary>
    public const string GatewayProviderId = "moa-gateway";

    private readonly MoaGatewayClient _client;
    private readonly ISessionService _sessions;
    private readonly ILogger<ExpertsStrategy> _logger;

    public ExpertsStrategy(
        MoaGatewayClient client,
        ISessionService sessions,
        ILogger<ExpertsStrategy>? logger = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _logger = logger ?? NullLogger<ExpertsStrategy>.Instance;
    }

    /// <inheritdoc />
    public OrchestrationStrategy Kind => OrchestrationStrategy.Experts;

    /// <inheritdoc />
    public async IAsyncEnumerable<ChatEvent> ExecuteAsync(
        OrchestrationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var ct = context.CancellationToken;

        var sessionId = context.Session.Id;
        var query = context.History.LastOrDefault(m => m.Role == ChatRole.User)?.Content;
        if (string.IsNullOrWhiteSpace(query))
        {
            var error = await FailAsync(sessionId, "专家团编排失败：会话历史中没有可用的用户任务文本", ct)
                .ConfigureAwait(false);
            yield return error;
            yield break;
        }

        // ---- 1. 健康探活：不可达 = 诚实失败（不白发 execute，也不回退冒充）----
        var health = await _client.HealthAsync(ct).ConfigureAwait(false);
        if (!health.IsSuccess)
        {
            var error = await FailAsync(
                sessionId,
                $"专家团网关不可用（探活失败：{health.Error}）。请确认 moa-gateway-pro 已在 MOA_GATEWAY_URL 指向的地址运行。",
                ct).ConfigureAwait(false);
            yield return error;
            yield break;
        }

        // ---- 2. 真实网关编排 ----
        var executed = await _client.ExecuteAsync(
            new MoaGatewayExecuteRequest { Query = query! }, ct).ConfigureAwait(false);
        if (!executed.IsSuccess || executed.Value is not { } result)
        {
            var reason = executed.Error ?? "empty gateway response";
            var error = await FailAsync(
                sessionId,
                $"专家团编排失败：{reason}" + (executed.StatusCode is null
                    ? string.Empty
                    : $"（HTTP {executed.StatusCode}）"),
                ct).ConfigureAwait(false);
            yield return error;
            yield break;
        }

        var mock = executed.IsMock || result.Mock;
        var label = "MOA 专家团" + (mock ? " · [Mock]" : string.Empty);

        // ---- 3. 产物落库：信封带兜底（fallback_used）或 mock 时如实标 Degraded ----
        var message = new ChatMessage
        {
            SessionId = sessionId,
            Role = ChatRole.Assistant,
            ProviderId = GatewayProviderId,
            ModelId = result.AggregatorModel ?? result.WinnerModel ?? string.Empty,
            OrchestrationRole = StrategyRole.Synthesizer,
            Label = label,
            Content = result.FinalContent,
            Status = result.FallbackUsed || mock ? MessageStatus.Degraded : MessageStatus.Completed,
        };
        var appended = await _sessions.AppendMessageAsync(message).ConfigureAwait(false);
        if (!appended.IsSuccess)
        {
            var error = await FailAsync(
                sessionId, appended.Error ?? "专家团产物落库失败", ct).ConfigureAwait(false);
            yield return error;
            yield break;
        }

        yield return new AssistantMessageStarted
        {
            SessionId = sessionId,
            MessageId = message.Id,
            ProviderId = GatewayProviderId,
            ModelId = message.ModelId,
            OrchestrationRole = StrategyRole.Synthesizer,
            Label = label,
        };

        // 网关一次性返回完整 final_content（无流式）：按单条增量发出，UI 投影口径一致。
        if (result.FinalContent.Length > 0)
        {
            yield return new TextDeltaEvent
            {
                SessionId = sessionId,
                MessageId = message.Id,
                Delta = result.FinalContent,
            };
        }

        _logger.LogInformation(
            "ExpertsStrategy completed via gateway: session={SessionId} references={References} critics={Critics} mock={Mock}",
            sessionId, result.References.Count, result.Critics.Count, mock);

        // 网关信封不含逐模型 usage 汇总：0/0/0 = 真实"未计价"，不估算。
        yield return new MessageCompletedEvent
        {
            SessionId = sessionId,
            MessageId = message.Id,
            TokensIn = 0,
            TokensOut = 0,
            CostUsd = 0,
            LatencyMs = 0,
        };
    }

    /// <summary>失败收尾：Failed 助手消息落库（DB 是事实源）+ MessageFailedEvent 返回给事件流。</summary>
    private async Task<MessageFailedEvent> FailAsync(string sessionId, string error, CancellationToken ct)
    {
        _logger.LogWarning("ExpertsStrategy failed: {Error}", error);
        await _sessions.AppendMessageAsync(new ChatMessage
        {
            SessionId = sessionId,
            Role = ChatRole.Assistant,
            ProviderId = GatewayProviderId,
            Label = "MOA 专家团",
            Status = MessageStatus.Failed,
            Error = error,
        }).ConfigureAwait(false);

        return new MessageFailedEvent
        {
            SessionId = sessionId,
            MessageId = string.Empty,
            Error = error,
        };
    }
}
