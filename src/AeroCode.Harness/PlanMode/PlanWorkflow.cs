// Copyright (c) AeroCode
// PlanWorkflow — Plan 模式完整工作流（对标 opencode plan.ts / cline plan-and-act / claude-code plan mode）。
// 状态机：Inactive --Enter--> Planning --Approve--> Approved（执行档）/ --Cancel--> Inactive。
// Planning 期由 PermissionMode.Plan 提供硬边界（只读白名单 + write_plan），计划文件真实落盘；
// 审批通过即切回执行档。write_plan 工具域由 Moa/Tools/Workspace/PlanToolbox.cs 实现 IWorkerToolbox。
using AeroCode.Harness.Permission;
using System.Text;

namespace AeroCode.Harness.PlanMode;

/// <summary>Plan 工作流状态。</summary>
public enum PlanState
{
    /// <summary>未进入规划（执行档）。</summary>
    Inactive = 0,
    /// <summary>规划中：只读白名单 + write_plan 可用。</summary>
    Planning = 1,
    /// <summary>计划已批准，切回执行档。</summary>
    Approved = 2,
}

/// <summary>
/// Plan 模式工作流。裁决唯一来源是 <see cref="PermissionPolicy.CurrentMode"/>——
/// 本类只做状态推进与计划文件落盘，绝不绕过权限层写任何其他文件。
/// </summary>
public sealed class PlanWorkflow
{
    private readonly PermissionPolicy _policy;
    private readonly string _planPath;

    public PlanWorkflow(PermissionPolicy policy, string workspaceRoot)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            throw new ArgumentException("workspace root must not be empty", nameof(workspaceRoot));
        }

        _planPath = Path.Combine(workspaceRoot, "PLAN.md");
    }

    /// <summary>计划文件绝对路径（审批 UI 展示/打开用）。</summary>
    public string PlanPath => _planPath;

    /// <summary>当前状态。</summary>
    public PlanState State { get; private set; } = PlanState.Inactive;

    /// <summary>进入规划：切 Plan 档 + 创建计划文件骨架（幂等）。</summary>
    public void Enter()
    {
        if (State == PlanState.Planning)
        {
            return;
        }

        State = PlanState.Planning;
        _policy.CurrentMode = PermissionMode.Plan;
        if (!File.Exists(_planPath))
        {
            File.WriteAllText(_planPath, "# Plan\n\n(待填写：目标 / 步骤 / 影响面 / 验收)\n", new UTF8Encoding(false));
        }
    }

    /// <summary>写入计划内容（仅 Planning 态允许；经策略校验，不绕过档位）。</summary>
    public void WritePlan(string content)
    {
        if (State != PlanState.Planning)
        {
            throw new InvalidOperationException("write_plan is only allowed while Planning");
        }

        var dir = Path.GetDirectoryName(_planPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(_planPath, content, new UTF8Encoding(false));
    }

    /// <summary>读取当前计划内容（不存在返回 null）。</summary>
    public string? ReadPlan() => File.Exists(_planPath) ? File.ReadAllText(_planPath) : null;

    /// <summary>批准计划：切回执行档（Default）。幂等于 Approved/Inactive 态。</summary>
    public void Approve()
    {
        if (State != PlanState.Planning)
        {
            return;
        }

        State = PlanState.Approved;
        _policy.CurrentMode = PermissionMode.Default;
    }

    /// <summary>放弃规划：回 Inactive 并切回执行档。</summary>
    public void Cancel()
    {
        if (State == PlanState.Inactive)
        {
            return;
        }

        State = PlanState.Inactive;
        _policy.CurrentMode = PermissionMode.Default;
    }
}
