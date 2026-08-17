// Copyright (c) AeroCode V3.0
// Preset tests (DSH 4 modes).
using AeroCode.Harness.Presets;
using Xunit;

namespace AeroCode.Tests.HarnessTests;

public class PresetTests
{
    [Fact]
    public void DefaultService_HasFourPresets()
    {
        var svc = new PresetService();
        var all = svc.List();
        Assert.Equal(4, all.Count);
    }

    [Fact]
    public void Standard_IsDefault()
    {
        var svc = new PresetService();
        var std = svc.Get("standard");
        Assert.NotNull(std);
        Assert.Equal("Standard", std!.Name);
        Assert.Equal("auto", std.ModelRoutingStrategy);
    }

    [Fact]
    public void Ptc_UsesProModel_AndStrictSafety()
    {
        var svc = new PresetService();
        var ptc = svc.Get("ptc");
        Assert.NotNull(ptc);
        Assert.Equal("v4-pro", ptc!.ModelRoutingStrategy);
        Assert.Equal("strict", ptc.SafetyPolicy);
    }

    [Fact]
    public void Minimal_HasOnlyThreeTools()
    {
        var svc = new PresetService();
        var min = svc.Get("minimal");
        Assert.NotNull(min);
        Assert.Equal(3, min!.Tools.Count);
    }

    [Fact]
    public void Creative_HasInternalInspectionTools()
    {
        var svc = new PresetService();
        var cr = svc.Get("creative");
        Assert.NotNull(cr);
        Assert.Contains("memory_inspect", cr!.Tools);
        Assert.Contains("skill_inspect", cr.Tools);
    }

    [Fact]
    public void Register_CustomPreset_AddsToList()
    {
        var svc = new PresetService();
        svc.Register(new Preset
        {
            Id = "my-preset",
            Name = "My Preset",
            Description = "Custom",
            SystemPrompt = "...",
            Tools = Array.Empty<string>(),
            ModelRoutingStrategy = "auto",
            SafetyPolicy = "permissive",
        });
        Assert.NotNull(svc.Get("my-preset"));
        Assert.Equal(5, svc.List().Count);
    }

    [Fact]
    public void BuiltIn_MatchesDshDocumentation()
    {
        // Per V3_INTEGRATION_PLAN §3.2
        Assert.NotNull(BuiltInPresets.Standard);
        Assert.NotNull(BuiltInPresets.Ptc);
        Assert.NotNull(BuiltInPresets.Minimal);
        Assert.NotNull(BuiltInPresets.Creative);
    }
}
