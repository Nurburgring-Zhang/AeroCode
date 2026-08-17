// Copyright (c) AeroCode V3.0
// PatchEngine tests (OpenCode + Google Small CLs).
using AeroCode.Harness.Patch;
using Xunit;

namespace AeroCode.Tests.HarnessTests;

public class PatchEngineTests
{
    [Fact]
    public void Replace_ExactMatch_Succeeds()
    {
        var engine = new PatchEngine();
        var content = "hello world";
        var patch = new Patch
        {
            FilePath = "test.cs",
            Kind = PatchKind.Replace,
            OldText = "world",
            NewText = "universe",
            Fuzzy = false,
        };
        var (ok, newContent, _) = engine.Apply(content, patch);
        Assert.True(ok);
        Assert.Equal("hello universe", newContent);
    }

    [Fact]
    public void Replace_NoMatch_Fails()
    {
        var engine = new PatchEngine();
        var content = "hello world";
        var patch = new Patch
        {
            FilePath = "test.cs",
            Kind = PatchKind.Replace,
            OldText = "foo",
            NewText = "bar",
            Fuzzy = false,
        };
        var (ok, _, error) = engine.Apply(content, patch);
        Assert.False(ok);
        Assert.NotNull(error);
    }

    [Fact]
    public void Replace_FuzzyMatch_HandlesWhitespace()
    {
        var engine = new PatchEngine();
        var content = "hello    world";
        var patch = new Patch
        {
            FilePath = "test.cs",
            Kind = PatchKind.Replace,
            OldText = "hello world",
            NewText = "hello universe",
            Fuzzy = true,
        };
        var (ok, newContent, _) = engine.Apply(content, patch);
        Assert.True(ok);
        Assert.NotNull(newContent);
    }

    [Fact]
    public void Insert_AtLineNumber_AddsLine()
    {
        var engine = new PatchEngine();
        var content = "line0\nline1\nline2";
        var patch = new Patch
        {
            FilePath = "test.cs",
            Kind = PatchKind.Insert,
            LineNumber = 1,
            NewText = "inserted",
        };
        var (ok, newContent, _) = engine.Apply(content, patch);
        Assert.True(ok);
        Assert.Contains("inserted", newContent);
    }

    [Fact]
    public void Delete_RemovesLine()
    {
        var engine = new PatchEngine();
        var content = "line0\nline1\nline2";
        var patch = new Patch
        {
            FilePath = "test.cs",
            Kind = PatchKind.Delete,
            LineNumber = 1,
        };
        var (ok, newContent, _) = engine.Apply(content, patch);
        Assert.True(ok);
        Assert.DoesNotContain("line1", newContent);
    }

    [Fact]
    public void ValidateSize_SmallFile_Ok()
    {
        var (ok, reason) = PatchEngine.ValidateSize("test.cs", 100, 1);
        Assert.True(ok);
        Assert.Null(reason);
    }

    [Fact]
    public void ValidateSize_LargeFile_Rejected()
    {
        var (ok, reason) = PatchEngine.ValidateSize("test.cs", 300, 1);
        Assert.False(ok);
        Assert.Contains("too large", reason);
    }

    [Fact]
    public void ValidateSize_TooManyFiles_Rejected()
    {
        var (ok, reason) = PatchEngine.ValidateSize("batch", 100, 20);
        Assert.False(ok);
        Assert.Contains("Too many files", reason);
    }
}
