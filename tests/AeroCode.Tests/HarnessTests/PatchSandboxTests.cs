// PatchEngine sandbox-guard tests (review R2-P1): patch paths must never escape rootDir.
using AeroCode.Harness.Patch;
using Xunit;

namespace AeroCode.Tests.HarnessTests;

public sealed class PatchSandboxTests : IDisposable
{
    private readonly string _root;

    public PatchSandboxTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "aerocode-patch-sandbox-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "inside.txt"), "OLD");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
    }

    private static Patch ReplacePatch(string oldText, string newText) => new()
    {
        FilePath = "ignored", // ApplyBatch uses the tuple path
        Kind = PatchKind.Replace,
        OldText = oldText,
        NewText = newText,
        Fuzzy = false,
    };

    [Fact]
    public void TryResolveInsideRoot_RelativeInside_IsAllowed()
    {
        Assert.True(PatchEngine.TryResolveInsideRoot(_root, "inside.txt", out var abs));
        Assert.Equal(Path.Combine(Path.GetFullPath(_root), "inside.txt"), abs);
    }

    [Fact]
    public void TryResolveInsideRoot_Traversal_IsRejected()
    {
        Assert.False(PatchEngine.TryResolveInsideRoot(_root, "../escape.txt", out _));
        Assert.False(PatchEngine.TryResolveInsideRoot(_root, "a/../../escape.txt", out _));
    }

    [Fact]
    public void TryResolveInsideRoot_AbsoluteOutside_IsRejected()
    {
        var outside = Path.Combine(Path.GetTempPath(), "somewhere-else.txt");
        Assert.False(PatchEngine.TryResolveInsideRoot(_root, outside, out _));
    }

    [Fact]
    public void ApplyBatch_EscapingPath_RejectsWholeBatch_AndWritesNothing()
    {
        var engine = new PatchEngine();
        var outsideTarget = Path.Combine(Path.GetTempPath(), "aerocode-escape-target-" + Guid.NewGuid().ToString("N") + ".txt");

        var result = engine.ApplyBatch(new[]
        {
            ("../" + Path.GetFileName(outsideTarget), ReplacePatch("x", "y")),
        }, _root);

        Assert.Equal(0, result.Applied);
        Assert.True(result.Failed > 0 || result.Skipped > 0);
        Assert.Contains(result.Errors, e => e.Contains("sandbox", StringComparison.OrdinalIgnoreCase));
        Assert.False(File.Exists(outsideTarget));
        Assert.Equal("OLD", File.ReadAllText(Path.Combine(_root, "inside.txt"))); // untouched
    }

    [Fact]
    public void ApplyBatch_LegitRelativePath_StillApplies()
    {
        var engine = new PatchEngine();
        var result = engine.ApplyBatch(new[]
        {
            ("inside.txt", ReplacePatch("OLD", "NEW")),
        }, _root);

        Assert.Equal(1, result.Applied);
        Assert.Equal("NEW", File.ReadAllText(Path.Combine(_root, "inside.txt")));
    }
}
