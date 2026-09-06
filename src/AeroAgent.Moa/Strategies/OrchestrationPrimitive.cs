// Copyright (c) AeroCode
// OrchestrationPrimitive — A5 双编排原语显式化：AgentsAsTool（子任务作为工具并行分工，
// = 既有 Decompose 行为基线）与 Handoff（顺序控制权转移）。默认 AgentsAsTool 保基线；
// PrimitiveSelectionPolicy 提供形态判定（显式配置优先），消费入口在 DecomposeStrategy。
using PlanStep = AeroCode.Harness.Planner.PlanStep;

namespace AeroAgent.Moa.Strategies;

/// <summary>
/// 编排原语。<see cref="AgentsAsTool"/> = 现行为（子任务作为工具，DAG 拓扑并行分工）；
/// <see cref="Handoff"/> = 顺序控制权转移（子任务按 planner 顺序串接成链，前一步完成才移交）。
/// </summary>
public enum OrchestrationPrimitive
{
    /// <summary>扇出/可并行形态：子任务作为工具并行分工（默认 = 现行为，保基线）。</summary>
    AgentsAsTool = 0,

    /// <summary>顺序控制权转移/上下文重形态：链式逐级移交。</summary>
    Handoff = 1,
}

/// <summary>任务形态输入（policy 判定所需最小事实）：扇出宽度 / 最长链深 / 上下文重标记。</summary>
/// <param name="FanOutWidth">最大可并行宽度（最长路径深度分层的最宽层数；同层节点必互相独立）。</param>
/// <param name="ChainDepth">最长依赖链长度（节点数计）。</param>
/// <param name="ContextHeavy">调用方显式声明的上下文重标记（单任务携带大上下文/长历史）。</param>
public sealed record PrimitiveTaskShape(int FanOutWidth, int ChainDepth, bool ContextHeavy = false)
{
    /// <summary>空计划形态（宽 0 深 0）。</summary>
    public static PrimitiveTaskShape Empty { get; } = new(0, 0);

    /// <summary>
    /// 从 planner 计划推导形态。层 = 最长路径深度分组：同一深度的两个节点必互不依赖
    /// （依赖会强制深度严格递增），故最宽层即最大可并行宽度的下界。
    /// 环在这里防御性截断（不递归到底）——环图由 TaskGraph 构造在随后如实报错。
    /// </summary>
    public static PrimitiveTaskShape FromPlan(IReadOnlyList<PlanStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        if (steps.Count == 0)
        {
            return Empty;
        }

        var stepById = new Dictionary<string, PlanStep>(StringComparer.Ordinal);
        foreach (var step in steps)
        {
            if (!string.IsNullOrEmpty(step.Id))
            {
                stepById.TryAdd(step.Id, step);
            }
        }

        var depth = new Dictionary<string, int>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        int DepthOf(string id)
        {
            if (depth.TryGetValue(id, out var known))
            {
                return known;
            }

            if (!visiting.Add(id))
            {
                return 0; // 环防御：不递归到底，交由 TaskGraph 构造报错。
            }

            var best = 1;
            if (stepById.TryGetValue(id, out var step))
            {
                foreach (var dep in step.DependsOn)
                {
                    if (stepById.ContainsKey(dep))
                    {
                        best = Math.Max(best, DepthOf(dep) + 1);
                    }
                }
            }

            visiting.Remove(id);
            depth[id] = best;
            return best;
        }

        var maxDepth = 0;
        var levelCounts = new Dictionary<int, int>();
        foreach (var id in stepById.Keys)
        {
            var level = DepthOf(id);
            levelCounts[level] = levelCounts.TryGetValue(level, out var count) ? count + 1 : 1;
            if (level > maxDepth)
            {
                maxDepth = level;
            }
        }

        return new PrimitiveTaskShape(levelCounts.Values.Max(), maxDepth);
    }
}

/// <summary>原语判定结果：选中的原语 + 可读依据（留痕/诊断用，不含任何凭据）。</summary>
public sealed record PrimitiveDecision(OrchestrationPrimitive Primitive, string Rationale);

/// <summary>
/// 原语选择策略：扇出/可并行任务形态 → AgentsAsTool；顺序控制权转移/上下文重 → Handoff。
/// 显式配置（用户意愿）优先于形态判定；未显式配置时按形态判定。
/// 纯函数，无状态无副作用。
/// </summary>
public static class PrimitiveSelectionPolicy
{
    /// <summary>
    /// 判定编排原语。<paramref name="configured"/> 非 null = 显式配置，直接生效；
    /// null = 未显式配置，按形态判定：上下文重 → Handoff；纯顺序链（深 &gt; 1 且无并行机会）→
    /// Handoff；其余（扇出/可并行/平凡单步）→ AgentsAsTool。
    /// </summary>
    public static PrimitiveDecision Select(PrimitiveTaskShape shape, OrchestrationPrimitive? configured = null)
    {
        ArgumentNullException.ThrowIfNull(shape);
        if (configured is { } choice)
        {
            return new PrimitiveDecision(choice, $"显式配置 {choice}，覆盖形态判定");
        }

        if (shape.ContextHeavy)
        {
            return new PrimitiveDecision(
                OrchestrationPrimitive.Handoff, "上下文重：顺序移交，避免重上下文在并行分支重复回灌");
        }

        if (shape.ChainDepth > 1 && shape.FanOutWidth <= 1)
        {
            return new PrimitiveDecision(
                OrchestrationPrimitive.Handoff, "顺序控制权转移：纯依赖链无并行机会，逐级移交");
        }

        return new PrimitiveDecision(
            OrchestrationPrimitive.AgentsAsTool, "扇出/可并行：存在可并行子任务，分工并行收益占优");
    }
}
