// Copyright (c) AeroCode
// A1 TokenBudgetGate（R1-W1 builder-α，契约 C-GATE）：mission 级 token 上限闸门的数据模型。
// 语义对标既有 Accounting.TurnBudget（诚实中止、真实用量累计、不静默继续），维度为 token。
namespace AeroAgent.Moa.Budget;

/// <summary>mission 级 token 预算状态机：Running（未达水位）→ Warning（达警告水位）→ Exhausted（超限，终态）。</summary>
public enum BudgetState
{
    /// <summary>预算内（未达警告水位）。</summary>
    Running,

    /// <summary>已达警告水位（ spentTokens ≥ limitTokens × warningRatio），尚未超限。</summary>
    Warning,

    /// <summary>已超限（终态，不回退）。进入时一次性触发 <see cref="ITokenBudgetGate.BudgetExhausted"/>。</summary>
    Exhausted,
}

/// <summary>预算快照（诊断/外显只读视图）。</summary>
/// <param name="State">当前状态。</param>
/// <param name="SpentTokens">累计真实 token 消耗（provider usage 逐轮实报，不估算）。</param>
/// <param name="LimitTokens">mission 级 token 上限。</param>
/// <param name="DegradedToSingleAgent">「降级单 agent」标志：Exhausted 后置位，之后并行子代理派发被诚实拒绝。</param>
public sealed record BudgetSnapshot(
    BudgetState State,
    long SpentTokens,
    long LimitTokens,
    bool DegradedToSingleAgent);
