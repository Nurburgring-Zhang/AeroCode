using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
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

    // 会话级轮次闸门：同一会话同时最多一个进行中的轮次。
    // 并发 SendAsync 会交叉"追加用户消息"与"加载完整历史"，把上下文串错；
    // 闸门按 sessionId 隔离，跨会话并行不受影响。
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _sessionGates = new();

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

        // 会话级轮次串行化（闸门随枚举完成或消费方 Dispose 释放）。
        await using var gateLease = await SessionGateLease.AcquireAsync(_sessionGates, sessionId, ct);

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
        // MOA 并行编排（Ensemble/Decompose）同时有多条消息在途：
        // 逐消息跟踪累积内容与在途集合，异常收容时各归各位，绝不串行混淆。
        var totalMessages = 0;
        var totalCost = 0.0;
        var inFlight = new Dictionary<string, StringBuilder>();

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
                    await MarkInFlightTerminalAsync(
                        sessionId, inFlight,
                        MessageStatus.Cancelled, "cancelled by user");

                    terminalFromException = new MessageCancelledEvent
                    {
                        SessionId = sessionId,
                        MessageId = string.Empty, // 轮级取消：在途消息已各自落库为 Cancelled
                    };
                    moved = false;
                }
                catch (Exception ex)
                {
                    await MarkInFlightTerminalAsync(
                        sessionId, inFlight,
                        MessageStatus.Failed, ex.Message);

                    terminalFromException = new MessageFailedEvent
                    {
                        SessionId = sessionId,
                        MessageId = string.Empty, // 轮级失败：在途消息已各自落库为 Failed
                        Error = ErrorText.Truncate(ex.Message) ?? ex.Message,
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
                switch (current)
                {
                    case AssistantMessageStarted started:
                        inFlight[started.MessageId] = new StringBuilder();
                        totalMessages++;
                        break;
                    case TextDeltaEvent delta:
                        if (inFlight.TryGetValue(delta.MessageId, out var sb))
                        {
                            sb.Append(delta.Delta);
                        }

                        break;
                    case MessageCompletedEvent completed:
                        inFlight.Remove(completed.MessageId);
                        totalCost += completed.CostUsd;
                        break;
                    case MessageFailedEvent failed:
                        inFlight.Remove(failed.MessageId);
                        break;
                    case MessageCancelledEvent cancelled:
                        inFlight.Remove(cancelled.MessageId);
                        break;
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

    /// <summary>
    /// 把异常时刻所有在途消息各自落为终态，保留各自已流出的部分内容。
    /// 会话消息只加载一次（在途消息可能多条，逐条加载是 O(N²)）。尽力而为，不抛出。
    /// </summary>
    private async Task MarkInFlightTerminalAsync(
        string sessionId,
        Dictionary<string, StringBuilder> inFlight,
        MessageStatus status,
        string error)
    {
        if (inFlight.Count == 0)
        {
            return;
        }

        var truncated = ErrorText.Truncate(error);
        try
        {
            var messages = await _sessions.GetMessagesAsync(sessionId);
            if (messages.IsSuccess && messages.Value is not null)
            {
                foreach (var (messageId, accumulated) in inFlight)
                {
                    var target = messages.Value.FirstOrDefault(m => m.Id == messageId);
                    if (target is null)
                    {
                        continue;
                    }

                    target.Status = status;
                    target.Error = truncated;
                    if (accumulated.Length > 0)
                    {
                        target.Content = accumulated.ToString();
                    }

                    await _sessions.UpdateMessageAsync(target);
                }
            }
        }
        catch (Exception ex)
        {
            // 收尾落库失败不应覆盖原始异常语义，但必须可见。
            _logger.LogWarning(
                "failed to persist terminal status {Status} for in-flight messages: {Error}",
                status, ex.Message);
        }
        finally
        {
            inFlight.Clear();
        }
    }

    /// <summary>
    /// 会话轮次闸门租约：持有期间该会话至多一个进行中的轮次；
    /// await using 在枚举正常结束、异常终止或消费方 Dispose 时都会释放。
    /// </summary>
    private sealed class SessionGateLease : IAsyncDisposable
    {
        private readonly SemaphoreSlim _gate;

        private SessionGateLease(SemaphoreSlim gate)
        {
            _gate = gate;
        }

        public static async ValueTask<SessionGateLease> AcquireAsync(
            ConcurrentDictionary<string, SemaphoreSlim> gates,
            string sessionId,
            CancellationToken ct)
        {
            var gate = gates.GetOrAdd(sessionId, static _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(ct).ConfigureAwait(false);
            return new SessionGateLease(gate);
        }

        public ValueTask DisposeAsync()
        {
            _gate.Release();
            return ValueTask.CompletedTask;
        }
    }
}
