// Copyright (c) AeroCode
// ACS-3 技能束 + 能力注册表 + 技能规格门测试。
using AeroCode.Harness.Acs;
using AeroCode.Skills.Bundled.Acs;
using Xunit;

namespace AeroCode.Tests.AcsTests;

public sealed class AcsSkillBundleTests
{
    [Fact]
    public void AllSixAcsSkills_LoadEmbeddedPrompt()
    {
        var skills = new AcsSkillBase[]
        {
            new UniversalTaskCodeSkill(),
            new LoopEngineeringSkill(),
            new GraphEngineeringSkill(),
            new SelfVerifyScalingSkill(),
            new TokenThriftSkill(),
            new QoderNativeIntegrationSkill(),
        };

        foreach (var s in skills)
        {
            var prompt = s.GetSystemPrompt();
            Assert.False(prompt.StartsWith("[ACS skill resource not found"),
                $"{s.Id} 内嵌资源未加载: {prompt}");
            Assert.True(prompt.Length > 1000, $"{s.Id} 技能正文过短（{prompt.Length} 字符），疑似未加载全文");
            Assert.True(s.IsAvailable());
            Assert.Equal("acs", s.Category);
            Assert.Equal("2.3.0", s.Version);
        }
    }

    [Fact]
    public void AcsSkills_HaveDistinctIds()
    {
        var ids = new[]
        {
            new UniversalTaskCodeSkill().Id,
            new LoopEngineeringSkill().Id,
            new GraphEngineeringSkill().Id,
            new SelfVerifyScalingSkill().Id,
            new TokenThriftSkill().Id,
            new QoderNativeIntegrationSkill().Id,
        };
        Assert.Equal(ids.Length, ids.Distinct().Count());
    }

    [Fact]
    public async Task AcsSkill_ExecuteReturnsPrompt_NoSideEffect()
    {
        var skill = new UniversalTaskCodeSkill();
        var result = await skill.ExecuteAsync(
            new AeroCode.Skills.Registry.SkillInput(),
            new AeroCode.Skills.Registry.SkillContext());
        Assert.True(result.Success);
        Assert.True(result.Text.Length > 1000);
    }
}

public sealed class CapabilityRegistryTests
{
    [Fact]
    public void RegisterAndQuery_ByLayer()
    {
        var reg = new CapabilityRegistry();
        reg.Register(new AcsCapability("c1", "成本闸门", AcsCapabilityLayer.Loop, "loop-cost", "active"));
        reg.Register(new AcsCapability("c2", "任务分级", AcsCapabilityLayer.Harness, "grading", "active"));

        Assert.Equal(2, reg.Capabilities.Count);
        Assert.Single(reg.ByLayer(AcsCapabilityLayer.Loop));
        Assert.Single(reg.ByLayer(AcsCapabilityLayer.Harness));
        Assert.Empty(reg.ByLayer(AcsCapabilityLayer.Prompt));
    }

    [Fact]
    public void DuplicateId_Rejected()
    {
        var reg = new CapabilityRegistry();
        reg.Register(new AcsCapability("c1", "能力", AcsCapabilityLayer.Loop, "d1", "active"));
        var (ok, err) = reg.Register(new AcsCapability("c1", "能力2", AcsCapabilityLayer.Loop, "d2", "active"));
        Assert.False(ok);
        Assert.Contains("重复", err);
    }

    [Fact]
    public void MissingNameOrDomain_Rejected()
    {
        var reg = new CapabilityRegistry();
        var (ok, err) = reg.Register(new AcsCapability("c1", "", AcsCapabilityLayer.Loop, "d1", "active"));
        Assert.False(ok);
    }

    [Fact]
    public void CoverageDomains_Distinct()
    {
        var reg = new CapabilityRegistry();
        reg.Register(new AcsCapability("c1", "能力1", AcsCapabilityLayer.Loop, "cost", "active"));
        reg.Register(new AcsCapability("c2", "能力2", AcsCapabilityLayer.Loop, "cost", "active"));
        reg.Register(new AcsCapability("c3", "能力3", AcsCapabilityLayer.Graph, "dag", "active"));
        Assert.Equal(2, reg.CoverageDomains().Count);
    }
}

public sealed class SkillSpecGateTests
{
    private readonly SkillSpecGate _gate = new();

    [Fact]
    public void ValidSkill_Passes()
    {
        var r = _gate.Validate(80, "name: foo\ndescription: bar");
        Assert.True(r.Passed);
    }

    [Fact]
    public void OverLineLimit_Fails()
    {
        var r = _gate.Validate(SkillSpecGate.MainEntryMaxLines + 1, "name: foo\ndescription: bar");
        Assert.False(r.Passed);
        Assert.Contains(r.Problems, p => p.Contains("主入口"));
    }

    [Fact]
    public void MissingRequiredSection_Fails()
    {
        var r = _gate.Validate(50, "name: foo"); // 缺 description
        Assert.False(r.Passed);
        Assert.Contains(r.Problems, p => p.Contains("description"));
    }

    [Fact]
    public void UnreachableReference_Fails()
    {
        var r = _gate.Validate(50, "name: foo\ndescription: bar",
            referencedFiles: new[] { "missing.md" },
            fileExists: _ => false);
        Assert.False(r.Passed);
        Assert.Contains(r.Problems, p => p.Contains("不可达"));
    }

    [Fact]
    public void ReachableReference_Passes()
    {
        var r = _gate.Validate(50, "name: foo\ndescription: bar",
            referencedFiles: new[] { "exists.md" },
            fileExists: f => f == "exists.md");
        Assert.True(r.Passed);
    }
}
