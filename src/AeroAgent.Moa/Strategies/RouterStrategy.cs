using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using System.Threading.Tasks;
using AeroAgent.Conversation.Models;
using AeroAgent.Conversation.Orchestration;
using AeroAgent.Moa.Accounting;
using AeroAgent.Moa.Profiles;
using AiChatMessage = AeroCode.AI.Models.ChatMessage;

namespace AeroAgent.Moa.Strategies;

/// <summary>
/// 路由策略：快速模型先把用户请求分类到强项类别，再按画像把任务路由给最优模型。
/// 路由决策作为一条 StrategyRole.Router 的真实消息持久化（调度可审计）。
/// 路由模型不可用时如实降级为关键词启发式分类——不静默、不伪造。
/// </summary>
public sealed class RouterStrategy : IOrchestrationStrategy
{
    private readonly WorkerRunner _runner;
    private readonly ModelResolver _resolver;
    private readonly MoaOptions _options;

    public RouterStrategy(WorkerRunner runner, ModelResolver resolver, MoaOptions options)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public OrchestrationStrategy Kind => OrchestrationStrategy.Router;

    public async IAsyncEnumerable<ChatEvent> ExecuteAsync(OrchestrationContext context)
    {
        var ct = context.CancellationToken;
        var userText = context.History.LastOrDefault(m => m.Role == ChatRole.User)?.Content ?? string.Empty;
        var budget = new TurnBudget(_options.MaxUsdPerTurn);

        // ---- 1. 路由模型：偏好 Fast 档 ----
        var routerAssignment = _resolver.Resolve(_options.Router, ModelStrength.General, SpeedTier.Fast);
        if (routerAssignment is null)
        {
            yield return Fail(context.Session.Id, "没有已配置的模型可用于路由");
            yield break;
        }

        var routerMessages = new List<AiChatMessage>
        {
            new() { Role = "system", Content = BuildClassifierPrompt() },
            new() { Role = "user", Content = userText },
        };

        var routerChannel = Channel.CreateUnbounded<ChatEvent>();
        var routerTask = Task.Run(async () =>
        {
            try
            {
                return await _runner.RunAsync(
                    context, routerAssignment, StrategyRole.Router,
                    parentMessageId: null, label: "路由分类",
                    routerMessages, stream: false, isFinal: false, sink: routerChannel.Writer, budget, ct);
            }
            finally
            {
                routerChannel.Writer.Complete();
            }
        });

        await foreach (var ev in EventPump.DrainAsync(routerChannel, ct))
        {
            yield return ev;
        }

        var routerOutcome = await routerTask;
        if (routerOutcome.Cancelled)
        {
            yield break;
        }

        // ---- 2. 解析类别：LLM 输出优先，解析失败/调用失败退化为关键词启发式 ----
        var category = routerOutcome.Succeeded
            ? ParseCategory(routerOutcome.Content, userText)
            : HeuristicCategory(userText);

        // ---- 3. 按类别分配 worker 并流式作答 ----
        var workerAssignment = _resolver.Resolve(null, category);
        if (workerAssignment is null)
        {
            yield return Fail(context.Session.Id, $"没有已配置的模型可处理类别 '{category}'");
            yield break;
        }

        var workerChannel = Channel.CreateUnbounded<ChatEvent>();
        var workerTask = Task.Run(async () =>
        {
            try
            {
                await _runner.RunAsync(
                    context, workerAssignment, StrategyRole.Worker,
                    parentMessageId: routerOutcome.MessageId, label: null,
                    HistoryMapper.ToProviderMessages(context.History),
                    stream: true, isFinal: true, sink: workerChannel.Writer, budget, ct);
            }
            finally
            {
                workerChannel.Writer.Complete();
            }
        });

        await foreach (var ev in EventPump.DrainAsync(workerChannel, ct))
        {
            yield return ev;
        }

        await workerTask; // 终态已由 runner 落库；此处仅等待收尾。
    }

    internal static string BuildClassifierPrompt()
    {
        var sb = new StringBuilder();
        sb.AppendLine("你是请求分类器。把用户请求分类到以下单一类别，只输出 JSON：");
        sb.AppendLine("{\"category\":\"<类别>\",\"reason\":\"<不超过20字的理由>\"}");
        sb.AppendLine("可选类别：");
        sb.AppendLine("- code：编程、代码、调试、SQL、脚本、API 实现");
        sb.AppendLine("- writing：文章、文案、邮件、润色、创作");
        sb.AppendLine("- analysis：分析、总结、对比、评估、研究报告");
        sb.AppendLine("- translation：语言互译");
        sb.AppendLine("- math：计算、证明、数学推导");
        sb.AppendLine("- planning：计划、方案、任务拆解");
        sb.AppendLine("- review：评审、审查、找问题");
        sb.AppendLine("- general：以上都不匹配的日常问答");
        return sb.ToString();
    }

    /// <summary>解析路由模型输出。LLM 输出不可解析时退回关键词启发式（绝不抛错）。</summary>
    internal static string ParseCategory(string routerOutput, string userText)
    {
        if (!string.IsNullOrWhiteSpace(routerOutput))
        {
            var text = routerOutput.Trim();
            var start = text.IndexOf('{');
            var end = text.LastIndexOf('}');
            if (start >= 0 && end > start)
            {
                try
                {
                    using var doc = JsonDocument.Parse(text[start..(end + 1)]);
                    if (doc.RootElement.TryGetProperty("category", out var catEl))
                    {
                        var normalized = ModelStrength.Normalize(catEl.GetString());
                        if (ModelStrength.All.Contains(normalized))
                        {
                            return normalized;
                        }
                    }
                }
                catch (JsonException)
                {
                    // 落入启发式。
                }
            }
        }

        return HeuristicCategory(userText);
    }

    /// <summary>关键词启发式分类（中英文）。确定性、可测试，作为 LLM 路由的兜底。</summary>
    internal static string HeuristicCategory(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return ModelStrength.General;
        }

        if (ContainsAny(text, "翻译", "译成", "translate", "translation"))
        {
            return ModelStrength.Translation;
        }

        if (ContainsAny(text, "代码", "编程", "函数", "调试", "编译", "脚本", "bug", "code", "debug", "SQL", "实现一个", "写个程序", "API"))
        {
            return ModelStrength.Code;
        }

        if (ContainsAny(text, "计算", "证明", "数学", "方程", "微积分", "概率", "math", "prove"))
        {
            return ModelStrength.Math;
        }

        if (ContainsAny(text, "评审", "审查", "review", "找问题", "检查这段"))
        {
            return ModelStrength.Review;
        }

        if (ContainsAny(text, "规划", "计划", "方案", "拆解", "plan"))
        {
            return ModelStrength.Planning;
        }

        if (ContainsAny(text, "写文章", "文案", "邮件", "润色", "作文", "诗", "小说", "公众号", "essay", "draft", "blog"))
        {
            return ModelStrength.Writing;
        }

        if (ContainsAny(text, "分析", "总结", "对比", "评估", "报告", "analyze", "summarize", "compare"))
        {
            return ModelStrength.Analysis;
        }

        return ModelStrength.General;
    }

    private static bool ContainsAny(string text, params string[] keywords) =>
        keywords.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase));

    private static MessageFailedEvent Fail(string sessionId, string error) => new()
    {
        SessionId = sessionId,
        MessageId = string.Empty,
        Error = error,
    };
}
