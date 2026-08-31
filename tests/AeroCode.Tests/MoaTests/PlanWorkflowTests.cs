// Copyright (c) AeroCode
// PlanWorkflow 状态机 + PLAN.md 真实落盘 + 与 PermissionPolicy/ToolRouter 的集成裁决验证。
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AeroAgent.Moa.Tools;
using AeroAgent.Moa.Tools.Workspace;
using AeroCode.Harness.EventBus;
using AeroCode.Harness.Permission;
using AeroCode.Harness.PlanMode;
using Xunit;

namespace AeroCode.Tests.MoaTests;

/// <summary>
/// Plan 工作流钉子：状态推进与档位切换联动（Enter→Plan 档 / Approve、Cancel→Default 档），
/// 计划文件真实落盘；集成用例验证 write_plan/write_file 经 ToolRouter 的真实裁决结果。
/// </summary>
public sealed class PlanWorkflowTests : IDisposable
{
    private readonly string _dir;
    private readonly PermissionPolicy _policy;
    private readonly PlanWorkflow _workflow;

    public PlanWorkflowTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"planwf_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _policy = PermissionPolicy.CreateDefault(new EventBus());
        _workflow = new PlanWorkflow(_policy, _dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // 临时目录清理失败不影响测试结论
        }
    }

    [Fact]
    public void Enter_SetsPlanning_AndSwitchesModeToPlan()
    {
        _workflow.Enter();

        Assert.Equal(PlanState.Planning, _workflow.State);
        Assert.Equal(PermissionMode.Plan, _policy.CurrentMode);
    }

    [Fact]
    public void Enter_WritesPlanSkeletonToDisk()
    {
        _workflow.Enter();

        Assert.True(File.Exists(_workflow.PlanPath));
        Assert.Equal(Path.Combine(_dir, "PLAN.md"), _workflow.PlanPath);
        Assert.Equal("# Plan\n\n(待填写：目标 / 步骤 / 影响面 / 验收)\n", File.ReadAllText(_workflow.PlanPath));
    }

    [Fact]
    public void Enter_Twice_Idempotent_SkeletonNotOverwritten()
    {
        _workflow.Enter();
        // 用户/模型在规划期可能已填写内容：重复 Enter 不得清掉
        File.WriteAllText(_workflow.PlanPath, "# 我自己的计划");

        _workflow.Enter();

        Assert.Equal(PlanState.Planning, _workflow.State);
        Assert.Equal("# 我自己的计划", File.ReadAllText(_workflow.PlanPath));
    }

    [Fact]
    public void WritePlan_PersistsContent_ReadPlanRoundTrips()
    {
        _workflow.Enter();

        _workflow.WritePlan("# 目标\n1. 交付");

        Assert.Equal("# 目标\n1. 交付", File.ReadAllText(_workflow.PlanPath));
        Assert.Equal("# 目标\n1. 交付", _workflow.ReadPlan());
    }

    [Fact]
    public void WritePlan_OutsidePlanning_Throws()
    {
        Assert.Equal(PlanState.Inactive, _workflow.State);

        var ex = Assert.Throws<InvalidOperationException>(() => _workflow.WritePlan("x"));
        Assert.Contains("only allowed while Planning", ex.Message);
    }

    [Fact]
    public void ReadPlan_MissingFile_ReturnsNull()
    {
        Assert.Null(_workflow.ReadPlan());
    }

    [Fact]
    public void Approve_SetsApproved_AndBackToDefaultMode()
    {
        _workflow.Enter();

        _workflow.Approve();

        Assert.Equal(PlanState.Approved, _workflow.State);
        Assert.Equal(PermissionMode.Default, _policy.CurrentMode);
    }

    [Fact]
    public void Approve_Twice_Idempotent()
    {
        _workflow.Enter();
        _workflow.Approve();

        _workflow.Approve();

        Assert.Equal(PlanState.Approved, _workflow.State);
        Assert.Equal(PermissionMode.Default, _policy.CurrentMode);
    }

    [Fact]
    public void Cancel_ReturnsToInactive_AndDefaultMode()
    {
        _workflow.Enter();

        _workflow.Cancel();

        Assert.Equal(PlanState.Inactive, _workflow.State);
        Assert.Equal(PermissionMode.Default, _policy.CurrentMode);
    }

    [Fact]
    public void Approve_WithoutEnter_NoOpStaysInactive()
    {
        _workflow.Approve();

        Assert.Equal(PlanState.Inactive, _workflow.State);
        Assert.Equal(PermissionMode.Default, _policy.CurrentMode);
    }

    [Fact]
    public async Task Integration_WritePlanViaRouter_InactiveMode_DeniedAndNothingWritten()
    {
        // Inactive/Default 档：write_plan 显式 Deny——即使 broker 愿意放行也到不了执行层。
        var registry = new ToolboxRegistry();
        registry.Register(new PlanToolbox(_workflow));
        var broker = new ScriptedBroker(PermissionDecision.Allow);
        var router = new ToolRouter(registry, _policy, broker);

        var result = await router.InvokeAsync(
            "write_plan", "{\"content\":\"# 计划\"}", CancellationToken.None);

        Assert.True(result.Denied);
        Assert.Contains("forbidden by policy", result.Output);
        Assert.Empty(broker.Consultations); // 显式 Deny 在征求用户之前就拒绝
        Assert.False(File.Exists(_workflow.PlanPath));
    }

    [Fact]
    public async Task Integration_WriteFile_InPlanMode_DeniedByWhitelist()
    {
        // Plan 档硬边界：write_file 不在只读白名单，直接 Deny 且文件绝不落盘。
        _workflow.Enter();
        var registry = new ToolboxRegistry();
        registry.Register(new WorkspaceToolbox(
            new WorkspaceContext(_dir),
            new ShellRunner(_dir, TimeSpan.FromSeconds(15))));
        var broker = new ScriptedBroker(PermissionDecision.Allow);
        var router = new ToolRouter(registry, _policy, broker);

        var result = await router.InvokeAsync(
            "write_file", "{\"path\":\"sneaky.txt\",\"content\":\"x\"}", CancellationToken.None);

        Assert.True(result.Denied);
        Assert.Contains("forbidden by policy", result.Output);
        Assert.Empty(broker.Consultations);
        Assert.False(File.Exists(Path.Combine(_dir, "sneaky.txt")));
    }

    [Fact]
    public async Task Integration_ReadFile_InPlanMode_AllowedViaWhitelist()
    {
        // Plan 档白名单内的只读工具照常执行。
        File.WriteAllText(Path.Combine(_dir, "spec.md"), "只读内容");
        _workflow.Enter();
        var registry = new ToolboxRegistry();
        registry.Register(new WorkspaceToolbox(
            new WorkspaceContext(_dir),
            new ShellRunner(_dir, TimeSpan.FromSeconds(15))));
        var router = new ToolRouter(registry, _policy);

        var result = await router.InvokeAsync("read_file", "{\"path\":\"spec.md\"}", CancellationToken.None);

        Assert.True(result.Success, result.Output);
        Assert.Contains("1: 只读内容", result.Output);
    }
}
