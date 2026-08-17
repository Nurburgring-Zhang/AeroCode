// Copyright (c) AeroCode V3.0
// PlanMode tests.
using AeroCode.Harness.EventBus;
using AeroCode.Harness.PlanMode;
using Xunit;

namespace AeroCode.Tests.HarnessTests;

public class PlanModeTests
{
    private static PlanModeManager New() => new(new EventBus());

    [Fact]
    public void InitiallyDisabled()
    {
        var pm = New();
        Assert.False(pm.IsEnabled);
    }

    [Fact]
    public void EnableDisable_Toggles()
    {
        var pm = New();
        pm.Enable();
        Assert.True(pm.IsEnabled);
        pm.Disable();
        Assert.False(pm.IsEnabled);
    }

    [Fact]
    public void SubmitIfPlanMode_WhenDisabled_ReturnsButNoPending()
    {
        var pm = New();
        var edit = pm.SubmitIfPlanMode("write_file", "test.cs", "content");
        Assert.Equal(0, pm.PendingCount);
        Assert.NotNull(edit);
    }

    [Fact]
    public void SubmitIfPlanMode_WhenEnabled_AddsToPending()
    {
        var pm = New();
        pm.Enable();
        var edit = pm.SubmitIfPlanMode("write_file", "test.cs", "content");
        Assert.Equal(1, pm.PendingCount);
        Assert.NotNull(edit);
    }

    [Fact]
    public void Approve_WritesFile_AndRemovesFromPending()
    {
        var pm = New();
        pm.Enable();
        var edit = pm.SubmitIfPlanMode("write_file", "test.cs", "hello world");
        var written = "";
        var state = pm.Approve(edit.Id, (path, content) => { written = $"{path}={content}"; });
        Assert.Equal(WriteState.Applied, state);
        Assert.Equal("test.cs=hello world", written);
        Assert.Equal(0, pm.PendingCount);
    }

    [Fact]
    public void Reject_RemovesFromPending()
    {
        var pm = New();
        pm.Enable();
        var edit = pm.SubmitIfPlanMode("write_file", "test.cs", "x");
        Assert.True(pm.Reject(edit.Id));
        Assert.Equal(0, pm.PendingCount);
    }

    [Fact]
    public void ApproveAll_AppliesAllPending()
    {
        var pm = New();
        pm.Enable();
        pm.SubmitIfPlanMode("write_file", "a.cs", "1");
        pm.SubmitIfPlanMode("write_file", "b.cs", "2");
        pm.SubmitIfPlanMode("write_file", "c.cs", "3");
        var applied = pm.ApproveAll((_, _) => { /* no-op */ });
        Assert.Equal(3, applied);
        Assert.Equal(0, pm.PendingCount);
    }
}
