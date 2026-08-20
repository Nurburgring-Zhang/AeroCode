using System;
using AeroAgent.Conversation.Models;

namespace AeroAgent.Autonomy.Analysis;

/// <summary>策略选择结果：选定的编排策略 + 可解释理由。</summary>
public sealed record StrategyDecision(OrchestrationStrategy Strategy, string Rationale);

/// <summary>
/// 策略选择器：由 <see cref="TaskAnalysis"/> 驱动选择 MOA 编排策略（G1 差距项的核心——
/// 策略不再靠人工下拉选定，而是任务分析结果决定）。规则确定、可解释、可测试：
/// <list type="number">
/// <item>Composite（多领域并存）→ Decompose：需要 planner 拆解子任务 DAG 并行分工。</item>
/// <item>复杂度 ≥ 4 → Decompose：多步骤多约束任务受益于拆解与画像分配。</item>
/// <item>Creative → Ensemble：创作类任务多候选并行 + judge 裁决，产出更优。</item>
/// <item>Research → Router：先分类再路由到知识强项模型。</item>
/// <item>Analysis → Pipeline：起草→评审→修订接力，保证分析严谨性。</item>
/// <item>其余（简单 Code/Ops 等）→ Single：单模型直连 + 工具循环即可。</item>
/// </list>
/// </summary>
public sealed class StrategySelector
{
    /// <summary>复杂度达到该值时升级为 Decompose（多步骤多约束）。</summary>
    public const int DecomposeComplexityThreshold = 4;

    /// <summary>按规则选择策略。纯函数，无副作用。</summary>
    public StrategyDecision Select(TaskAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);

        if (analysis.Type == TaskType.Composite)
        {
            return new StrategyDecision(
                OrchestrationStrategy.Decompose,
                $"任务为多领域复合型（复杂度 {analysis.Complexity}），需要 planner 拆解子任务并行分工 → Decompose");
        }

        if (analysis.Complexity >= DecomposeComplexityThreshold)
        {
            return new StrategyDecision(
                OrchestrationStrategy.Decompose,
                $"复杂度 {analysis.Complexity} ≥ {DecomposeComplexityThreshold}（多步骤多约束），拆解执行更稳 → Decompose");
        }

        switch (analysis.Type)
        {
            case TaskType.Creative:
                return new StrategyDecision(
                    OrchestrationStrategy.Ensemble,
                    "创作类任务：多模型并行产出候选 + judge 择优合成 → Ensemble");

            case TaskType.Research:
                return new StrategyDecision(
                    OrchestrationStrategy.Router,
                    "研究/检索类任务：先分类再路由到知识强项模型 → Router");

            case TaskType.Analysis:
                return new StrategyDecision(
                    OrchestrationStrategy.Pipeline,
                    "分析类任务：起草→评审→修订接力保证严谨性 → Pipeline");

            default:
                return new StrategyDecision(
                    OrchestrationStrategy.Single,
                    $"常规 {analysis.Type} 任务（复杂度 {analysis.Complexity}），单模型直连 + 工具循环即可 → Single");
        }
    }
}
