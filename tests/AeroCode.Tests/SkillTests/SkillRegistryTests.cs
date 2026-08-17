// Copyright (c) AeroCode V3.0
// SkillRegistry unit tests.
using AeroCode.Skills;
using AeroCode.Skills.Registry;
using Xunit;

namespace AeroCode.Tests.SkillTests;

public class SkillRegistryTests : IDisposable
{
    private readonly string _tempRoot;

    public SkillRegistryTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "aerocode-test-" + Guid.NewGuid().ToString("N"));
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
    public void Register_AddsSkill()
    {
        var hub = new SkillHub(_tempRoot);
        var before = hub.Registry.Count;
        // All 7 bundled skills auto-register.
        Assert.True(before >= 7);
    }

    [Fact]
    public void Get_ReturnsRegisteredSkill()
    {
        var hub = new SkillHub(_tempRoot);
        var skill = hub.Get("engineering/code-review");
        Assert.NotNull(skill);
        Assert.Equal("Code Review", skill!.Name);
    }

    [Fact]
    public void List_ByCategory_ReturnsFiltered()
    {
        var hub = new SkillHub(_tempRoot);
        var engineering = hub.List(category: "engineering");
        var productivity = hub.List(category: "productivity");

        Assert.NotEmpty(engineering);
        Assert.NotEmpty(productivity);
        Assert.All(engineering, s => Assert.Equal("engineering", s.Category));
        Assert.All(productivity, s => Assert.Equal("productivity", s.Category));
    }

    [Fact]
    public void ListForPrompt_ReturnsLevel0Entries()
    {
        var hub = new SkillHub(_tempRoot);
        var entries = hub.Registry.ListForPrompt();
        Assert.NotEmpty(entries);
        Assert.All(entries, e => Assert.True(e.Description.Length <= 60, $"Description too long: {e.Description}"));
    }

    [Fact]
    public void RecordInvocation_UpdatesStats()
    {
        var registry = new SkillRegistry();
        registry.RecordInvocation("test/skill", success: true);
        registry.RecordInvocation("test/skill", success: false);

        var (count, rate) = registry.GetStats("test/skill");
        Assert.Equal(2, count);
        Assert.Equal(0.5, rate, 3);
    }

    [Fact]
    public void DuplicateRegistration_Throws()
    {
        var registry = new SkillRegistry();
        var skill = new StubSkill("dup", "Dup", "Test dup skill.");
        registry.Register(skill);
        Assert.Throws<InvalidOperationException>(() => registry.Register(skill));
    }
}

internal sealed class StubSkill : ISkill
{
    public StubSkill(string id, string name, string desc) { Id = id; Name = name; Description = desc; }
    public string Id { get; }
    public string Name { get; }
    public string Description { get; }
    public string Category => "test";
    public string Author => "tester";
    public string Version => "1.0.0";
    public IReadOnlyList<string> Tags => Array.Empty<string>();
    public string GetSystemPrompt() => Name;
    public bool IsAvailable() => true;
    public Task<SkillResult> ExecuteAsync(SkillInput input, SkillContext ctx, CancellationToken ct = default)
        => Task.FromResult(new SkillResult { Text = "ok" });
}
