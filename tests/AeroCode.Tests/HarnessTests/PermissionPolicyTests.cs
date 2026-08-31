// Copyright (c) AeroCode V3.0
// Permission policy tests.
using AeroCode.Harness.EventBus;
using AeroCode.Harness.Permission;
using Xunit;

namespace AeroCode.Tests.HarnessTests;

public class PermissionPolicyTests
{
    private static PermissionPolicy NewPolicy()
    {
        var bus = new EventBus();
        return PermissionPolicy.CreateDefault(bus);
    }

    [Fact]
    public void ReadFile_IsAllowed()
    {
        var p = NewPolicy();
        var r = p.Check("read_file");
        Assert.Equal(PermissionDecision.Allow, r.Decision);
    }

    [Fact]
    public void WriteFile_Asks()
    {
        var p = NewPolicy();
        var r = p.Check("write_file", new Dictionary<string, object?>
        {
            ["path"] = "test.cs",
            ["content"] = "x",
        });
        Assert.Equal(PermissionDecision.Ask, r.Decision);
    }

    [Fact]
    public void RunShell_SafeCommand_Asks()
    {
        // run_shell 接线真实执行器后基线收紧为 Ask（Permission.cs 既定变更）：
        // 安全命令也需经授权面批准；危险命令由 Override 继续升级为 Ask。
        var p = NewPolicy();
        var r = p.Check("run_shell", new Dictionary<string, object?>
        {
            ["command"] = "git status",
        });
        Assert.Equal(PermissionDecision.Ask, r.Decision);
    }

    [Fact]
    public void RunShell_DangerousCommand_Asks()
    {
        var p = NewPolicy();
        var r = p.Check("run_shell", new Dictionary<string, object?>
        {
            ["command"] = "rm -rf /",
        });
        Assert.Equal(PermissionDecision.Ask, r.Decision);
    }

    [Fact]
    public void RunShell_DelCommand_Asks()
    {
        var p = NewPolicy();
        var r = p.Check("run_shell", new Dictionary<string, object?>
        {
            ["command"] = "del file.txt",
        });
        Assert.Equal(PermissionDecision.Ask, r.Decision);
    }

    [Fact]
    public void UnknownTool_Asks()
    {
        var p = NewPolicy();
        var r = p.Check("totally_unknown_tool");
        Assert.Equal(PermissionDecision.Ask, r.Decision);
    }

    [Fact]
    public void GitPush_Asks()
    {
        var p = NewPolicy();
        var r = p.Check("git_push");
        Assert.Equal(PermissionDecision.Ask, r.Decision);
    }

    [Fact]
    public void SetDefaultDecision_UnknownTool_CreatesRule()
    {
        var p = NewPolicy();
        p.SetDefaultDecision("mcp_notes_delete", PermissionDecision.Allow);

        Assert.Equal(PermissionDecision.Allow, p.Check("mcp_notes_delete").Decision);
        var rule = Assert.Single(p.ListRules(), r => r.ToolName == "mcp_notes_delete");
        Assert.Equal("user decision", rule.Notes);
    }

    [Fact]
    public void SetDefaultDecision_Deny_WinsOverOverride()
    {
        // 用户显式拒绝 run_shell 后，危险模式 Override 不得把安全命令翻回放行。
        var p = NewPolicy();
        p.SetDefaultDecision("run_shell", PermissionDecision.Deny);

        var safe = p.Check("run_shell", new Dictionary<string, object?> { ["command"] = "git status" });
        Assert.Equal(PermissionDecision.Deny, safe.Decision);

        var dangerous = p.Check("run_shell", new Dictionary<string, object?> { ["command"] = "rm -rf /" });
        Assert.Equal(PermissionDecision.Deny, dangerous.Decision);
    }

    [Fact]
    public void SetDefaultDecision_Allow_OverrideStillEscalates()
    {
        // 显式 Allow 不压制危险模式探测：Override 仍可升级为 Ask。
        var p = NewPolicy();
        p.SetDefaultDecision("run_shell", PermissionDecision.Allow);

        var dangerous = p.Check("run_shell", new Dictionary<string, object?> { ["command"] = "rm -rf /" });
        Assert.Equal(PermissionDecision.Ask, dangerous.Decision);
    }

    [Fact]
    public void SetDefaultDecision_Ask_OverrideCannotDowngrade()
    {
        // 用户把 run_shell 设为"每次询问"后，Override 对安全命令返回 Allow
        // 不得把决策降级放行——Override 只许升级审慎度。
        var p = NewPolicy();
        p.SetDefaultDecision("run_shell", PermissionDecision.Ask);

        var safe = p.Check("run_shell", new Dictionary<string, object?> { ["command"] = "git status" });
        Assert.Equal(PermissionDecision.Ask, safe.Decision);

        // 危险命令维持 Ask（升级方向不受影响）
        var dangerous = p.Check("run_shell", new Dictionary<string, object?> { ["command"] = "rm -rf /" });
        Assert.Equal(PermissionDecision.Ask, dangerous.Decision);
    }

    [Fact]
    public void ListRules_ReturnsOrderedSnapshot()
    {
        var p = NewPolicy();
        var rules = p.ListRules();

        Assert.Contains(rules, r => r.ToolName == "read_file" && r.DefaultDecision == PermissionDecision.Allow);
        Assert.Contains(rules, r => r.ToolName == "run_shell" && r.Override is not null);
        // 快照按名有序，且后续修改策略不影响已返回的列表
        var names = rules.Select(r => r.ToolName).ToList();
        Assert.Equal(names.OrderBy(n => n, StringComparer.Ordinal), names);
    }
}
