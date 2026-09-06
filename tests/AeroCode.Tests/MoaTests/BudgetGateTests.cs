using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AeroAgent.Moa.Budget;
using AeroAgent.Moa.Profiles;
using AeroAgent.Moa.Subagent;
using AeroAgent.Moa.Tools;
using AeroAgent.Conversation.Models;
using AeroCode.AI.Models;
using AeroCode.Harness.EventBus;
using Xunit;
using ConvChatMessage = AeroAgent.Conversation.Models.ChatMessage;

namespace AeroCode.Tests.MoaTests;

/// <summary>
/// A1 TokenBudgetGate（契约 C-GATE）状态机行为：Running → Warning → Exhausted 阈值边界、
/// Exhausted 转换沿一次性事件、降级标志粘滞、构造与实报参数校验、并发实报线程安全。
/// </summary>
public sealed class BudgetGateTests
{
    [Fact]
    public void Constructor_NonPositiveLimitOrBadRatio_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TokenBudgetGate(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TokenBudgetGate(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TokenBudgetGate(100, warningRatio: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TokenBudgetGate(100, warningRatio: -0.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TokenBudgetGate(100, warningRatio: 1.1));
    }

    [Fact]
    public void InitialState_IsRunning_ZeroSpend()
    {
        var gate = new TokenBudgetGate(100, warningRatio: 0.5);
        Assert.Equal(BudgetState.Running, gate.State);
        Assert.Equal(0, gate.SpentTokens);
        Assert.Equal(100, gate.LimitTokens);
        Assert.False(gate.DegradedToSingleAgent);

        var snapshot = gate.Snapshot();
        Assert.Equal(BudgetState.Running, snapshot.State);
        Assert.Equal(0, snapshot.SpentTokens);
        Assert.Equal(100, snapshot.LimitTokens);
        Assert.False(snapshot.DegradedToSingleAgent);
    }

    [Fact]
    public void ReportUsage_TransitionsAtExactThresholdBoundaries()
    {
        // limit=100、ratio=0.5（二进制精确表示，边界无浮点误差）：Warning ≥ 50，Exhausted ≥ 100。
        var gate = new TokenBudgetGate(100, warningRatio: 0.5);

        Assert.Equal(BudgetState.Running, gate.ReportUsage(49));
        Assert.Equal(BudgetState.Running, gate.State);

        Assert.Equal(BudgetState.Warning, gate.ReportUsage(1)); // 恰好 50 = 100×0.5 → Warning 下边界
        Assert.Equal(BudgetState.Warning, gate.ReportUsage(49)); // 99 仍在警告带内

        // 恰好 100 同时满足警告水位与上限：Exhausted 优先判定（终态）。
        Assert.Equal(BudgetState.Exhausted, gate.ReportUsage(1));
        Assert.Equal(BudgetState.Exhausted, gate.State);

        // 终态停留：继续实报只累计、不改变状态。
        Assert.Equal(BudgetState.Exhausted, gate.ReportUsage(50));
        Assert.Equal(150, gate.SpentTokens);
        Assert.Equal(150, gate.Snapshot().SpentTokens);
    }

    [Fact]
    public void DefaultWarningRatio_IsEightyPercent()
    {
        var gate = new TokenBudgetGate(1000); // 默认 0.8 → 警告水位 800
        Assert.Equal(BudgetState.Running, gate.ReportUsage(790));
        Assert.Equal(BudgetState.Warning, gate.ReportUsage(20)); // 810 进入警告带
        Assert.Equal(BudgetState.Exhausted, gate.ReportUsage(200)); // 1010 超限
    }

    [Fact]
    public void NegativeUsage_Throws()
    {
        var gate = new TokenBudgetGate(100);
        Assert.Throws<ArgumentOutOfRangeException>(() => gate.ReportUsage(-1));
    }

    [Fact]
    public void ExhaustedEvent_FiresExactlyOnce_AtTransitionEdge()
    {
        var gate = new TokenBudgetGate(100, warningRatio: 0.5);
        var fired = new List<BudgetSnapshot>();
        gate.BudgetExhausted += fired.Add;

        gate.ReportUsage(70); // Warning：未超限，无事件
        Assert.Empty(fired);

        gate.ReportUsage(30); // 100 → 跨越上限的转换沿
        var snapshot = Assert.Single(fired);
        Assert.Equal(BudgetState.Exhausted, snapshot.State);
        Assert.Equal(100, snapshot.SpentTokens);
        Assert.Equal(100, snapshot.LimitTokens);
        Assert.True(snapshot.DegradedToSingleAgent);

        gate.ReportUsage(50); // 状态停留期：不重复触发
        Assert.Single(fired);
    }

    [Fact]
    public void DegradedFlag_SetOnlyOnExhaustion_AndSticky()
    {
        var gate = new TokenBudgetGate(100, warningRatio: 0.5);
        gate.ReportUsage(50); // Warning：未降级
        Assert.False(gate.DegradedToSingleAgent);
        Assert.False(gate.Snapshot().DegradedToSingleAgent);

        gate.ReportUsage(50); // Exhausted：降级置位
        Assert.True(gate.DegradedToSingleAgent);
        Assert.True(gate.Snapshot().DegradedToSingleAgent);
    }

    [Fact]
    public void ConcurrentReporting_ThreadSafe_ExactAggregate()
    {
        var gate = new TokenBudgetGate(1_000_000);
        const int reporters = 50;
        const int tokensPerReport = 10;

        Parallel.For(0, reporters, _ => gate.ReportUsage(tokensPerReport));

        Assert.Equal(reporters * tokensPerReport, gate.SpentTokens);
        Assert.Equal(BudgetState.Running, gate.State);
        Assert.False(gate.DegradedToSingleAgent);
    }
}

/// <summary>
/// SubagentOptions 并行开关（A1，C-GATE 新增成员）：默认保持现行为（并行可用）；
/// 关闭时生效并行上限钳制为 1，配置原样保留。
/// </summary>
public sealed class SubagentOptionsTests
{
    [Fact]
    public void Defaults_ParallelEnabled_KeepsCurrentBehavior()
    {
        var options = new SubagentOptions();
        Assert.True(options.ParallelEnabled);
        Assert.Equal(2, options.MaxParallel);
        Assert.Equal(2, options.EffectiveMaxParallel);
        Assert.Equal(4, options.EffectiveMaxDepth);
    }

    [Fact]
    public void ParallelDisabled_EffectiveParallelClampedToOne_ConfigPreserved()
    {
        var options = new SubagentOptions { MaxParallel = 3, ParallelEnabled = false };
        Assert.False(options.ParallelEnabled);
        Assert.Equal(3, options.MaxParallel); // 配置原样保留（诚实诊断面）
        Assert.Equal(1, options.EffectiveMaxParallel); // 生效并行上限钳制为 1
    }

    [Fact]
    public void EffectiveMaxDepth_ClampedToHardLimit()
    {
        Assert.Equal(SubAgentSpec.MaxDepth, new SubagentOptions { MaxDepth = 10 }.EffectiveMaxDepth);
        Assert.Equal(1, new SubagentOptions { MaxDepth = 0 }.EffectiveMaxDepth);
    }
}

/// <summary>
/// C-GATE 与 SubAgentRunner 的接线行为（真实 SQLite 会话库 + 可编程 provider）：
/// 派发前置闸门、Exhausted 取消在飞并行子代理（无孤儿任务/无半写状态）、
/// 逐轮真实 usage 实报、ParallelEnabled=false 信号量钳制。
/// </summary>
public sealed class BudgetGateSubagentTests : MoaTestBase
{
    private static SubAgentSpec Spec(string providerId, string prompt = "子任务：读取笔记并汇总") => new(
        Description: "probe-subagent",
        Prompt: prompt,
        ProviderId: providerId,
        Model: string.Empty,
        Depth: 1,
        MaxTurns: 8,
        MaxCostUsd: 0,
        ParallelSafe: true);

    private static ChatResponse FinalResponse(string content, UsageInfo? usage) => new()
    {
        Id = "resp-final",
        Model = string.Empty,
        Content = content,
        FinishReason = "stop",
        Usage = usage,
    };

    private SubAgentRunner NewRunner(ITokenBudgetGate? gate = null, SubagentOptions? options = null)
        => new(Sessions, Registry, Catalog, new EventBus(), options, tools: null, logger: null, budgetGate: gate);

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.True(condition(), "condition not met within timeout");
    }

    /// <summary>汇总全部子代理会话（标题以 [subagent] 开头）中的持久化消息。</summary>
    private async Task<List<ConvChatMessage>> SubagentSessionMessagesAsync()
    {
        var listed = await Sessions.ListSessionsAsync();
        Assert.True(listed.IsSuccess);
        var messages = new List<ConvChatMessage>();
        foreach (var session in listed.Value!.Where(s => s.Title.StartsWith("[subagent]", StringComparison.Ordinal)))
        {
            var got = await Sessions.GetMessagesAsync(session.Id);
            Assert.True(got.IsSuccess);
            messages.AddRange(got.Value!);
        }

        return messages;
    }

    [Fact]
    public async Task ExhaustedGate_CancelsInFlightSubagents_NoOrphansNoHalfWrittenState()
    {
        var gate = new TokenBudgetGate(10_000);
        var exhausted = new List<BudgetSnapshot>();
        gate.BudgetExhausted += exhausted.Add;

        var g1 = new GatedProvider { ProviderId = "bg1", Content = "p1" };
        var g2 = new GatedProvider { ProviderId = "bg2", Content = "p2" };
        Registry.Add(g1);
        Registry.Add(g2);

        var runner = NewRunner(gate: gate, options: new SubagentOptions { MaxParallel = 2 });
        var h1 = await runner.LaunchAsync(Spec("bg1"), CancellationToken.None);
        var h2 = await runner.LaunchAsync(Spec("bg2"), CancellationToken.None);
        await WaitUntilAsync(() => runner.ActiveCount == 2, TimeSpan.FromSeconds(5));

        // 两个并行子代理悬挂在 provider 调用中（在飞）；此时未超限、无事件。
        Assert.Empty(exhausted);

        // mission 预算熔断：转换沿事件 + 降级标志，两个在飞子代理被取消。
        Assert.Equal(BudgetState.Exhausted, gate.ReportUsage(gate.LimitTokens));
        var snapshot = Assert.Single(exhausted);
        Assert.True(snapshot.DegradedToSingleAgent);
        Assert.True(gate.DegradedToSingleAgent);
        Assert.True(runner.DegradedToSingleAgent);

        // 无孤儿任务：两个 handle 都诚实收场（WaitAsync 必然回填）。
        Assert.Equal("cancelled by user", await h1.WaitAsync(CancellationToken.None));
        Assert.Equal("cancelled by user", await h2.WaitAsync(CancellationToken.None));
        await WaitUntilAsync(() => runner.ActiveCount == 0, TimeSpan.FromSeconds(5));

        // 无半写状态：子代理会话内不留 Pending/Streaming 僵尸消息（DB 是事实源）。
        var persisted = await SubagentSessionMessagesAsync();
        Assert.NotEmpty(persisted);
        Assert.All(persisted, m => Assert.NotEqual(MessageStatus.Pending, m.Status));
        Assert.All(persisted, m => Assert.NotEqual(MessageStatus.Streaming, m.Status));

        await h1.DisposeAsync();
        await h2.DisposeAsync();
        g1.Gate.TrySetResult(); // 收尾：释放悬挂任务
        g2.Gate.TrySetResult();
    }

    [Fact]
    public async Task ExhaustedGate_RefusesNewDispatchBeforeLaunch_HonestSingleAgentDegradation()
    {
        // 派发前置闸门：超预算后不再派发新并行子代理（诚实拒绝，不静默修正）。
        var gate = new TokenBudgetGate(100);
        Assert.Equal(BudgetState.Exhausted, gate.ReportUsage(100));

        var runner = NewRunner(gate: gate);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.LaunchAsync(Spec("bg-refused"), CancellationToken.None));
        Assert.Contains("token budget exhausted", ex.Message);
        Assert.Contains("single-agent", ex.Message);
        Assert.Equal(0, runner.ActiveCount); // 派发未发生：无在飞子代理
    }

