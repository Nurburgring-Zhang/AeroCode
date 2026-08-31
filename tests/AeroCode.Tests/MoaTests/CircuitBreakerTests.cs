// Copyright (c) AeroCode
// ApprovalCircuitBreaker 测试（builder-β）：连续批准阈值、成本阈值、熔断锁存、
// Deny 重置计数、无自动采纳时行为不变、Ask 归一为 Deny（broker 契约）。
// 内层 broker 复用 ToolKernelTests 的 ScriptedBroker（真实接口、按脚本出裁决）。
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AeroAgent.Moa.Safety;
using AeroAgent.Moa.Tools;
using AeroCode.Harness.EventBus;
using AeroCode.Harness.Permission;
using Xunit;

namespace AeroCode.Tests.MoaTests;

public sealed class CircuitBreakerTests
{
    private static ValueTask<PermissionDecision> ResolveAsync(ApprovalCircuitBreaker breaker, string tool = "edit_file") =>
        breaker.ResolveAsync(tool, null, CancellationToken.None);

    [Fact]
    public async Task BelowThreshold_FastPathIsUsed_InteractiveUntouched()
    {
        var interactive = new ScriptedBroker();
        var auto = new ScriptedBroker(PermissionDecision.Allow, PermissionDecision.Allow);
        var breaker = new ApprovalCircuitBreaker(
            interactive, auto, new EventBus(), "s1", maxConsecutiveApprovals: 3, maxSessionCostUsd: 100);

        Assert.Equal(PermissionDecision.Allow, await ResolveAsync(breaker));
        Assert.Equal(PermissionDecision.Allow, await ResolveAsync(breaker));

        Assert.Equal(2, auto.Consultations.Count);
        Assert.Empty(interactive.Consultations); // 未达阈值不弹窗
        Assert.False(breaker.IsBroken);
        Assert.Equal(2, breaker.ConsecutiveApprovals);
    }

    [Fact]
    public async Task ConsecutiveApprovals_NthPlusOne_ForcedToInteractive_WithEvent()
    {
        var bus = new EventBus();
        var events = new List<ApprovalCircuitBrokenEvent>();
        bus.Subscribe<ApprovalCircuitBrokenEvent>(events.Add);
        var interactive = new ScriptedBroker(PermissionDecision.Allow);
        var auto = new ScriptedBroker(
            PermissionDecision.Allow, PermissionDecision.Allow, PermissionDecision.Allow);
        var breaker = new ApprovalCircuitBreaker(
            interactive, auto, bus, "s1", maxConsecutiveApprovals: 3, maxSessionCostUsd: 100);

        Assert.Equal(PermissionDecision.Allow, await ResolveAsync(breaker));
        Assert.Equal(PermissionDecision.Allow, await ResolveAsync(breaker));
        Assert.Equal(PermissionDecision.Allow, await ResolveAsync(breaker));

        // 第 N+1 次强制人工：快速通道不再被咨询
        Assert.Equal(PermissionDecision.Allow, await ResolveAsync(breaker, "delete_file"));

        Assert.Equal(3, auto.Consultations.Count);
        Assert.Single(interactive.Consultations);
        Assert.Equal("delete_file", interactive.Consultations[0].ToolName);
        Assert.Single(events); // 熔断沿只发布一次
        Assert.Equal("s1", events[0].SessionId);
        Assert.Contains("consecutive", events[0].Reason, StringComparison.Ordinal);
        Assert.True(breaker.IsBroken);
    }

    [Fact]
    public async Task CostThreshold_Trips_AndForcesInteractive()
    {
        var bus = new EventBus();
        var events = new List<ApprovalCircuitBrokenEvent>();
        bus.Subscribe<ApprovalCircuitBrokenEvent>(events.Add);
        var interactive = new ScriptedBroker(PermissionDecision.Deny);
        var auto = new ScriptedBroker(PermissionDecision.Allow);
        var breaker = new ApprovalCircuitBreaker(
            interactive, auto, bus, "s2", maxConsecutiveApprovals: 100, maxSessionCostUsd: 5.0);

        breaker.RecordCost(3.0);
        Assert.False(breaker.IsBroken);
        breaker.RecordCost(2.5); // 累计 5.5 ≥ 5 → 熔断

        Assert.True(breaker.IsBroken);
        Assert.Single(events);
        Assert.Contains("cost", events[0].Reason, StringComparison.Ordinal);
        Assert.Equal(5.5, breaker.CostUsd, precision: 6);

        // 熔断后走人工（脚本返回 Deny）
        Assert.Equal(PermissionDecision.Deny, await ResolveAsync(breaker));
        Assert.Single(interactive.Consultations);
        Assert.Empty(auto.Consultations); // 快速通道从未被咨询（成本熔断先于任何批准发生）
    }

