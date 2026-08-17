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
    public void RunShell_SafeCommand_Allowed()
    {
        var p = NewPolicy();
        var r = p.Check("run_shell", new Dictionary<string, object?>
        {
            ["command"] = "git status",
        });
        Assert.Equal(PermissionDecision.Allow, r.Decision);
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
}
