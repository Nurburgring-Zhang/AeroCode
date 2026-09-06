// Copyright (c) AeroCode
// B5（R2 波次 γ）PermissionPolicy guardrail 挂点行为验证：
// 挂点可选注入（null = 现行为）、咨询按审慎度只升不降（Allow<Ask<Deny）、
// 显式 Deny 顶格短路不咨询、ct 全程透传、reason 回退口径。
using System;
using System.Collections.Generic;
using System.Threading;
using AeroCode.Harness.EventBus;
using AeroCode.Harness.Permission;
using Xunit;

namespace AeroCode.Tests.HarnessTests;

/// <summary>脚本化 guardrail 咨询双：按次序出队咨询结果（空则 null = 无意见），记录全部咨询调用。</summary>
internal sealed class StubAdvisor : IPermissionGuardrailAdvisor
{
    private readonly Queue<GuardrailConsult?> _script = new();

    public StubAdvisor(params GuardrailConsult?[] consults)
    {
        foreach (var c in consults)
        {
            _script.Enqueue(c);
        }
    }

    public List<(string ToolName, IReadOnlyDictionary<string, object?>? Args, CancellationToken Ct)> Calls { get; } = new();

    public GuardrailConsult? AdviseToolCall(
        string toolName, IReadOnlyDictionary<string, object?>? args, CancellationToken cancellationToken)
    {
        Calls.Add((toolName, args, cancellationToken));
        return _script.Count > 0 ? _script.Dequeue() : null;
    }
}

public sealed class PermissionGuardrailHookTests
{
    private static PermissionPolicy NewPolicy() => PermissionPolicy.CreateDefault(new EventBus());

    private static IReadOnlyDictionary<string, object?> Args(string key, object value)
        => new Dictionary<string, object?> { [key] = value };

    [Fact]
    public void AdvisorNull_BaselineUnchanged()
    {
        var policy = NewPolicy();

        Assert.Equal(PermissionDecision.Allow, policy.Check("read_file").Decision);
        Assert.Equal(PermissionDecision.Ask, policy.Check("write_file").Decision);
        Assert.Equal(PermissionDecision.Deny, policy.Check("write_plan").Decision);
    }

    [Fact]
    public void AdvisorNoConsult_BaselineUnchanged_AndReasonAbsent()
    {
        var policy = NewPolicy();
        policy.GuardrailAdvisor = new StubAdvisor(); // 恒返回 null = 无意见

        var read = policy.Check("read_file");
        Assert.Equal(PermissionDecision.Allow, read.Decision);
        Assert.Null(read.Reason);

        var write = policy.Check("write_file");
        Assert.Equal(PermissionDecision.Ask, write.Decision);
        Assert.Null(write.Reason);
    }

    [Fact]
    public void AdvisorAsk_UpgradeOfAllowTool_ReasonFromConsult()
    {
        var policy = NewPolicy();
        policy.GuardrailAdvisor = new StubAdvisor(
            new GuardrailConsult(PermissionDecision.Ask, "guardrail review"));

        var result = policy.Check("read_file"); // 基线 Allow → 咨询 Ask 升级

        Assert.Equal(PermissionDecision.Ask, result.Decision);
        Assert.Equal("guardrail review", result.Reason);
    }

    [Fact]
    public void AdvisorDeny_UpgradeOfAskTool()
    {
        var policy = NewPolicy();
        policy.GuardrailAdvisor = new StubAdvisor(
            new GuardrailConsult(PermissionDecision.Deny, "blocked by guardrail"));

        var result = policy.Check("write_file"); // 基线 Ask → 咨询 Deny 升级

        Assert.Equal(PermissionDecision.Deny, result.Decision);
        Assert.Equal("blocked by guardrail", result.Reason);
    }

    [Fact]
    public void AdvisorAllow_NeverDowngradesBaseline()
    {
        var policy = NewPolicy();
        policy.GuardrailAdvisor = new StubAdvisor(new GuardrailConsult(PermissionDecision.Allow));

        // Ask 基线不被咨询降级
        var write = policy.Check("write_file");
        Assert.Equal(PermissionDecision.Ask, write.Decision);

        // Allow 基线保持放行（同级咨询不改变裁决，也不产生 reason）
        var read = policy.Check("read_file");
        Assert.Equal(PermissionDecision.Allow, read.Decision);
        Assert.Null(read.Reason);
    }

