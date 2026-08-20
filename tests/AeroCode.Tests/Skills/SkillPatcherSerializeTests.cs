// SkillPatcher.Serialize regression (G11): the version frontmatter must carry a REAL
// incremented version, never the literal text "IncrementMinor(skill.Version)".
using AeroCode.Skills;
using AeroCode.Skills.AutoCreate;
using Xunit;

namespace AeroCode.Tests.Skills;

public class SkillPatcherSerializeTests
{
    [Theory]
    [InlineData("1.2.3", "1.3.0")]
    [InlineData("0.1.0", "0.2.0")]
    [InlineData("1.2", "1.3")]
    [InlineData("2", "2.1")]
    [InlineData("abc", "abc.1")]
    [InlineData("", "0.1")]
    public void IncrementMinor_ProducesRealBumpedVersion(string input, string expected)
    {
        Assert.Equal(expected, SkillPatcher.IncrementMinor(input));
    }

    [Fact]
    public void IncrementMinor_NeverReturnsTheLiteralCallExpression()
    {
        // The historical bug wrote this literal string into the frontmatter.
        Assert.DoesNotContain("IncrementMinor(", SkillPatcher.IncrementMinor("1.0.0"));
    }
}

public sealed class SkillPatcherRoundTripTests : IDisposable
{
    private readonly string _tempRoot;

    public SkillPatcherRoundTripTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "aerocode-patch-rt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, true); } catch { }
    }

    [Fact]
    public void PatchedSkillFile_HasBumpedVersion_AndKnownIssueNote_AndNoLiteralBug()
    {
        var hub = new SkillHub(_tempRoot);
        hub.TryAutoCreateSkill(new AutoCreateCandidate
        {
            SuggestedId = "rt/versioned",
            SuggestedName = "Versioned",
            SuggestedDescription = "Version bump regression target.",
            SuggestedBody = "# Steps\n1. do thing",
            ToolCallCount = 8,
            Succeeded = true,
        });
        hub.Loader.LoadFromDirectory(_tempRoot, "user");

        hub.ReportInvocation("rt/versioned", null);
        var patched = hub.ReportInvocation("rt/versioned", "tool renamed: bar -> baz");
        Assert.True(patched);

        var file = Path.Combine(_tempRoot, "skills", "rt", "versioned", "SKILL.md");
        var content = File.ReadAllText(file);

        Assert.DoesNotContain("IncrementMinor(skill.Version)", content); // old bug gone
        Assert.Contains("version: 0.2.0", content);                       // 0.1.0 → 0.2.0 real bump
        Assert.Contains("Known Issue", content);
        Assert.Contains("tool renamed: bar -> baz", content);
        Assert.Contains("# Steps", content);                              // original body preserved
    }
}