    [Fact]
    public async Task WarningState_DoesNotBlockDispatch()
    {
        // 闸门只在 Exhausted 拒绝派发；Warning 带内派发照常可用。
        var gate = new TokenBudgetGate(1000);
        Assert.Equal(BudgetState.Warning, gate.ReportUsage(800));

        var provider = AddProvider("bg-warn");
        provider.ResponseQueue.Enqueue(FinalResponse("done", null));
        var runner = NewRunner(gate: gate);

        var handle = await runner.LaunchAsync(Spec("bg-warn"), CancellationToken.None);
        Assert.Equal("done", await handle.WaitAsync(CancellationToken.None));
        await handle.DisposeAsync();
        Assert.False(gate.DegradedToSingleAgent);
    }

    [Fact]
    public async Task SubagentTurns_ReportRealUsageToGate()
    {
        var gate = new TokenBudgetGate(1_000_000);
        var provider = AddProvider("bg-usage");
        provider.ResponseQueue.Enqueue(FinalResponse(
            "汇总", new UsageInfo { PromptTokens = 100, CompletionTokens = 10 }));

        var runner = NewRunner(gate: gate);
        var handle = await runner.LaunchAsync(Spec("bg-usage"), CancellationToken.None);
        Assert.Equal("汇总", await handle.WaitAsync(CancellationToken.None));
        await handle.DisposeAsync();

        // 逐轮真实 usage 实报（provider usage 实报，不估算）：100 + 10。
        Assert.Equal(110, gate.SpentTokens);
        Assert.Equal(BudgetState.Running, gate.State);
    }

