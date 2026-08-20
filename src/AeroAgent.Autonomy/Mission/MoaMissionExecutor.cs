using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AeroAgent.Conversation.Orchestration;
using AeroAgent.Conversation.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AeroAgent.Autonomy.Mission;

/// <summary>
/// 生产任务执行器：经 AeroAgent.Conversation 的 <see cref="IChatOrchestrationFacade"/>
/// 门面驱动 AeroAgent.Moa 编排策略真实执行——创建带策略的真实会话、发送任务文本、
/// 消费完整事件流并按消息聚合正文/成本，成败都在结果里（不吞错、不冒充）。
/// </summary>
public sealed class MoaMissionExecutor : IMissionExecutor
{
    private readonly ISessionService _sessions;
    private readonly IChatOrchestrationFacade _facade;
    private readonly ILogger<MoaMissionExecutor> _logger;

    public MoaMissionExecutor(
        ISessionService sessions,
        IChatOrchestrationFacade facade,
        ILogger<MoaMissionExecutor>? logger = null)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _facade = facade ?? throw new ArgumentNullException(nameof(facade));
        _logger = logger ?? NullLogger<MoaMissionExecutor>.Instance;
    }

    /// <inheritdoc/>
    public async Task<MissionExecutionOutcome> ExecuteAsync(MissionExecutionContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(context.EffectiveTaskText))
        {
            return new MissionExecutionOutcome(false, false, string.Empty, "任务文本为空，无法执行", null, 0, 0);
        }

        // 1) 创建真实会话（策略由任务分析驱动，非人工下拉）。
        var sessionResult = await _sessions.CreateSessionAsync(
            strategy: context.Strategy,
            title: $"Mission {context.MissionId[..Math.Min(8, context.MissionId.Length)]}");
        if (!sessionResult.IsSuccess || sessionResult.Value is not { } session)
        {
            return new MissionExecutionOutcome(
                false, false, string.Empty,
                $"创建执行会话失败: {sessionResult.Error ?? "unknown"}", null, 0, 0);
        }

        // 2) 组装真实发送文本：system prompt 作为上下文前缀随用户消息下发
        //    （门面按会话策略编排，策略/历史/持久化全部走真实链路）。
        var payload = string.IsNullOrWhiteSpace(context.SystemPrompt)
            ? context.EffectiveTaskText
            : context.SystemPrompt + Environment.NewLine + Environment.NewLine + context.EffectiveTaskText;

        // 3) 消费事件流，按消息聚合。
        var contents = new Dictionary<string, StringBuilder>();
        var order = new List<string>();
        var assistantMessages = 0;
        var totalCost = 0.0;
        string? failure = null;
        var cancelled = false;

        try
        {
            await foreach (var ev in _facade.SendAsync(session.Id, payload, ct))
            {
                switch (ev)
                {
                    case AssistantMessageStarted started:
                        assistantMessages++;
                        if (!contents.ContainsKey(started.MessageId))
                        {
                            contents[started.MessageId] = new StringBuilder();
                            order.Add(started.MessageId);
                        }
                        break;

                    case TextDeltaEvent delta:
                        if (contents.TryGetValue(delta.MessageId, out var sb))
                        {
                            sb.Append(delta.Delta);
                        }
                        break;

                    case MessageCompletedEvent completed:
                        totalCost += completed.CostUsd;
                        break;

                    case MessageFailedEvent failed:
                        failure ??= failed.Error;
                        break;

                    case MessageCancelledEvent:
                        cancelled = true;
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            return new MissionExecutionOutcome(
                false, true, CollectFinalContent(contents, order),
                null, session.Id, assistantMessages, totalCost);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("任务执行事件流异常: {Error}", ex.Message);
            return new MissionExecutionOutcome(
                false, false, CollectFinalContent(contents, order),
                ex.Message, session.Id, assistantMessages, totalCost);
        }

        var finalContent = CollectFinalContent(contents, order);
        if (cancelled)
        {
            return new MissionExecutionOutcome(false, true, finalContent, null, session.Id, assistantMessages, totalCost);
        }

        if (failure is not null)
        {
            return new MissionExecutionOutcome(false, false, finalContent, failure, session.Id, assistantMessages, totalCost);
        }

        if (assistantMessages == 0)
        {
            return new MissionExecutionOutcome(
                false, false, finalContent,
                "编排未产出任何助手消息（策略事件流为空）", session.Id, 0, totalCost);
        }

        return new MissionExecutionOutcome(true, false, finalContent, null, session.Id, assistantMessages, totalCost);
    }

    /// <summary>取最后一条有正文的编排消息作为最终产出（MOA 多消息时以收尾消息为准）。</summary>
    private static string CollectFinalContent(Dictionary<string, StringBuilder> contents, List<string> order)
    {
        for (var i = order.Count - 1; i >= 0; i--)
        {
            var text = contents[order[i]].ToString().Trim();
            if (text.Length > 0)
            {
                return text;
            }
        }

        return string.Empty;
    }
}
