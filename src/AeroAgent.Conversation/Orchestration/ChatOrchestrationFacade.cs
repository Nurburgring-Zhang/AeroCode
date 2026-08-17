using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using AeroAgent.Conversation.Models;
using AeroAgent.Conversation.Services;
using AeroCode.AI.Providers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AeroAgent.Conversation.Orchestration;

/// <summary>
/// 统一对话门面：用户输入 → 持久化 → 按会话策略编排 → 事件流。
/// UI 只需订阅 <see cref="SendAsync"/> 的事件流。
/// </summary>
public interface IChatOrchestrationFacade
{
    /// <summary>发送一条用户消息并返回编排事件流。</summary>
    IAsyncEnumerable<ChatEvent> SendAsync(
        string sessionId, string userText, CancellationToken ct = default);
}

/// <summary>
/// <see cref="IChatOrchestrationFacade"/> 默认实现。
/// 策略注册表按 <see cref="OrchestrationStrategy"/> 路由；会话策略未注册时
/// 如实回退 Single。策略流中的异常（含取消）由本门面收容：把进行中的助手
/// 消息落库为 Failed/Cancelled，再向事件流补发终态事件——DB 状态是事实源。
/// </summary>
public sealed class ChatOrchestrationFacade : IChatOrchestrationFacade
{
    private readonly ISessionService _sessions;
    private readonly IProviderRegistry _providers;
    private readonly IReadOnlyDictionary<OrchestrationStrategy, IOrchestrationStrategy> _strategies;
    private readonly ILogger<ChatOrchestrationFacade> _logger;

