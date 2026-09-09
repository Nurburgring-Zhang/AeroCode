using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using AeroAgent.Conversation.Models;
using AeroAgent.Conversation.Orchestration;
using AeroAgent.Conversation.Services;
using AeroAgent.Moa.Accounting;
using AeroAgent.Moa.Aggregation;
using AeroAgent.Moa.Assignment;
using AeroAgent.Moa.Planning;
using AeroAgent.Moa.Profiles;
using AeroCode.Harness.Compaction;
using AeroCode.Harness.Graph;
using AiChatMessage = AeroCode.AI.Models.ChatMessage;
using PlanStep = AeroCode.Harness.Planner.PlanStep;

namespace AeroAgent.Moa.Strategies;

/// <summary>
/// 分工策略：planner 模型把目标拆成子任务 DAG（复用 Harness TaskGraph 拓扑并行执行），
/// ModelAssigner 按子任务强项分配 worker 模型，synthesizer 模型合成最终答复。
///
/// 诚实降级：continueOnError 语义下，失败子任务如实标注进入合成输入；
/// 全部子任务失败时本轮如实报失败；有子任务失败但最终合成成功时，
/// 最终消息状态标记为 Degraded。
/// </summary>
public sealed class DecomposeStrategy : IOrchestrationStrategy
{
    private readonly ISessionService _sessions;
    private readonly WorkerRunner _runner;
    private readonly ModelResolver _resolver;
    private readonly ModelAssigner _assigner;
    private readonly TaskPlanner _planner;
    private readonly Synthesizer _synthesizer;
    private readonly MoaOptions _options;

    /// <summary>
    /// B-SELECT 四层成本排序的生产接点（R2 修复 HIGH-1）：组合根在 settings costTiers.enabled=true 时注入。
    /// null（默认）= 走既有 <see cref="ModelAssigner.Assign"/> 打分路径，行为与注入前逐字节一致；
    /// 注入后 worker 选模改走 <see cref="ModelAssigner.Decide"/> 四层判定（①长上下文 → ②缓存折扣
    /// → ③batch → ④峰值档），未命中时回落既有打分路径。
    /// </summary>
    public CostTierSelectionOptions? CostTierSelection { get; set; }

    public DecomposeStrategy(
        ISessionService sessions,
        WorkerRunner runner,
        ModelResolver resolver,
        ModelAssigner assigner,
        TaskPlanner planner,
        Synthesizer synthesizer,
        MoaOptions options)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _assigner = assigner ?? throw new ArgumentNullException(nameof(assigner));
        _planner = planner ?? throw new ArgumentNullException(nameof(planner));
        _synthesizer = synthesizer ?? throw new ArgumentNullException(nameof(synthesizer));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public OrchestrationStrategy Kind => OrchestrationStrategy.Decompose;

