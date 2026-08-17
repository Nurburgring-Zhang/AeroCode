using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AeroAgent.Conversation.Models;
using AeroAgent.Conversation.Orchestration;
using AeroAgent.Moa.Assignment;
using AeroAgent.Moa.Profiles;
using AeroAgent.Moa.Strategies;
using AeroCode.Harness.Planner;
using AiChatMessage = AeroCode.AI.Models.ChatMessage;
using Plan = AeroCode.Harness.Planner.Plan;

namespace AeroAgent.Moa.Planning;

/// <summary>planner 的拆解结果：Harness Plan + 持久化的 planner 消息 Id。</summary>
public sealed record PlannerResult(Plan Plan, string PlannerMessageId, WorkerOutcome Outcome);

/// <summary>
/// 任务规划器：让 planner 模型把用户目标拆成子任务 DAG（JSON），
/// 复用 Harness <see cref="Planner.ParsePlan"/> 的健壮解析（JSON/fence/编号列表三级兜底）。
/// planner 的原始输出作为一条真实消息持久化（StrategyRole.Planner），调度过程可审计。
/// </summary>
public sealed class TaskPlanner
{
    private readonly WorkerRunner _runner;

    public TaskPlanner(WorkerRunner runner)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
    }

    public async Task<PlannerResult> PlanAsync(
        OrchestrationContext ctx,
        ModelAssignment plannerAssignment,
        string userText,
        IReadOnlyList<AiChatMessage> history,
        Accounting.TurnBudget? budget,
        System.Threading.Channels.ChannelWriter<ChatEvent>? sink,
        CancellationToken ct)
    {
        // planner 提示词与 Harness Planner.FromLlm 对齐（2-7 步、唯一根、只规划不执行），
        // 追加"每步标注所需强项"以便 ModelAssigner 分工。
        var messages = new List<AiChatMessage>(history)
        {
            new()
            {
                Role = "system",
                Content =
                    "You are a senior task planner. Decompose the user's goal into 2-7 sub-tasks. Output JSON only: " +
                    "{\"goal\":\"<goal>\",\"steps\":[{\"id\":\"s1\",\"title\":\"...\",\"description\":\"...\",\"dependsOn\":[],\"kind\":\"code|writing|analysis|translation|math|review|general\"}, ...]}. " +
                    "Rules: ids unique; dependsOn may only reference earlier ids; keep it minimal; " +
                    "do NOT execute the goal yourself, only plan it.",
            },
        };

        var outcome = await _runner.RunAsync(
            ctx, plannerAssignment, StrategyRole.Planner,
            parentMessageId: null, label: "任务规划",
            messages, stream: false, isFinal: false, sink: sink, budget, ct);

        var plan = Planner.ParsePlan(userText, outcome.Content);

        // 规范化 step id（planner 可能输出非法依赖；ParsePlan 已兜底为单步）。
        return new PlannerResult(plan, outcome.MessageId, outcome);
    }

    /// <summary>把 Harness PlanStep.Kind 映射到画像强项词汇。</summary>
    public static string KindToStrength(string? kind) => ModelStrength.Normalize(kind) switch
    {
        "code" or "shell" or "read" => ModelStrength.Code,
        "write" => ModelStrength.Writing,
        "web" => ModelStrength.Analysis,
        var k => ModelStrength.All.Contains(k) ? k : ModelStrength.General,
    };
}
