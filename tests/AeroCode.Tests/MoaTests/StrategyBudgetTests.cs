// Copyright (c) AeroCode V3.0
// 编排策略预算测试：TurnBudget 单元语义 + Router/Ensemble/Pipeline 超预算时的真实行为。
// 所有断言对照真实实现：
//   - TurnBudget（src/AeroAgent.Moa/Accounting/CostTracker.cs:34-83）：
//     真实 API 为 MaxUsd / SpentUsd / HasBudget / AddActual（实现中不存在
//     IsExceeded / CanConsume / Consume，故按真实成员断言）。
//   - 预算守卫的唯一执行点在 WorkerRunner.RunAsync 发起调用之前
//     （src/AeroAgent.Moa/Strategies/WorkerRunner.cs:98-110）：超限即发
//     MessageFailedEvent("budget exceeded...") 并返回失败 outcome，不落占位消息。
using System;
using System.Linq;
using System.Threading.Tasks;
using AeroAgent.Conversation.Models;
using AeroAgent.Conversation.Orchestration;
using AeroAgent.Moa.Accounting;
using AeroAgent.Moa.Aggregation;
using AeroAgent.Moa.Profiles;
using AeroAgent.Moa.Strategies;
using AeroCode.AI.Models;
using Xunit;

namespace AeroCode.Tests.MoaTests;

/// <summary>
/// TurnBudget 单元语义：记账累计、超限判定、边界与参数校验。
/// 契约（CostTracker.cs:29-33 注释）：已花费超过上限即 HasBudget == false；
/// null 上限 = 不限制；预算基于真实用量，负数花费不被接受。
/// </summary>
public sealed class TurnBudgetTests
{
    /// <summary>null 上限 = 不限制：任意花费后 HasBudget 恒为 true，但花费如实累计。</summary>
    [Fact]
    public void NullLimit_Unlimited_SpendingStillTracked()
    {
        var budget = new TurnBudget(null);

        Assert.Null(budget.MaxUsd);
        Assert.True(budget.HasBudget);

        Assert.True(budget.AddActual(1_000_000.0)); // 无上限 → 永远"仍在预算内"
        Assert.True(budget.HasBudget);
        Assert.Equal(1_000_000.0, budget.SpentUsd, 6); // 花费照样如实记账
    }

    /// <summary>上限必须为正数：0 与负数构造即抛 ArgumentOutOfRangeException。</summary>
    [Fact]
    public void Ctor_ZeroOrNegativeLimit_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TurnBudget(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TurnBudget(-0.01));
    }

    /// <summary>负数花费不被接受：AddActual(-x) 抛 ArgumentOutOfRangeException。</summary>
    [Fact]
    public void AddActual_NegativeAmount_Throws()
    {
        var budget = new TurnBudget(1.0);
        Assert.Throws<ArgumentOutOfRangeException>(() => budget.AddActual(-0.01));
    }

    /// <summary>
    /// 记账逐笔累计；返回值与 HasBudget 的判定是"严格小于上限"——
    /// 累计恰好等于上限即视为超限（下一次调用将被守卫拦下）。
    /// </summary>
    [Fact]
    public void AddActual_Accumulates_ExactlyReachingLimitCountsAsExceeded()
    {
        var budget = new TurnBudget(1.0);
        Assert.Equal(0.0, budget.SpentUsd, 6);
        Assert.True(budget.HasBudget);

        Assert.True(budget.AddActual(0.4));  // 0.4 < 1.0 → 仍在预算内
        Assert.True(budget.AddActual(0.4));  // 0.8 < 1.0 → 仍在预算内
        Assert.Equal(0.8, budget.SpentUsd, 6);
        Assert.True(budget.HasBudget);

        Assert.False(budget.AddActual(0.2)); // 1.0 恰好触顶：实现用 _spent < max 判定 → false
        Assert.Equal(1.0, budget.SpentUsd, 6);
        Assert.False(budget.HasBudget);      // 后续调用会被 WorkerRunner 的守卫拦下
    }

    /// <summary>单笔花费即超上限：AddActual 返回 false，HasBudget 翻转为 false。</summary>
    [Fact]
    public void SingleOverspend_FlipsHasBudget()
    {
        var budget = new TurnBudget(0.05);

        Assert.False(budget.AddActual(0.02 + 0.04)); // 0.06 > 0.05
        Assert.False(budget.HasBudget);
        Assert.Equal(0.06, budget.SpentUsd, 6); // 超支金额如实记录，不回滚
    }
}