    public async IAsyncEnumerable<ChatEvent> ExecuteAsync(OrchestrationContext context)
    {
        var ct = context.CancellationToken;
        var sessionId = context.Session.Id;
        var userText = context.History.LastOrDefault(m => m.Role == ChatRole.User)?.Content ?? string.Empty;
        var budget = new TurnBudget(_options.MaxUsdPerTurn);

        // ---- 1. planner 拆解（规划产物作为真实消息持久化）----
        var plannerAssignment = _resolver.Resolve(_options.Planner, ModelStrength.Planning);
        if (plannerAssignment is null)
        {
            yield return Fail(sessionId, "没有已配置的模型可用于任务规划");
            yield break;
        }

        var planChannel = Channel.CreateUnbounded<ChatEvent>();
        var planTask = Task.Run(async () =>
        {
            try
            {
                return await _planner.PlanAsync(
                    context, plannerAssignment, userText,
                    HistoryMapper.ToProviderMessages(context.History, context.SystemPrompt),
                    budget, planChannel.Writer, ct);
            }
            finally
            {
                planChannel.Writer.Complete();
            }
        });

        await foreach (var ev in EventPump.DrainAsync(planChannel, ct))
        {
            yield return ev;
        }

        var planResult = await planTask;
        if (planResult.Outcome.Cancelled)
        {
            yield break;
        }

        // planner 失败/输出不可解析时 ParsePlan 已兜底为单步计划——如实降级继续。
        var plan = planResult.Plan;
        var plannerMessageId = string.IsNullOrEmpty(planResult.PlannerMessageId)
            ? null
            : planResult.PlannerMessageId;

        // ---- planner 产物预校验：重复 Id/空 Id/依赖缺失如实报失败，
        //      不让 TaskGraph 抛裸 KeyNotFoundException，也不让重复 Id 静默覆盖。----
        var planError = ValidatePlan(plan.Steps);
        if (planError is not null)
        {
            yield return Fail(sessionId, planError);
            yield break;
        }

        // ---- 2. 子任务 DAG：每节点一个 worker 调用 ----
        // A5 双编排原语 + F-M2 收口（R2 缝合 #20，MoaOptions.OrchestrationPrimitive 已改可空）：
        // null（默认）= 未显式配置 → PrimitiveSelectionPolicy 形态自动判定
        // （扇出/可并行 → AgentsAsTool=基线；纯顺序链/上下文重 → Handoff）；
        // 显式配置（含显式 AgentsAsTool，可抑制自动判定）直接生效——过渡期
        // 「默认标记值视为未配置」的近似映射由可空字段替代，三种状态语义完备。
        var primitive = PrimitiveSelectionPolicy.Select(
            PrimitiveTaskShape.FromPlan(plan.Steps), _options.OrchestrationPrimitive);
        var handoffChain = primitive.Primitive == OrchestrationPrimitive.Handoff;
        var nodes = new Dictionary<string, TaskNode>(StringComparer.Ordinal);
        var channel = Channel.CreateUnbounded<ChatEvent>();

        string? handoffPreviousId = null;
        foreach (var step in plan.Steps)
        {
            var stepCapture = step;
            var strength = TaskPlanner.KindToStrength(stepCapture.Kind);
            var assignment = ResolveWorkerAssignment(strength, userText);

            // Handoff 串接：把 planner 顺序中的前一步并入依赖（链式移交）；AgentsAsTool 不变。
            var dependsOn = stepCapture.DependsOn;
            if (handoffChain && handoffPreviousId is not null)
            {
                dependsOn = dependsOn.Append(handoffPreviousId).Distinct(StringComparer.Ordinal).ToList();
            }

            handoffPreviousId = stepCapture.Id;

            var node = new TaskNode
            {
                Id = stepCapture.Id,
                Name = stepCapture.Title,
                Description = stepCapture.Description,
                DependsOn = dependsOn,
            };
            node.Execute = async nodeCt =>
            {
                // 上游未成功则不执行（诚实跳过，不拿缺失输入硬跑）。
                foreach (var depId in node.DependsOn)
                {
                    if (nodes.TryGetValue(depId, out var dep) && dep.State != TaskState.Succeeded)
                    {
                        throw new InvalidOperationException($"上游子任务 '{dep.Name}' 未成功");
                    }
                }

                if (assignment is null)
                {
                    throw new InvalidOperationException($"没有已配置的模型可处理强项 '{strength}'");
                }

                var outcome = await _runner.RunAsync(
                    context, assignment, StrategyRole.Worker,
                    plannerMessageId, stepCapture.Title,
                    BuildWorkerMessages(userText, stepCapture, dependsOn, nodes),
                    stream: false, isFinal: false, sink: channel.Writer, budget, nodeCt);

                if (outcome.Cancelled)
                {
                    throw new OperationCanceledException(nodeCt);
                }

                if (!outcome.Succeeded)
                {
                    throw new InvalidOperationException(outcome.Error ?? "子任务失败");
                }

                return outcome.Content;
            };
            nodes[node.Id] = node;
        }

        TaskGraph? graph = null;
        string? graphError = null;
        try
        {
            graph = new TaskGraph(nodes);
        }
        catch (InvalidOperationException ex)
        {
            // planner 给出了非法 DAG（环/缺失依赖）：记录下来，在 try 外如实报告。
            graphError = $"planner 产出的任务图非法：{ex.Message}";
        }

        if (graph is null)
        {
            yield return Fail(sessionId, graphError ?? "任务图构建失败");
            yield break;
        }

        var graphTask = Task.Run(async () =>
        {
            try
            {
                return await graph.ExecuteAsync(ct, continueOnError: true);
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

        var graphResult = await graphTask;
        if (ct.IsCancellationRequested)
        {
            yield break; // runner 已把进行中的消息落库为 Cancelled。
        }

        // ---- 3. 汇总子任务结果 ----
        var results = graphResult.Nodes
            .OrderBy(n => n.Id, StringComparer.Ordinal)
            .Select(n => new SubtaskResult(
                n.Name,
                n.State == TaskState.Succeeded,
                n.State == TaskState.Succeeded ? null : (n.Error ?? $"状态={n.State}"),
                n.Result ?? string.Empty))
            .ToList();

        if (!results.Any(r => r.Succeeded))
        {
            var reasons = string.Join("；", results
                .Where(r => !r.Succeeded)
                .Select(r => $"{r.Title}: {r.Error}"));
            yield return Fail(sessionId, $"所有子任务均失败：{reasons}");
            yield break;
        }

        // ---- 4. synthesizer 合成最终答复（流式）----
        var synthAssignment = _resolver.Resolve(_options.Synthesizer, ModelStrength.General);
        if (synthAssignment is null)
        {
            yield return Fail(sessionId, "没有已配置的模型可用于结果合成");
            yield break;
        }

        var synthChannel = Channel.CreateUnbounded<ChatEvent>();
        var synthTask = Task.Run(async () =>
        {
            try
            {
                return await _synthesizer.SynthesizeAsync(
                    context, synthAssignment, StrategyRole.Synthesizer,
                    plannerMessageId, "综合答复",
                    Synthesizer.BuildDecomposePrompt(userText, results),
                    isFinal: true, budget, synthChannel.Writer, ct);
            }
            finally
            {
                synthChannel.Writer.Complete();
            }
        });

        await foreach (var ev in EventPump.DrainAsync(synthChannel, ct))
        {
            yield return ev;
        }

        var synthOutcome = await synthTask;

        // ---- 5. 降级标注：有子任务失败但整体有产出 ----
        var anyFailed = results.Any(r => !r.Succeeded);
        if (anyFailed && synthOutcome.Succeeded && !string.IsNullOrEmpty(synthOutcome.MessageId))
        {
            await MarkDegradedAsync(sessionId, synthOutcome.MessageId);
        }
    }

    /// <summary>
    /// worker 选模真实接点（R2 修复 HIGH-1）：<see cref="CostTierSelection"/> 为 null（默认）
    /// 时走既有 <see cref="ModelAssigner.Assign"/> 打分路径（现行为，字节兼容）；
    /// 注入后从可观测信号构造 <see cref="CostRequestContext"/>，走 <see cref="ModelAssigner.Decide"/>
    /// 四层判定，判定无结果（无候选/四层全未命中由 Decide 内部回落打分）时回落既有 Assign。
    /// 信号口径（全部如实，宁缺勿假）：
    /// ①层输入 token 用 TokenCounter 估算总目标文本（未知时 null，①层不触发）；
    /// ②层 CacheFriendly 恒为 false——本编排路径不维护冻结缓存前缀（冻结前缀机制在
    /// WorkerRunner 工具循环内部，选模点无法保证该折扣真实兑现，按保守口径不申报）；
    /// ③层 BatchAllowed 恒为 false——请求管线无 batch 传输，按 batch 折扣选模而照常发请求会虚增成本口径；
    /// ④层 PeakRequested 来自 settings costTiers.peakTierEnabled（组合根注入，真实④层输入；
    /// 峰值档实际可用性仍由 B4 EffortProfile 探测裁决）。
    /// </summary>
    private ModelAssignment? ResolveWorkerAssignment(string strength, string userText)
    {
        var selection = CostTierSelection;
        if (selection is null)
        {
            return _assigner.Assign(strength); // 现行为
        }

        var decision = _assigner.Decide(
            new CostRequestContext
            {
                EstimatedInputTokens = userText.Length > 0 ? TokenCounter.ApproxTokens(userText) : null,
                CacheFriendly = false,
                BatchAllowed = false,
                PeakRequested = selection.PeakTierEnabled,
            },
            strength,
            options: selection.Options);
        return decision.Assignment ?? _assigner.Assign(strength);
    }

    /// <summary>校验 planner 计划的结构合法性；合法返回 null，否则返回可读错误。</summary>
    private static string? ValidatePlan(IReadOnlyList<PlanStep> steps)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var step in steps)
        {
            if (string.IsNullOrWhiteSpace(step.Id))
            {
                return "planner 产出的计划包含空步骤 Id";
            }

            if (!ids.Add(step.Id))
            {
                return $"planner 产出的计划包含重复步骤 Id '{step.Id}'";
            }
        }

        foreach (var step in steps)
        {
            foreach (var dep in step.DependsOn)
            {
                if (!ids.Contains(dep))
                {
                    return $"planner 产出的计划中步骤 '{step.Id}' 依赖了不存在的步骤 '{dep}'";
                }
            }
        }

        return null;
    }

    /// <summary>worker 输入 = 总目标 + 本子任务描述 + 已完成依赖的产出（依赖列表由调用方传入：AgentsAsTool 为计划原依赖，Handoff 为串接后的链上依赖）。</summary>
    private static IReadOnlyList<AiChatMessage> BuildWorkerMessages(
        string goal, PlanStep step, IReadOnlyList<string> dependsOn, Dictionary<string, TaskNode> nodes)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"总目标：{goal}");
        sb.AppendLine();
        sb.AppendLine($"你负责的子任务：{step.Title}");
        if (!string.IsNullOrWhiteSpace(step.Description))
        {
            sb.AppendLine($"任务说明：{step.Description}");
        }