    [Fact]
    public async Task RealUsageCrossingLimit_CancelsSiblingInFlight_SelfCompletes()
    {
        var gate = new TokenBudgetGate(100_000);
        var hung = new GatedProvider { ProviderId = "bg-hang", Content = "hung" };
        Registry.Add(hung);
        var reporter = AddProvider("bg-report");
        reporter.ResponseQueue.Enqueue(FinalResponse(
            "A done", new UsageInfo { PromptTokens = 120_000, CompletionTokens = 0 }));

        var events = new EventBus();
        var completed = new List<SubAgentCompletedEvent>();
        events.Subscribe<SubAgentCompletedEvent>(completed.Add);

        var runner = new SubAgentRunner(
            Sessions, Registry, Catalog, events,
            new SubagentOptions { MaxParallel = 2 }, budgetGate: gate);

        var hHang = await runner.LaunchAsync(Spec("bg-hang"), CancellationToken.None);
        await WaitUntilAsync(() => runner.ActiveCount == 1, TimeSpan.FromSeconds(5));
        var hReport = await runner.LaunchAsync(Spec("bg-report"), CancellationToken.None);

        // 兄弟子代理一轮真实 usage（12 万 tokens）实报跨过 10 万上限：
        // 转换沿取消在飞的悬挂兄弟；自身本轮已到终态、诚实完成。
        Assert.Equal("A done", await hReport.WaitAsync(CancellationToken.None));
        Assert.Equal("cancelled by user", await hHang.WaitAsync(CancellationToken.None));
        Assert.Equal(120_000, gate.SpentTokens);
        Assert.True(gate.DegradedToSingleAgent);
        Assert.True(runner.DegradedToSingleAgent);

        Assert.Equal(2, completed.Count);
        Assert.True(completed.Single(e => e.SubAgentId == hReport.Id).Success);
        Assert.False(completed.Single(e => e.SubAgentId == hHang.Id).Success);

        await hHang.DisposeAsync();
        await hReport.DisposeAsync();
        hung.Gate.TrySetResult();
    }

