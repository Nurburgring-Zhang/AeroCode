using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Channels;
using System.Threading.Tasks;
using AeroAgent.Conversation.Models;
using AeroAgent.Conversation.Orchestration;
using AeroAgent.Conversation.Services;
using AeroAgent.Moa.Accounting;
using AeroAgent.Moa.Aggregation;
using AeroAgent.Moa.Assignment;
using AeroAgent.Moa.Profiles;
using AiChatMessage = AeroCode.AI.Models.ChatMessage;

namespace AeroAgent.Moa.Strategies;

/// <summary>
/// 集成策略：N 个不同模型对同一请求并行独立作答，judge 模型裁决/合成最终答案。
/// 候选作答以 StrategyRole.Worker 消息持久化（归属可查），judge 输出为最终答复。
/// 诚实降级：部分候选失败时如实告知 judge；全部失败时本轮如实报失败。
/// </summary>
public sealed class EnsembleStrategy : IOrchestrationStrategy
{
    private readonly ISessionService _sessions;
    private readonly WorkerRunner _runner;
    private readonly ModelResolver _resolver;
    private readonly ModelAssigner _assigner;
    private readonly Synthesizer _synthesizer;
    private readonly MoaOptions _options;

    public EnsembleStrategy(
        ISessionService sessions,
        WorkerRunner runner,
        ModelResolver resolver,
        ModelAssigner assigner,
        Synthesizer synthesizer,
        MoaOptions options)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _assigner = assigner ?? throw new ArgumentNullException(nameof(assigner));
        _synthesizer = synthesizer ?? throw new ArgumentNullException(nameof(synthesizer));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public OrchestrationStrategy Kind => OrchestrationStrategy.Ensemble;

    public async IAsyncEnumerable<ChatEvent> ExecuteAsync(OrchestrationContext context)
    {
        var ct = context.CancellationToken;
        var sessionId = context.Session.Id;
        var userText = context.History.LastOrDefault(m => m.Role == ChatRole.User)?.Content ?? string.Empty;
        var budget = new TurnBudget(_options.MaxUsdPerTurn);

        // ---- 1. 选 N 个互不相同的候选模型 ----
        var size = Math.Clamp(_options.EnsembleSize, 2, 4);
        var candidates = _assigner
            .RankCandidates(ModelStrength.General)
            .DistinctBy(c => c.Key)
            .Take(size)
            .ToList();

        if (candidates.Count < 2)
        {
            yield return new MessageFailedEvent
            {
                SessionId = sessionId,
                MessageId = string.Empty,
                Error = $"集成策略需要至少 2 个已配置模型，当前只有 {candidates.Count} 个候选",
            };
            yield break;
        }

        // ---- 2. 并行作答（事件经 channel 汇入统一流）----
        var channel = Channel.CreateUnbounded<ChatEvent>();
        var historyMessages = HistoryMapper.ToProviderMessages(context.History);

        var workersTask = Task.Run(async () =>
        {
            try
            {
                var tasks = candidates.Select((assignment, i) =>
                {
                    var label = $"候选 {Synthesizer.CandidateLabels[i % Synthesizer.CandidateLabels.Length]}";
                    return _runner.RunAsync(
                        context, assignment, StrategyRole.Worker,
                        parentMessageId: null, label,
                        historyMessages, stream: false, isFinal: false,
                        sink: channel.Writer, budget, ct);
                });
                return await Task.WhenAll(tasks);
            }
            finally
            {
                channel.Writer.Complete();
            }
        });

        await foreach (var ev in EventPump.DrainAsync(channel, ct))
        {
            yield return ev;
        }

        var outcomes = await workersTask;
        if (ct.IsCancellationRequested)
        {
            yield break;
        }

        var results = outcomes
            .Select((o, i) => new SubtaskResult(
                $"{Synthesizer.CandidateLabels[i % Synthesizer.CandidateLabels.Length]} · {o.ProviderId}/{o.ModelId}",
                o.Succeeded,
                o.Succeeded ? null : o.Error,
                o.Content))
            .ToList();

        if (!results.Any(r => r.Succeeded))
        {
            var reasons = string.Join("；", results.Select(r => $"{r.Title}: {r.Error}"));
            yield return new MessageFailedEvent
            {
                SessionId = sessionId,
                MessageId = string.Empty,
                Error = $"所有候选模型均失败：{reasons}",
            };
            yield break;
        }

        // ---- 3. judge 裁决合成（流式，面向用户的最终答复）----
        var judgeAssignment = _resolver.Resolve(_options.Judge, ModelStrength.Review);
        if (judgeAssignment is null)
        {
            yield return new MessageFailedEvent
            {
                SessionId = sessionId,
                MessageId = string.Empty,
                Error = "没有已配置的模型可用于裁决",
            };
            yield break;
        }

        var judgeChannel = Channel.CreateUnbounded<ChatEvent>();
        var judgeTask = Task.Run(async () =>
        {
            try
            {
                return await _synthesizer.SynthesizeAsync(
                    context, judgeAssignment, StrategyRole.Judge,
                    parentMessageId: null, label: "裁决合成",
                    Synthesizer.BuildEnsemblePrompt(userText, results),
                    isFinal: true, budget, judgeChannel.Writer, ct);
            }
            finally
            {
                judgeChannel.Writer.Complete();
            }
        });

        await foreach (var ev in EventPump.DrainAsync(judgeChannel, ct))
        {
            yield return ev;
        }

        var judgeOutcome = await judgeTask;

        // ---- 4. 降级标注：有候选失败但裁决成功 ----
        var anyFailed = results.Any(r => !r.Succeeded);
        if (anyFailed && judgeOutcome.Succeeded && !string.IsNullOrEmpty(judgeOutcome.MessageId))
        {
            var messages = await _sessions.GetMessagesAsync(sessionId);
            if (messages.IsSuccess && messages.Value is not null)
            {
                var target = messages.Value.FirstOrDefault(m => m.Id == judgeOutcome.MessageId);
                if (target is not null)
                {
                    target.Status = MessageStatus.Degraded;
                    await _sessions.UpdateMessageAsync(target);
                }
            }
        }
    }
}
