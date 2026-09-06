// Copyright (c) AeroCode
// R3 修复（S-MED-2）：审批熔断器对 advisor 自动放行通道生效的端到端测试。
// 修复前：熔断后路由到同一 DialogPermissionBroker，其 advisor risk=low 仍自动放行，
// 连续批准限制对自动通道形同虚设。修复后：breaker.IsBroken 经 forceInteractive 信号
// 传进 broker——熔断态跳过 advisor 自动采纳分支直接弹窗。
// 不真启 LLM：advisor 用脚本化 provider（复用 MoaTests.AdvisorScriptedProvider），
// 弹窗用 ScriptedPresenter（与 AutoAdoptTighteningTests 同一约定）。
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AeroAgent.Moa.Safety;
using AeroCode.App.Services;
using AeroCode.Harness.EventBus;
using AeroCode.Harness.Permission;
using AeroCode.Tests.MoaTests;
using Xunit;

namespace AeroCode.Tests.AppTests;

public sealed class CircuitBreakerForceInteractiveTests : IDisposable
{
    private readonly PermissionPolicy _policy;
    private readonly JsonPermissionStore _store;
    private readonly ScriptedPresenter _presenter;
    private readonly AdvisorScriptedProvider _provider;
    private readonly string _dir;

    public CircuitBreakerForceInteractiveTests()
    {
        _policy = PermissionPolicy.CreateDefault(new EventBus()); // write_file = Ask
        _dir = Path.Combine(Path.GetTempPath(), $"breaker_force_{Guid.NewGuid():N}");
        _store = new JsonPermissionStore(Path.Combine(_dir, "permissions.json"));
        _presenter = new ScriptedPresenter();
        _provider = new AdvisorScriptedProvider
        {
            // 全程判 low/allow：钉死「即使 advisor 一直说低风险，熔断后也必须弹窗」。
            DefaultContent = "{\"recommend\":\"allow\",\"risk\":\"low\",\"reason\":\"looks harmless\"}",
        };
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort 清理 */ }
    }

    /// <summary>组合根同构接线：broker 的 forceInteractive 闭包读 breaker.IsBroken。</summary>
    private (DialogPermissionBroker Broker, ApprovalCircuitBreaker Breaker) CreateWiring(int burstLimit = 2)
    {
        ApprovalCircuitBreaker? breaker = null;
        var broker = new DialogPermissionBroker(
            _policy,
            _store,
            _presenter,
            advisor: new PermissionAdvisor(_provider, "cheap-model"),
            autoApproveLowRisk: true,
            forceInteractive: () => breaker?.IsBroken == true);
        breaker = new ApprovalCircuitBreaker(
            interactiveBroker: broker,
            autoAdoptBroker: null,
            eventBus: null,
            sessionId: "s-med-2-test",
            maxConsecutiveApprovals: burstLimit,
            maxSessionCostUsd: 5.0);
        return (broker, breaker);
    }

    private static Dictionary<string, object?> Args() => new()
    {
        ["path"] = "notes.txt",
    };

    [Fact]
    public async Task BurstTripped_AdvisorStillLowRisk_ForcesDialog_EveryCallAfter()
    {
        var (_, breaker) = CreateWiring(burstLimit: 2);

        // 熔断前：risk=low 自动放行（现行为），不弹窗。
        Assert.Equal(PermissionDecision.Allow, await breaker.ResolveAsync("write_file", Args(), CancellationToken.None));
        Assert.Equal(PermissionDecision.Allow, await breaker.ResolveAsync("write_file", Args(), CancellationToken.None));
        Assert.Empty(_presenter.Prompts);
        Assert.False(breaker.IsBroken);

        // 第 3 次：连续批准达到阈值 2 → 熔断——即使 advisor 仍判 risk=low 也必须弹窗。
        _presenter.Enqueue(new PermissionDialogResult(Approved: true, Remember: false));
        Assert.Equal(PermissionDecision.Allow, await breaker.ResolveAsync("write_file", Args(), CancellationToken.None));
        Assert.True(breaker.IsBroken);
        var prompt = Assert.Single(_presenter.Prompts);
        Assert.Contains("AI 建议", prompt.AdvisorNote ?? string.Empty, StringComparison.Ordinal); // 建议仍随弹窗展示

        // 熔断锁存：人工批准后也不解熔，下一次照样弹窗（自动放行通道彻底失效）。
        _presenter.Enqueue(new PermissionDialogResult(Approved: true, Remember: false));
        Assert.Equal(PermissionDecision.Allow, await breaker.ResolveAsync("write_file", Args(), CancellationToken.None));
        Assert.Equal(2, _presenter.Prompts.Count);

        // advisor 每轮仍被咨询（建议供人工参考），但不再产生任何自动放行。
        Assert.Equal(4, _provider.CallCount);
    }

    [Fact]
    public async Task BurstTripped_DialogDenied_DeniedNotAutoApproved()
    {
        var (_, breaker) = CreateWiring(burstLimit: 1);

        // 第 1 次：计数 0<1，未熔断 → risk=low 自动放行（现行为）。
        Assert.Equal(PermissionDecision.Allow, await breaker.ResolveAsync("write_file", Args(), CancellationToken.None));
        Assert.Empty(_presenter.Prompts);

        // 第 2 次：连续批准达到阈值 1 → 熔断——risk=low 也弹窗；人工拒绝 = Deny，绝不静默放行。
        _presenter.Enqueue(new PermissionDialogResult(Approved: false, Remember: false));
        var decision = await breaker.ResolveAsync("write_file", Args(), CancellationToken.None);

        Assert.Equal(PermissionDecision.Deny, decision);
        Assert.True(breaker.IsBroken);
        Assert.Single(_presenter.Prompts);
    }

    [Fact]
    public async Task CostTrip_ForcesDialog_Too()
    {
        var (_, breaker) = CreateWiring(burstLimit: 25);

        // 成本通道熔断：累计成本达阈值后，下一次 Ask 即使 risk=low 也弹窗。
        breaker.RecordCost(5.0);
        Assert.True(breaker.IsBroken);

        _presenter.Enqueue(new PermissionDialogResult(Approved: true, Remember: false));
        Assert.Equal(PermissionDecision.Allow, await breaker.ResolveAsync("write_file", Args(), CancellationToken.None));
        Assert.Single(_presenter.Prompts);
    }

    [Fact]
    public async Task NoForceInteractiveSignal_PreR3Behavior_AutoAdoptUnaffected()
    {
        // 信号缺省（null）= 现行为逐字节一致：无熔断概念参与，risk=low 照旧自动放行。
        var broker = new DialogPermissionBroker(
            _policy,
            _store,
            _presenter,
            advisor: new PermissionAdvisor(_provider, "cheap-model"),
            autoApproveLowRisk: true);

        var decision = await broker.ResolveAsync("write_file", Args(), CancellationToken.None);

        Assert.Equal(PermissionDecision.Allow, decision);
        Assert.Empty(_presenter.Prompts);
    }
}
