// SkillHub registration + invoke→record→auto-patch loop tests (PHASE 5 T3 wiring).
using AeroCode.Skills;
using AeroCode.Skills.AutoCreate;
using AeroCode.Skills.Registry;
using Xunit;

namespace AeroCode.Tests.Skills;

public sealed class SkillHubRegistrationTests : IDisposable
{
    private readonly string _tempRoot;

    public SkillHubRegistrationTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "aerocode-hub-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, true); } catch { }
    }

    [Fact]
    public void Hub_RegistersFullCatalog_IncludingPreviouslyUnregisteredResearchSkills()
    {
        var hub = new SkillHub(_tempRoot);

        // 8 original + web_research + browser + embedding + roslyn + acquire-deploy = 13.
        Assert.True(hub.Registry.Count >= 13, $"expected >=13 skills, got {hub.Registry.Count}");

        foreach (var id in new[]
        {
            "research/web_research",
            "research/browser",
            "research/embedding",
            "analysis/roslyn",
            "research/acquire-deploy",
        })
        {
            Assert.True(hub.Get(id) is not null, $"skill '{id}' must be registered");
        }
    }

    [Fact]
    public void ListForPrompt_IncludesResearchSkills_AsAvailable()
    {
        var hub = new SkillHub(_tempRoot);
        var entries = hub.Registry.ListForPrompt();
        Assert.Contains(entries, e => e.Id == "research/web_research");
        Assert.Contains(entries, e => e.Id == "research/acquire-deploy");
    }

    [Fact]
    public async Task InvokeAsync_UnknownSkill_FailsWithoutRecording()
    {
        var hub = new SkillHub(_tempRoot);
        var result = await hub.InvokeAsync(
            "no/such-skill",
            new SkillInput(),
            new SkillContext { WorkspaceRoot = _tempRoot });

        Assert.False(result.Success);
        Assert.Contains("not found", result.Text);
        Assert.Equal(0, hub.Registry.GetStats("no/such-skill").invocations);
    }

    [Fact]
    public async Task InvokeAsync_RealSkill_RecordsInvocationIntoLearningLoop()
    {
        var hub = new SkillHub(_tempRoot);
        // Roslyn analyzer is a real registered skill; run it on an empty dir (fast, offline).
        var dir = Path.Combine(_tempRoot, "empty-target");
        Directory.CreateDirectory(dir);

        var result = await hub.InvokeAsync(
            "analysis/roslyn",
            new SkillInput { Args = new Dictionary<string, object?> { ["path"] = dir } },
            new SkillContext { WorkspaceRoot = dir });

        // No .cs files → the skill honestly reports failure; the point is the recording.
        Assert.False(result.Success);
        var (invocations, _) = hub.Registry.GetStats("analysis/roslyn");
        Assert.Equal(1, invocations);
    }

    [Fact]
    public void TryAutoCreateSkill_RealCreatorPath_WritesFileAndRegisters()
    {
        var hub = new SkillHub(_tempRoot);
        var created = hub.TryAutoCreateSkill(new AutoCreateCandidate
        {
            SuggestedId = "user/hub-wired-skill",
            SuggestedName = "Hub Wired",
            SuggestedDescription = "Created through the hub's real creator path.",
            SuggestedBody = "# Body",
            ToolCallCount = 12,
            Succeeded = true,
        });

        Assert.NotNull(created);
        Assert.True(File.Exists(created!.SourcePath));
        Assert.NotNull(hub.Get("user/hub-wired-skill"));
    }

    [Fact]
    public void TryAutoCreateSkill_BelowTriggerThreshold_ReturnsNull()
    {
        var hub = new SkillHub(_tempRoot);
        var created = hub.TryAutoCreateSkill(new AutoCreateCandidate
        {
            SuggestedId = "user/below-threshold",
            SuggestedName = "Below",
            SuggestedDescription = "Should not be created.",
            SuggestedBody = "body",
            ToolCallCount = 2, // below the 5-call trigger
            Succeeded = true,
        });
        Assert.Null(created);
    }

    [Fact]
    public void RepeatedFailures_TriggerSelfPatch_ThroughHubReportInvocation()
    {
        var hub = new SkillHub(_tempRoot);
        hub.TryAutoCreateSkill(new AutoCreateCandidate
        {
            SuggestedId = "user/will-degrade",
            SuggestedName = "WillDegrade",
            SuggestedDescription = "Will accumulate failures.",
            SuggestedBody = "# Original body",
            ToolCallCount = 10,
            Succeeded = true,
        });
        hub.Loader.LoadFromDirectory(_tempRoot, "user");

        hub.ReportInvocation("user/will-degrade", null); // success
        var patched = hub.ReportInvocation("user/will-degrade", "command moved: foo -> foo2");

        Assert.True(patched);
        var skillFile = Path.Combine(_tempRoot, "skills", "user", "will-degrade", "SKILL.md");
        Assert.True(File.Exists(skillFile));
        var content = File.ReadAllText(skillFile);
        Assert.Contains("Known Issue", content);
    }
}