    public ChatOrchestrationFacade(
        ISessionService sessions,
        IProviderRegistry providers,
        IEnumerable<IOrchestrationStrategy> strategies,
        ILogger<ChatOrchestrationFacade>? logger = null)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _providers = providers ?? throw new ArgumentNullException(nameof(providers));
        _strategies = (strategies ?? throw new ArgumentNullException(nameof(strategies)))
            .ToDictionary(s => s.Kind);
        _logger = logger ?? NullLogger<ChatOrchestrationFacade>.Instance;
    }

    public async IAsyncEnumerable<ChatEvent> SendAsync(
        string sessionId,
        string userText,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userText))
        {
            yield return new MessageFailedEvent
            {
                SessionId = sessionId,
                MessageId = string.Empty,
                Error = "user text must not be empty",
            };
            yield break;
        }

        var sessionResult = await _sessions.GetSessionAsync(sessionId);
        if (!sessionResult.IsSuccess || sessionResult.Value is not { } session)
        {
            yield return new MessageFailedEvent
            {
                SessionId = sessionId,
                MessageId = string.Empty,
                Error = sessionResult.Error ?? "session not found",
            };
            yield break;
        }

        // ---- 持久化用户消息 ----
        var userMessage = new ChatMessage
        {
            SessionId = sessionId,
            Role = ChatRole.User,
            Content = userText,
            Status = MessageStatus.Completed,
        };
        var appended = await _sessions.AppendMessageAsync(userMessage);
        if (!appended.IsSuccess)
        {
            yield return new MessageFailedEvent
            {
                SessionId = sessionId,
                MessageId = userMessage.Id,
                Error = appended.Error ?? "failed to persist user message",
            };
            yield break;
        }

        // ---- 加载完整历史（含刚写入的用户消息）----
        var historyResult = await _sessions.GetMessagesAsync(sessionId);
        if (!historyResult.IsSuccess || historyResult.Value is not { } history)
        {
            yield return new MessageFailedEvent
            {
                SessionId = sessionId,
                MessageId = userMessage.Id,
                Error = historyResult.Error ?? "failed to load history",
            };
            yield break;
        }

        if (!_strategies.TryGetValue(session.Strategy, out var strategy))
        {
            // 未注册的策略如实回退 Single（Phase 2 会补齐其余策略）。
            strategy = _strategies[OrchestrationStrategy.Single];
        }

        var context = new OrchestrationContext
        {
            Session = session,
            History = history,
            UserMessageId = userMessage.Id,
            Providers = _providers,
            CancellationToken = ct,
        };

        // ---- 手动枚举策略流：异常收容在 MoveNextAsync 周围，
        //      yield 位于 try 之外（C# 禁止在带 catch 的 try 内 yield）。
        var totalMessages = 0;
        var totalCost = 0.0;
        string? currentMessageId = null;
        var accumulated = new System.Text.StringBuilder();

        var enumerator = strategy.ExecuteAsync(context).GetAsyncEnumerator(ct);
        try
        {
            while (true)
            {
                ChatEvent? ev = null;
                ChatEvent? terminalFromException = null;
                bool moved;
                try
                {
                    moved = await enumerator.MoveNextAsync();
                    if (moved)
                    {
                        ev = enumerator.Current;
                    }
                }
                catch (OperationCanceledException)
                {
                    if (currentMessageId is not null)
                    {
                        await MarkTerminalAsync(
                            sessionId, currentMessageId,
                            MessageStatus.Cancelled, "cancelled by user",
                            accumulated.ToString());
                    }

                    terminalFromException = new MessageCancelledEvent
                    {
                        SessionId = sessionId,
                        MessageId = currentMessageId ?? string.Empty,
                    };
                    moved = false;
                }
                catch (Exception ex)
                {
                    if (currentMessageId is not null)
                    {
                        await MarkTerminalAsync(
                            sessionId, currentMessageId,
                            MessageStatus.Failed, ex.Message,
                            accumulated.ToString());
                    }

                    terminalFromException = new MessageFailedEvent
                    {
                        SessionId = sessionId,
                        MessageId = currentMessageId ?? string.Empty,
                        Error = ex.Message,
                    };
                    moved = false;
                }

                if (!moved)
                {
                    // 终态补发事件在 try-catch 之外产出（C# 禁止 catch 内 yield）。
                    if (terminalFromException is not null)
                    {
                        yield return terminalFromException;
                    }

                    break;
                }

                var current = ev!;
                if (current is AssistantMessageStarted started)
                {
                    currentMessageId = started.MessageId;
                    totalMessages++;
                    accumulated.Clear();
                }

                if (current is TextDeltaEvent delta)
                {
                    accumulated.Append(delta.Delta);
                }

                if (current is MessageCompletedEvent completed)
                {
                    totalCost += completed.CostUsd;
                }

                yield return current;
            }
        }
        finally
        {
            await enumerator.DisposeAsync();
        }

        yield return new TurnCompletedEvent
        {
            SessionId = sessionId,
            MessageId = userMessage.Id,
            Strategy = session.Strategy,
            TotalMessages = totalMessages,
            TotalCostUsd = totalCost,
        };
    }

    /// <summary>把进行中的助手消息落为终态（失败/取消），并保留已流出的部分内容。尽力而为，不抛出。</summary>
    private async Task MarkTerminalAsync(
        string sessionId, string messageId, MessageStatus status, string? error,
        string partialContent)
    {
        try
        {
            var messages = await _sessions.GetMessagesAsync(sessionId);
            if (!messages.IsSuccess || messages.Value is null)
            {
                return;
            }

            var target = messages.Value.FirstOrDefault(m => m.Id == messageId);
            if (target is null)
            {
                return;
            }

            target.Status = status;
            target.Error = error;
            if (partialContent.Length > 0)
            {
                target.Content = partialContent;
            }

            await _sessions.UpdateMessageAsync(target);
        }
        catch (Exception ex)
        {
            // 收尾落库失败不应覆盖原始异常语义，但必须可见。
            _logger.LogWarning(
                "failed to persist terminal status {Status} for message {MessageId}: {Error}",
                status, messageId, ex.Message);
        }
    }
}