    [Fact]
    public void ExplicitDeny_ShortCircuits_BeforeAdvisor()
    {
        var policy = NewPolicy();
        var advisor = new StubAdvisor(new GuardrailConsult(PermissionDecision.Allow));
        policy.GuardrailAdvisor = advisor;

        // write_plan 显式 Deny（Plan 文件规则）：顶格审慎度，咨询根本不发生
        var result = policy.Check("write_plan");

        Assert.Equal(PermissionDecision.Deny, result.Decision);
        Assert.Equal("Explicitly denied", result.Reason);
        Assert.Empty(advisor.Calls);
    }

    [Fact]
    public void PlanMode_UnknownToolDeny_NotDowngradedByAdvisor()
    {
        var policy = NewPolicy();
        policy.CurrentMode = PermissionMode.Plan;
        policy.GuardrailAdvisor = new StubAdvisor(new GuardrailConsult(PermissionDecision.Allow));

        // Plan 档未知工具基线 Deny：Allow 咨询绝不降格
        var result = policy.Check("ghost_tool");

        Assert.Equal(PermissionDecision.Deny, result.Decision);
        Assert.Contains("plan mode", result.Reason);
    }

    [Fact]
    public void DefaultMode_UnknownTool_AskBaseline_CanBeUpgradedToDeny()
    {
        var policy = NewPolicy();
        policy.GuardrailAdvisor = new StubAdvisor(
            new GuardrailConsult(PermissionDecision.Deny, "guardrail deny"));

        var result = policy.Check("ghost_tool"); // 未知工具默认 Ask → 咨询 Deny 升级

        Assert.Equal(PermissionDecision.Deny, result.Decision);
        Assert.Equal("guardrail deny", result.Reason);
    }

    [Fact]
    public void AdvisorConsult_AndDangerousPatternOverride_Interplay()
    {
        // Bypass 档 + 安全命令：基线 Allow（Bypass 放行 Ask）、Override 返回 Allow；
        // guardrail 咨询 Ask 升级 → Ask（Override 不得再降回 Allow）。
        var bypass = NewPolicy();
        bypass.CurrentMode = PermissionMode.Bypass;
        bypass.GuardrailAdvisor = new StubAdvisor(new GuardrailConsult(PermissionDecision.Ask, "guardrail ask"));
        var upgraded = bypass.Check("run_shell", Args("command", "echo hello"));

        Assert.Equal(PermissionDecision.Ask, upgraded.Decision);
        Assert.Equal("guardrail ask", upgraded.Reason);

        // 咨询无意见 + 危险命令（Bypass 档基线 Allow）：原 Override 升级语义不变——
        // Bypass 放行的是 Ask 基线，不是危险探测（R1 行为回归钉）。
        var bypassPlain = NewPolicy();
        bypassPlain.CurrentMode = PermissionMode.Bypass;
        bypassPlain.GuardrailAdvisor = new StubAdvisor(); // null = 无意见
        var overridden = bypassPlain.Check("run_shell", Args("command", "rm -rf /tmp/x"));

        Assert.Equal(PermissionDecision.Ask, overridden.Decision);
        Assert.Equal("Rule override", overridden.Reason);
    }

    [Fact]
    public void CancellationToken_AndArgs_PassedToAdvisor()
    {
        var policy = NewPolicy();
        var advisor = new StubAdvisor();
        policy.GuardrailAdvisor = advisor;
        using var cts = new CancellationTokenSource();

        policy.Check("write_file", Args("path", "a.txt"), cts.Token);

        var call = Assert.Single(advisor.Calls);
        Assert.Equal("write_file", call.ToolName);
        Assert.Equal("a.txt", Assert.IsType<string>(call.Args!["path"]));
        Assert.Equal(cts.Token, call.Ct);
    }

    [Fact]
    public void ConsultReasonNull_FallsBackToGuardrailReason()
    {
        var policy = NewPolicy();
        policy.GuardrailAdvisor = new StubAdvisor(
            new GuardrailConsult(PermissionDecision.Deny, Reason: null));

        var result = policy.Check("edit_file");

        Assert.Equal(PermissionDecision.Deny, result.Decision);
        Assert.Equal("guardrail", result.Reason);
    }
}