/// <summary>
/// Router / Ensemble / Pipeline 策略的超预算行为（对照真实实现逐一确认）：
/// 三个策略都在 ExecuteAsync 开头创建 TurnBudget（RouterStrategy.cs:40、
/// PipelineStrategy.cs:40、EnsembleStrategy.cs:58），并在每次 WorkerRunner.RunAsync
/// 发起前接受守卫检查——超限时拒绝执行该次调用、产出 MessageFailedEvent
/// （Error 含 "budget exceeded"），且不落占位消息、不调用 provider。
/// 成本构造方式：画像填写单价（CostPerMIn/Out）+ provider 实报 usage，
/// 使一次非流式调用的真实成本（CostTracker.Estimate）超过极小预算。
/// </summary>
public sealed class StrategyBudgetTests : MoaTestBase
{
    /// <summary>1000 in + 1000 out @ $10/M 各 = $0.02 的实报用量。</summary>
    private static readonly UsageInfo ExpensiveUsage = new()
    {
        PromptTokens = 1000,
        CompletionTokens = 1000,
        TotalTokens = 2000,
    };

    private RouterStrategy MakeRouterStrategy() => new(Runner, Resolver, Options);

    private EnsembleStrategy MakeEnsembleStrategy() =>
        new(Sessions, Runner, Resolver, Assigner, new Synthesizer(Runner), Options);

    private PipelineStrategy MakePipelineStrategy() => new(Runner, Resolver, Options);

    /// <summary>
    /// Router：路由分类调用真实发生并如实记账（$0.02），随后 worker 最终答复
    /// 在发起前被预算守卫拦下——产出 "budget exceeded" 失败事件、不落 worker 消息、
    /// worker provider 从未被调用；整轮以 TurnCompletedEvent 收尾（诚实中止，不静默继续）。
    /// </summary>
    [Fact]
    public async Task Router_BudgetExhaustedByClassification_WorkerRefusedHonestly()
    {
        var fast = AddProvider("fast");
        SetProfile("fast", new[] { ModelStrength.General },
            costPerMIn: 10, costPerMOut: 10, speed: SpeedTier.Fast);
        fast.ChatQueue.Enqueue("{\"category\":\"code\",\"reason\":\"代码请求\"}");
        fast.NonStreamUsage = ExpensiveUsage; // 分类一次即花费 $0.02

        var coder = AddProvider("coder");
        SetProfile("coder", new[] { ModelStrength.Code });
        coder.Deltas = new[] { "不该", "被执行" };

        Options.MaxUsdPerTurn = 1e-9; // 任何已计价调用都必然超限

        var facade = MakeFacade(MakeRouterStrategy());
        var session = await NewSessionAsync(OrchestrationStrategy.Router);

        var events = await CollectAsync(facade.SendAsync(session.Id, "写一个排序函数"));

        // worker 调用被预算守卫拒绝：可读失败事件（WorkerRunner.cs:98-110 产出）
        var failure = Assert.Single(events.OfType<MessageFailedEvent>());
        Assert.Equal(string.Empty, failure.MessageId); // 守卫在落占位消息之前拦截
        Assert.Contains("budget exceeded", failure.Error);

        var messages = (await Sessions.GetMessagesAsync(session.Id)).Value!;

        // 路由分类照常完成且成本如实落库：1000/1M*10 * 2 = $0.02
        var routerMsg = messages.Single(m => m.OrchestrationRole == StrategyRole.Router);
        Assert.Equal(MessageStatus.Completed, routerMsg.Status);
        Assert.Equal(0.02, routerMsg.CostUsd, 6);

        // worker 消息未被创建（预算拦截在持久化之前），provider 也从未被调用
        Assert.DoesNotContain(messages, m => m.OrchestrationRole == StrategyRole.Worker);
        Assert.Null(coder.LastRequestMessages);

        Assert.IsType<TurnCompletedEvent>(events[^1]);
    }

