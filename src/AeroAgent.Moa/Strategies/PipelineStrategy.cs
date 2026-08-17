using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Channels;
using System.Threading.Tasks;
using AeroAgent.Conversation.Models;
using AeroAgent.Conversation.Orchestration;
using AeroAgent.Moa.Accounting;
using AeroAgent.Moa.Profiles;
using AiChatMessage = AeroCode.AI.Models.ChatMessage;

namespace AeroAgent.Moa.Strategies;

/// <summary>
/// 流水线策略：起草模型 → 评审模型 → 修订模型顺序接力。每阶段产物都是一条
/// 真实消息（StrategyRole + Label 标注阶段，ParentMessageId 串成接力链），
/// 最终修订稿流式输出给用户。任一阶段失败/取消即如实停止
/// （失败消息已由 WorkerRunner 落库并发出终态事件）。
/// </summary>
public sealed class PipelineStrategy : IOrchestrationStrategy
{
    private readonly WorkerRunner _runner;
    private readonly ModelResolver _resolver;
    private readonly MoaOptions _options;

    public PipelineStrategy(WorkerRunner runner, ModelResolver resolver, MoaOptions options)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public OrchestrationStrategy Kind => OrchestrationStrategy.Pipeline;

    public async IAsyncEnumerable<ChatEvent> ExecuteAsync(OrchestrationContext context)
    {
        var ct = context.CancellationToken;
        var sessionId = context.Session.Id;
        var userText = context.History.LastOrDefault(m => m.Role == ChatRole.User)?.Content ?? string.Empty;
        var budget = new TurnBudget(_options.MaxUsdPerTurn);

        // 起草强项按请求内容粗判：代码类请求用 code 画像，否则 writing。
        var draftStrength = RouterStrategy.HeuristicCategory(userText) == ModelStrength.Code
            ? ModelStrength.Code
            : ModelStrength.Writing;
        var history = HistoryMapper.ToProviderMessages(context.History);

        // ---- 阶段 1：起草（非流式，阶段产物可见）----
        var drafter = _resolver.Resolve(null, draftStrength);
        if (drafter is null)
        {
            yield return Fail(sessionId, "没有已配置的模型可用于起草");
            yield break;
        }

        var draftMessages = new List<AiChatMessage>(history)
        {
            new()
            {
                Role = "system",
                Content = "你是起草者。根据对话中的用户请求产出第一版完整稿件。只输出稿件正文，不要输出解释或前言。",
            },
        };

        var draftChannel = Channel.CreateUnbounded<ChatEvent>();
        var draftTask = Task.Run(async () =>
        {
            try
            {
                return await _runner.RunAsync(
                    context, drafter, StrategyRole.Worker,
                    parentMessageId: null, label: "起草",
                    draftMessages, stream: false, isFinal: false, sink: draftChannel.Writer, budget, ct);
            }
            finally
            {
                draftChannel.Writer.Complete();
            }
        });

        await foreach (var ev in EventPump.DrainAsync(draftChannel, ct))
        {
            yield return ev;
        }

        var draft = await draftTask;
        if (draft.Cancelled || !draft.Succeeded)
        {
            yield break; // 终态已由 runner 落库并报事件。
        }

        // ---- 阶段 2：评审（复用 Judge 绑定；无绑定则按 review 画像分配）----
        var reviewer = _resolver.Resolve(_options.Judge, ModelStrength.Review);
        if (reviewer is null)
        {
            yield return Fail(sessionId, "没有已配置的模型可用于评审");
            yield break;
        }

        var reviewMessages = new List<AiChatMessage>
        {
            new()
            {
                Role = "system",
                Content = "你是评审者。严格评审给出的稿件，逐条列出具体问题与改进建议（内容准确性、结构、表达）。只输出评审意见，不要重写稿件。",
            },
            new()
            {
                Role = "user",
                Content = $"用户原始请求：\n{userText}\n\n待评审稿件：\n{draft.Content}",
            },
        };

        var reviewChannel = Channel.CreateUnbounded<ChatEvent>();
        var reviewTask = Task.Run(async () =>
        {
            try
            {
                return await _runner.RunAsync(
                    context, reviewer, StrategyRole.Judge,
                    parentMessageId: draft.MessageId, label: "评审",
                    reviewMessages, stream: false, isFinal: false, sink: reviewChannel.Writer, budget, ct);
            }
            finally
            {
                reviewChannel.Writer.Complete();
            }
        });

        await foreach (var ev in EventPump.DrainAsync(reviewChannel, ct))
        {
            yield return ev;
        }

        var review = await reviewTask;
        if (review.Cancelled || !review.Succeeded)
        {
            yield break;
        }

        // ---- 阶段 3：修订（流式，面向用户的最终稿）----
        var reviser = _resolver.Resolve(null, draftStrength);
        if (reviser is null)
        {
            yield return Fail(sessionId, "没有已配置的模型可用于修订");
            yield break;
        }

        var reviseMessages = new List<AiChatMessage>
        {
            new()
            {
                Role = "system",
                Content = "你是修订者。根据评审意见修订稿件，产出最终稿。只输出最终稿正文，不要输出解释或变更清单。",
            },
            new()
            {
                Role = "user",
                Content = $"用户原始请求：\n{userText}\n\n初稿：\n{draft.Content}\n\n评审意见：\n{review.Content}",
            },
        };

        var reviseChannel = Channel.CreateUnbounded<ChatEvent>();
        var reviseTask = Task.Run(async () =>
        {
            try
            {
                await _runner.RunAsync(
                    context, reviser, StrategyRole.Worker,
                    parentMessageId: review.MessageId, label: "修订终稿",
                    reviseMessages, stream: true, isFinal: true, sink: reviseChannel.Writer, budget, ct);
            }
            finally
            {
                reviseChannel.Writer.Complete();
            }
        });

        await foreach (var ev in EventPump.DrainAsync(reviseChannel, ct))
        {
            yield return ev;
        }

        await reviseTask; // 终态已由 runner 落库。
    }

    private static MessageFailedEvent Fail(string sessionId, string error) => new()
    {
        SessionId = sessionId,
        MessageId = string.Empty,
        Error = error,
    };
}