    [Fact]
    public async Task Deny_ResetsConsecutiveChain()
    {
        var interactive = new ScriptedBroker();
        var auto = new ScriptedBroker(
            PermissionDecision.Allow, PermissionDecision.Allow,
            PermissionDecision.Deny,
            PermissionDecision.Allow, PermissionDecision.Allow);
        var breaker = new ApprovalCircuitBreaker(
            interactive, auto, new EventBus(), "s3", maxConsecutiveApprovals: 3, maxSessionCostUsd: 100);

        for (var i = 0; i < 5; i++)
        {
            await ResolveAsync(breaker);
        }

        // allow,allow → deny(重置) → allow,allow：全程未达 3 → 从未强制弹窗
        Assert.Equal(5, auto.Consultations.Count);
        Assert.Empty(interactive.Consultations);
        Assert.Equal(2, breaker.ConsecutiveApprovals);
        Assert.False(breaker.IsBroken);
    }

    [Fact]
    public async Task Broken_Latches_EvenWhenHumanApproves()
    {
        var interactive = new ScriptedBroker(PermissionDecision.Allow, PermissionDecision.Allow);
        var auto = new ScriptedBroker(PermissionDecision.Allow);
        var breaker = new ApprovalCircuitBreaker(
            interactive, auto, new EventBus(), "s4", maxConsecutiveApprovals: 1, maxSessionCostUsd: 100);

        // 第 1 次调用 = 第 1 次批准，仍走快速通道（阈值语义：N 次批准后的第 N+1 次强制人工）
        await ResolveAsync(breaker);
        Assert.False(breaker.IsBroken);
        Assert.Single(auto.Consultations);

        // 第 2 次：连续批准已达 1 → 熔断 → 强制人工；人工再批准也不解熔（会话锁存）
        await ResolveAsync(breaker);
        Assert.True(breaker.IsBroken);
        await ResolveAsync(breaker);

        Assert.Equal(2, interactive.Consultations.Count);
        Assert.Equal(1, auto.Consultations.Count);
    }

    [Fact]
    public async Task NoAutoAdoptBroker_BehaviorUnchanged_AllThroughInteractive()
    {
        var bus = new EventBus();
        var events = new List<ApprovalCircuitBrokenEvent>();
        bus.Subscribe<ApprovalCircuitBrokenEvent>(events.Add);
        var interactive = new ScriptedBroker(
            PermissionDecision.Allow, PermissionDecision.Allow, PermissionDecision.Allow);
        // autoAdopt = null：快速通道即真实弹窗 broker —— 行为与无熔断器完全一致
        var breaker = new ApprovalCircuitBreaker(
            interactive, autoAdoptBroker: null, bus, "s5", maxConsecutiveApprovals: 3, maxSessionCostUsd: 100);

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(PermissionDecision.Allow, await ResolveAsync(breaker));
        }

        Assert.Equal(3, interactive.Consultations.Count);
        Assert.False(breaker.IsBroken);
        Assert.Empty(events); // 未触发任何熔断沿
    }

    [Fact]
    public async Task AskFromFastPath_NormalizedToDeny_BrokerContract()
    {
        // 契约：broker 只允许返回 Allow/Deny，Ask 视为 Deny —— 熔断器不得让 Ask 穿透。
        var auto = new ScriptedBroker(PermissionDecision.Ask);
        var breaker = new ApprovalCircuitBreaker(
            new ScriptedBroker(), auto, new EventBus(), "s6", maxConsecutiveApprovals: 5, maxSessionCostUsd: 100);

        var decision = await ResolveAsync(breaker);

        Assert.Equal(PermissionDecision.Deny, decision);
        Assert.Equal(0, breaker.ConsecutiveApprovals); // Deny 不推进连续批准计数
    }

    [Fact]
    public void Reset_ClearsAllState()
    {
        var breaker = new ApprovalCircuitBreaker(
            new ScriptedBroker(), new ScriptedBroker(PermissionDecision.Allow), new EventBus(),
            "s7", maxConsecutiveApprovals: 1, maxSessionCostUsd: 1);

        breaker.RecordCost(1.0);
        Assert.True(breaker.IsBroken);

        breaker.Reset();

        Assert.False(breaker.IsBroken);
        Assert.Equal(0, breaker.ConsecutiveApprovals);
        Assert.Equal(0, breaker.CostUsd);
        Assert.Null(breaker.BrokenReason);
    }

    [Fact]
    public void InvalidThresholds_Throw()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ApprovalCircuitBreaker(new ScriptedBroker(), maxConsecutiveApprovals: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ApprovalCircuitBreaker(new ScriptedBroker(), maxSessionCostUsd: 0));
        Assert.Throws<ArgumentNullException>(() =>
            new ApprovalCircuitBreaker(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => breaker_cost());
    }

    private static ApprovalCircuitBreaker breaker_cost() =>
        new(new ScriptedBroker(), maxSessionCostUsd: -1);
}
