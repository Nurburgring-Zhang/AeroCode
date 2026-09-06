// Copyright (c) AeroCode
// A1 TokenBudgetGate（契约 C-GATE）：mission 级 token 上限闸门接口。
// 消费方：SubAgentRunner（派发并行子代理前过闸门；Exhausted 事件触发在飞取消 + 降级单 agent）。
namespace AeroAgent.Moa.Budget;

/// <summary>
/// mission 级 token 预算闸门。实现约束：
/// 1. 只累计真实 token 消耗（provider usage 实报），不估算、不预扣；
/// 2. 状态机 <see cref="BudgetState"/> 单向推进（Running → Warning → Exhausted），不回退；
/// 3. 进入 Exhausted 时恰好在转换沿触发一次 <see cref="BudgetExhausted"/>（状态停留期不重复触发）；
/// 4. 线程安全（多并行子代理逐轮并发实报）。
/// </summary>
public interface ITokenBudgetGate
{
    /// <summary>当前预算状态。</summary>
    BudgetState State { get; }

    /// <summary>mission 级 token 上限（构造后不可变）。</summary>
    long LimitTokens { get; }

    /// <summary>累计真实 token 消耗。</summary>
    long SpentTokens { get; }

    /// <summary>「降级单 agent」标志：Exhausted 后为 true；此后并行子代理派发被诚实拒绝。</summary>
    bool DegradedToSingleAgent { get; }

    /// <summary>
    /// 记录一次真实 token 消耗（如某轮 usage 的 promptTokens + completionTokens）。
    /// 返回累计后的状态；跨越上限时置降级标志并在转换沿触发 <see cref="BudgetExhausted"/>。
    /// </summary>
    BudgetState ReportUsage(long tokens);

    /// <summary>当前预算快照（状态 + 已耗/上限 + 降级标志）。</summary>
    BudgetSnapshot Snapshot();

    /// <summary>进入 Exhausted 的转换沿事件（一次性）。参数为触发时的快照。</summary>
    event Action<BudgetSnapshot>? BudgetExhausted;
}