    [Fact]
    public async Task ParallelDisabled_SemaphoreClampedToOne()
    {
        // ParallelEnabled=false（降级单 agent）：即便 MaxParallel=3，信号量收紧为 1。
        var g1 = new GatedProvider { ProviderId = "pd1", Content = "p1" };
        Registry.Add(g1);
        var scripted2 = AddProvider("pd2");
        scripted2.NonStreamContent = "p2";

        var gate = new TokenBudgetGate(1_000_000);
        var runner = NewRunner(
            gate: gate,
            options: new SubagentOptions { MaxParallel = 3, ParallelEnabled = false });

        var h1 = await runner.LaunchAsync(Spec("pd1"), CancellationToken.None);
        var h2 = await runner.LaunchAsync(Spec("pd2"), CancellationToken.None);
        await WaitUntilAsync(() => runner.ActiveCount == 1, TimeSpan.FromSeconds(5));

        // 第二个派发排队等待：未发起任何 provider 调用（并行上限钳制生效）。
        Assert.Null(scripted2.LastRequest);

        g1.Gate.TrySetResult();
        Assert.Equal("p1", await h1.WaitAsync(CancellationToken.None));
        Assert.Equal("p2", await h2.WaitAsync(CancellationToken.None));
        await h1.DisposeAsync();
        await h2.DisposeAsync();
    }
}