        foreach (var depId in dependsOn)
        {
            if (nodes.TryGetValue(depId, out var dep) &&
                dep.State == TaskState.Succeeded &&
                !string.IsNullOrEmpty(dep.Result))
            {
                sb.AppendLine();
                sb.AppendLine($"前置子任务「{dep.Name}」的结果：");
                sb.AppendLine(dep.Result);
            }
        }

        sb.AppendLine();
        sb.AppendLine("只输出本子任务的结果正文，不要输出其他内容。");

        return new List<AiChatMessage> { new() { Role = "user", Content = sb.ToString() } };
    }

    private async Task MarkDegradedAsync(string sessionId, string messageId)
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

        target.Status = MessageStatus.Degraded;
        await _sessions.UpdateMessageAsync(target);
    }

    private static MessageFailedEvent Fail(string sessionId, string error) => new()
    {
        SessionId = sessionId,
        MessageId = string.Empty,
        Error = error,
    };
}

/// <summary>
/// B-SELECT 四层成本排序的生产注入载荷（R2 修复 HIGH-1）：组合根在 settings costTiers.enabled=true
/// 时构造并注入 <see cref="DecomposeStrategy.CostTierSelection"/>；默认不注入 = 既有 Assign 打分路径。
/// </summary>
public sealed record CostTierSelectionOptions
{
    /// <summary>四层判定的阈值与价格参数（settings costTiers 节点映射；null = ModelAssigner 内部默认）。</summary>
    public CostTierOptions? Options { get; init; }

    /// <summary>④峰值档开关（settings costTiers.peakTierEnabled；④层实际可用性仍由 EffortProfile 探测裁决）。</summary>
    public bool PeakTierEnabled { get; init; }
}
