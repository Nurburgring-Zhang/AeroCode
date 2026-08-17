// Copyright (c) AeroCode V3.0
// SkillCreator + SkillPatcher unit tests.
using AeroCode.Skills;
using AeroCode.Skills.AutoCreate;
using Xunit;

namespace AeroCode.Tests.SkillTests;

public class SkillCreatorTests : IDisposable
{
    private readonly string _tempRoot;

    public SkillCreatorTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "aerocode-creator-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            try { Directory.Delete(_tempRoot, true); } catch { }
        }
    }

    [Fact]
    public void TryCreate_BelowThreshold_ReturnsNull()
    {
        var hub = new SkillHub(_tempRoot);
        var candidate = new AutoCreateCandidate
        {
            SuggestedId = "test/skill",
            SuggestedName = "Test",
            SuggestedDescription = "Test skill.",
            SuggestedBody = "Body",
            ToolCallCount = 2,  // below 5
            Succeeded = true,
        };
        var skill = hub.Creator.TryCreate(candidate);
        Assert.Null(skill);
    }

    [Fact]
    public void TryCreate_FailedTask_ReturnsNull()
    {
        var hub = new SkillHub(_tempRoot);
        var candidate = new AutoCreateCandidate
        {
            SuggestedId = "test/skill2",
            SuggestedName = "Test",
            SuggestedDescription = "Test skill.",
            SuggestedBody = "Body",
            ToolCallCount = 10,
            Succeeded = false,
        };
        var skill = hub.Creator.TryCreate(candidate);
        Assert.Null(skill);
    }

    [Fact]
    public void TryCreate_AboveThreshold_Success_CreatesFileAndRegisters()
    {
        var hub = new SkillHub(_tempRoot);
        var candidate = new AutoCreateCandidate
        {
            SuggestedId = "test/auto-skill",
            SuggestedName = "Auto Skill",
            SuggestedDescription = "Auto-created test skill.",
            SuggestedBody = "# Auto\n\nAuto body.",
            Tags = new[] { "auto", "test" },
            ToolCallCount = 10,
            Succeeded = true,
        };
        var skill = hub.Creator.TryCreate(candidate);
        Assert.NotNull(skill);
        Assert.True(skill!.AutoCreated);
        Assert.True(File.Exists(skill.SourcePath));
        Assert.NotNull(hub.Get("test/auto-skill"));
    }

    [Fact]
    public void TryCreate_DuplicateId_ReturnsNull()
    {
        var hub = new SkillHub(_tempRoot);
        var candidate = new AutoCreateCandidate
        {
            SuggestedId = "test/dup",
            SuggestedName = "Dup",
            SuggestedDescription = "Duplicate test.",
            SuggestedBody = "body",
            ToolCallCount = 10,
            Succeeded = true,
        };
        var first = hub.Creator.TryCreate(candidate);
        var second = hub.Creator.TryCreate(candidate);
        Assert.NotNull(first);
        Assert.Null(second);
    }

    [Fact]
    public void Description_AutoTrimmedTo60Chars()
    {
        var hub = new SkillHub(_tempRoot);
        var longDesc = "This description is way too long and should be trimmed to 60 chars";
        var candidate = new AutoCreateCandidate
        {
            SuggestedId = "test/long-desc",
            SuggestedName = "LongDesc",
            SuggestedDescription = longDesc,
            SuggestedBody = "body",
            ToolCallCount = 10,
            Succeeded = true,
        };
        var skill = hub.Creator.TryCreate(candidate);
        Assert.NotNull(skill);
        Assert.True(skill!.Description.Length <= 60);
    }
}

public class SkillPatcherTests : IDisposable
{
    private readonly string _tempRoot;

    public SkillPatcherTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "aerocode-patcher-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            try { Directory.Delete(_tempRoot, true); } catch { }
        }
    }

    [Fact]
    public void Success_DoesNotPatch()
    {
        var hub = new SkillHub(_tempRoot);
        var candidate = new AutoCreateCandidate
        {
            SuggestedId = "test/success",
            SuggestedName = "Success",
            SuggestedDescription = "Will succeed.",
            SuggestedBody = "body",
            ToolCallCount = 10,
            Succeeded = true,
        };
        hub.Creator.TryCreate(candidate);
        var patched = hub.Patcher.RecordFailureAndMaybePatch("test/success", errorMessage: null);
        Assert.False(patched);
    }

    [Fact]
    public void NonCatastrophicFailure_TriggersPatch()
    {
        var hub = new SkillHub(_tempRoot);
        var candidate = new AutoCreateCandidate
        {
            SuggestedId = "test/will-fail",
            SuggestedName = "WillFail",
            SuggestedDescription = "Will fail next time.",
            SuggestedBody = "body",
            ToolCallCount = 10,
            Succeeded = true,
        };
        hub.Creator.TryCreate(candidate);

        // Simulate that the skill has been loaded into the loader cache (Hub normally does this
        // via LoadFromDisk on startup). Without this, the patcher can't find the skill to patch.
        hub.Loader.LoadFromDirectory(_tempRoot, "user");

        // First call: success (recorded)
        hub.Patcher.RecordFailureAndMaybePatch("test/will-fail", null);
        // Second call: failure — should trigger patch since success rate is now 0.5
        var patched = hub.Patcher.RecordFailureAndMaybePatch("test/will-fail", "command not found: foo");
        Assert.True(patched);
    }

    [Fact]
    public void CatastrophicFailure_DoesNotPatch()
    {
        var hub = new SkillHub(_tempRoot);
        var candidate = new AutoCreateCandidate
        {
            SuggestedId = "test/catastrophic",
            SuggestedName = "Catastrophic",
            SuggestedDescription = "Catastrophic failure.",
            SuggestedBody = "body",
            ToolCallCount = 10,
            Succeeded = true,
        };
        hub.Creator.TryCreate(candidate);
        hub.Loader.LoadFromDirectory(_tempRoot, "user");

        hub.Patcher.RecordFailureAndMaybePatch("test/catastrophic", null);
        var patched = hub.Patcher.RecordFailureAndMaybePatch("test/catastrophic", "permission denied: /etc/shadow");
        Assert.False(patched);
    }
}