    /// <summary>
    /// Ensemble：N 个并行候选的真实成本在共享 TurnBudget 上累计
    /// （2 × $0.02 = $0.04 > $0.03 上限）；候选完成后 judge 裁决调用在发起前
    /// 被守卫拦下——"budget exceeded" 失败事件、无 judge 消息、judge provider 未被调用。
    /// 同时如实固化当前守卫粒度：守卫只在"每次调用发起前"检查
    /// （WorkerRunner.cs:99），并行候选在同一时刻全部通过检查，
    /// 因此本轮累计花费可以超过上限（$0.04 > $0.03）——拦截只对后续调用生效。
    /// </summary>
    [Fact]
    public async Task Ensemble_CumulativeWorkerCost_BlocksJudgeCall()
    {
        var alpha = AddProvider("alpha");
        SetProfile("alpha", new[] { ModelStrength.General }, costPerMIn: 10, costPerMOut: 10);
        alpha.NonStreamContent = "ANSWER-A";
        alpha.NonStreamUsage = ExpensiveUsage; // 每个候选 $0.02

        var beta = AddProvider("beta");
        SetProfile("beta", new[] { ModelStrength.General }, costPerMIn: 10, costPerMOut: 10);
        beta.NonStreamContent = "ANSWER-B";
        beta.NonStreamUsage = ExpensiveUsage;

        var judge = AddProvider("judge");
        SetProfile("judge", new[] { ModelStrength.Review });
        judge.Deltas = new[] { "不该", "裁决" };

        Options.MaxUsdPerTurn = 0.03; // 容纳单个候选（0.02），必被两个候选的累计（0.04）击穿

        var facade = MakeFacade(MakeEnsembleStrategy());
        var session = await NewSessionAsync(OrchestrationStrategy.Ensemble);

        var events = await CollectAsync(facade.SendAsync(session.Id, "哪一个是正确的？"));

        var messages = (await Sessions.GetMessagesAsync(session.Id)).Value!;

        // 两个候选都在发起时通过检查（当时 spent=0），各自完成并如实记账
        var candidates = messages.Where(m => m.OrchestrationRole == StrategyRole.Worker).ToList();
        Assert.Equal(2, candidates.Count);
        Assert.All(candidates, m =>
        {
            Assert.Equal(MessageStatus.Completed, m.Status);
            Assert.Equal(0.02, m.CostUsd, 6);
        });
        var totalSpent = candidates.Sum(m => m.CostUsd);
        Assert.True(totalSpent > Options.MaxUsdPerTurn,
            "并行候选的累计成本应真实超过单轮上限——守卫粒度为逐次调用发起前");

        // judge 裁决被预算守卫拦下：失败事件 + 不落消息 + provider 未被调用
        var failure = Assert.Single(events.OfType<MessageFailedEvent>());
        Assert.Equal(string.Empty, failure.MessageId);
        Assert.Contains("budget exceeded", failure.Error);
        Assert.DoesNotContain(messages, m => m.OrchestrationRole == StrategyRole.Judge);
        Assert.Null(judge.LastRequestMessages);

        Assert.IsType<TurnCompletedEvent>(events[^1]);
    }

    /// <summary>
    /// Pipeline：起草阶段真实发生并如实记账（$0.02）后，评审阶段在发起前
    /// 被预算守卫拦下——"budget exceeded" 失败事件、评审/修订消息均未创建、
    /// 评审 provider 从未被调用；草稿如实保留（策略对失败的阶段诚实停止）。
    /// </summary>
    [Fact]
    public async Task Pipeline_BudgetExhaustedByDraft_ReviewRefusedHonestly()
    {
        var writer = AddProvider("writer");
        SetProfile("writer", new[] { ModelStrength.Writing }, costPerMIn: 10, costPerMOut: 10);
        writer.NonStreamContent = "DRAFT-V1";
        writer.NonStreamUsage = ExpensiveUsage; // 起草一次即花费 $0.02
        writer.Deltas = new[] { "不该", "修订" };

        var reviewer = AddProvider("reviewer");
        SetProfile("reviewer", new[] { ModelStrength.Review });
        reviewer.NonStreamContent = "不该被评审";

        Options.MaxUsdPerTurn = 1e-9; // 任何已计价调用都必然超限

        var facade = MakeFacade(MakePipelineStrategy());
        var session = await NewSessionAsync(OrchestrationStrategy.Pipeline);

        var events = await CollectAsync(facade.SendAsync(session.Id, "写一篇公众号文章"));

        // 评审调用被预算守卫拒绝
        var failure = Assert.Single(events.OfType<MessageFailedEvent>());
        Assert.Equal(string.Empty, failure.MessageId);
        Assert.Contains("budget exceeded", failure.Error);

        var messages = (await Sessions.GetMessagesAsync(session.Id)).Value!;

        // 草稿照常完成且成本如实落库：$0.02
        var draft = messages.Single(m => m.Label == "起草");
        Assert.Equal(MessageStatus.Completed, draft.Status);
        Assert.Equal(0.02, draft.CostUsd, 6);

        // 后续阶段未启动：评审/修订消息不存在，评审 provider 从未被调用
        Assert.DoesNotContain(messages, m => m.Label == "评审" || m.Label == "修订终稿");
        Assert.Null(reviewer.LastRequestMessages);

        Assert.IsType<TurnCompletedEvent>(events[^1]);
    }
}
