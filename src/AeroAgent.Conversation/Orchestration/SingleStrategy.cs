using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using AeroAgent.Conversation.Models;
using AeroAgent.Conversation.Services;
using AeroCode.AI.Providers;
using AiChatRequest = AeroCode.AI.Models.ChatRequest;

namespace AeroAgent.Conversation.Orchestration;

/// <summary>
/// 单模型直连策略：按会话偏好（或全局默认）解析 provider/model，
/// 真实流式调用，逐块产出事件。
///
/// 异常边界约定：本策略不在迭代器内捕获异常（C# 禁止在带 catch 的 try
/// 中 yield），流式调用抛出的异常向上传播，由
/// <see cref="ChatOrchestrationFacade"/> 统一收容并落库失败状态。
/// 正常收尾时本策略负责持久化完成态与真实用量。
/// </summary>
public sealed class SingleStrategy : IOrchestrationStrategy
{
    private readonly ISessionService _sessions;

    public SingleStrategy(ISessionService sessions)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
    }

    public OrchestrationStrategy Kind => OrchestrationStrategy.Single;

    public async IAsyncEnumerable<ChatEvent> ExecuteAsync(OrchestrationContext context)
    {
        var ct = context.CancellationToken;

        // ---- 解析 provider / model（会话偏好 → 全局默认）----
        var providerId = context.Session.PreferredProviderId
                         ?? context.Providers.DefaultProviderId;
        if (!context.Providers.TryGetConfig(providerId, out var config))
        {
            yield return new MessageFailedEvent
            {
                SessionId = context.Session.Id,
                MessageId = string.Empty,
                Error = $"provider '{providerId}' not configured",
            };
            yield break;
        }

        var model = context.Session.PreferredModel ?? config.DefaultModel;
        var provider = context.Providers.Get(providerId);

        // ---- 持久化占位助手消息（前端先渲染骨架）----
        var message = new ChatMessage
        {
            SessionId = context.Session.Id,
            Role = ChatRole.Assistant,
            ProviderId = providerId,
            ModelId = model,
            OrchestrationRole = StrategyRole.None,
            IsFinal = true,
            Status = MessageStatus.Streaming,
        };
        var appended = await _sessions.AppendMessageAsync(message);
        if (!appended.IsSuccess)
        {
            yield return new MessageFailedEvent
            {
                SessionId = context.Session.Id,
                MessageId = message.Id,
                Error = appended.Error ?? "persist failed",
            };
            yield break;
        }

        yield return new AssistantMessageStarted
        {
            SessionId = context.Session.Id,
            MessageId = message.Id,
            ProviderId = providerId,
            ModelId = model,
            OrchestrationRole = StrategyRole.None,
        };

        // ---- 真实调用（异常向上传播，门面收容）----
        var request = new AiChatRequest
        {
            Model = model,
            Messages = HistoryMapper.ToProviderMessages(context.History),
            Stream = provider.SupportsStreaming,
        };

        var sb = new StringBuilder();
        var sw = Stopwatch.StartNew();
        var tokensIn = 0;
        var tokensOut = 0;

        if (provider.SupportsStreaming)
        {
            await foreach (var chunk in provider.StreamChatAsync(request, ct))
            {
                if (!string.IsNullOrEmpty(chunk.DeltaContent))
                {
                    sb.Append(chunk.DeltaContent);
                    yield return new TextDeltaEvent
                    {
                        SessionId = context.Session.Id,
                        MessageId = message.Id,
                        Delta = chunk.DeltaContent,
                    };
                }

                if (!string.IsNullOrEmpty(chunk.DeltaReasoning))
                {
                    yield return new ReasoningDeltaEvent
                    {
                        SessionId = context.Session.Id,
                        MessageId = message.Id,
                        Delta = chunk.DeltaReasoning,
                    };
                }
            }
        }
        else
        {
            var response = await provider.ChatAsync(request, ct);
            sb.Append(response.Content);
            if (response.Content.Length > 0)
            {
                yield return new TextDeltaEvent
                {
                    SessionId = context.Session.Id,
                    MessageId = message.Id,
                    Delta = response.Content,
                };
            }

            if (response.Usage is not null)
            {
                tokensIn = response.Usage.PromptTokens;
                tokensOut = response.Usage.CompletionTokens;
            }
        }

        sw.Stop();

        // ---- 正常收尾：真实状态落库 ----
        message.Content = sb.ToString();
        message.Status = MessageStatus.Completed;
        message.TokensIn = tokensIn;
        message.TokensOut = tokensOut;
        message.LatencyMs = (int)sw.ElapsedMilliseconds;
        await _sessions.UpdateMessageAsync(message);

        yield return new MessageCompletedEvent
        {
            SessionId = context.Session.Id,
            MessageId = message.Id,
            TokensIn = tokensIn,
            TokensOut = tokensOut,
            CostUsd = 0, // Single 暂不接入画像计价（MOA 各策略经 WorkerRunner 核算）；未计价如实为 0
            LatencyMs = message.LatencyMs,
        };
    }
}
