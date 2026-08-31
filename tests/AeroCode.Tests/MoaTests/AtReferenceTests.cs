// Copyright (c) AeroCode
// AtReference @引用解析/扩展 + InstructionLoader 两级指令合并（真实临时文件）。
using System;
using System.IO;
using System.Linq;
using AeroAgent.Conversation.Orchestration;
using Xunit;

namespace AeroCode.Tests.MoaTests;

/// <summary>
/// Extract/Expand 纯函数钉子 + 真实文件注入：解析出的引用经真实 ReadAllText 回读进 file 块。
/// </summary>
public sealed class AtReferenceTests : IDisposable
{
    private readonly string _dir;

    public AtReferenceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"atref_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // 临时目录清理失败不影响测试结论
        }
    }

    private string? ReadRef(string relative)
    {
        var abs = Path.Combine(_dir, relative);
        return File.Exists(abs) ? File.ReadAllText(abs) : null;
    }

    // ---------- Extract ----------

    [Fact]
    public void Extract_BasicReference()
    {
        var refs = AtReference.Extract("请看 @src/main.cs 并总结");

        var r = Assert.Single(refs);
        Assert.Equal("src/main.cs", r);
    }

    [Fact]
    public void Extract_MultipleReferences_PreservesOrder_KeepsNonAdjacentRepeats()
    {
        var refs = AtReference.Extract("@a.txt then @b.txt then @a.txt again");

        Assert.Equal(new[] { "a.txt", "b.txt", "a.txt" }, refs);
    }

    [Fact]
    public void Extract_AdjacentDuplicates_Deduped()
    {
        var refs = AtReference.Extract("@a.txt @a.txt @b.txt");

        Assert.Equal(new[] { "a.txt", "b.txt" }, refs);
    }

    [Fact]
    public void Extract_TrailingPunctuation_NotPartOfPath()
    {
        var refs = AtReference.Extract("compare @notes.md, and @img.png); also @cfg.yml:");

        Assert.Equal(new[] { "notes.md", "img.png", "cfg.yml" }, refs);
    }

    [Theory]
    [InlineData("email me at a@b.c thanks")]
    [InlineData("lone @ marker")]
    [InlineData("@@double-at")]
    [InlineData("no references here")]
    [InlineData("")]
    public void Extract_NonReferences_ReturnsEmpty(string text)
    {
        Assert.Empty(AtReference.Extract(text));
    }

    // ---------- Expand ----------

    [Fact]
    public void Expand_NoReferences_ReturnsOriginalText()
    {
        const string text = "plain message without refs";

        Assert.Equal(text, AtReference.Expand(text, ReadRef));
    }

    [Fact]
    public void Expand_ExistingReference_AppendsRealFileBlock()
    {
        File.WriteAllText(Path.Combine(_dir, "hello.md"), "# 标题\n真实内容");
        const string text = "解释 @hello.md 谢谢";

        var expanded = AtReference.Expand(text, ReadRef);

        Assert.StartsWith(text, expanded);
        Assert.Contains("<file path=\"hello.md\">", expanded);
        Assert.Contains("# 标题\n真实内容", expanded);
        Assert.EndsWith("</file>", expanded.TrimEnd());
        // 未解析清单不得出现（全部解析成功）
        Assert.DoesNotContain("@references not found", expanded);
    }

    [Fact]
    public void Expand_MissingReference_ListedHonest()
    {
        var expanded = AtReference.Expand("read @ghost.txt now", ReadRef);

        Assert.Contains("[aerocode] @references not found: ghost.txt", expanded);
        Assert.DoesNotContain("<file path=", expanded);
    }

    [Fact]
    public void Expand_MixedResolvedAndUnresolved_BothPresent()
    {
        File.WriteAllText(Path.Combine(_dir, "real.txt"), "REAL");
        var expanded = AtReference.Expand("@real.txt and @missing.txt", ReadRef);

        Assert.Contains("<file path=\"real.txt\">", expanded);
        Assert.Contains("REAL", expanded);
        Assert.Contains("</file>", expanded);
        Assert.Contains("@references not found: missing.txt", expanded);
    }

    // ---------- InstructionLoader ----------

    [Fact]
    public void Loader_NoFiles_HasAnyFalse_LoadEmpty()
    {
        var loader = new InstructionLoader(_dir, workspaceRoot: null);

        Assert.False(loader.HasAny);
        Assert.Equal(string.Empty, loader.Load());
        Assert.Null(loader.EffectiveProjectFile());
    }

    [Fact]
    public void Loader_ProjectAgentsFile_PreferredOverClaudeFile()
    {
        File.WriteAllText(Path.Combine(_dir, "AGENTS.md"), "项目指令");
        var loader = new InstructionLoader(_dir, _dir);

        Assert.True(loader.HasAny);
        Assert.Equal(Path.Combine(_dir, "AGENTS.md"), loader.EffectiveProjectFile());
        Assert.Contains("<instructions source=\"project instructions (AGENTS.md)\">", loader.Load());
        Assert.Contains("项目指令", loader.Load());
    }

    [Fact]
    public void Loader_FallbackToClaudeFile_WhenNoAgentsFile()
    {
        File.WriteAllText(Path.Combine(_dir, "CLAUDE.md"), "claude 兼容指令");
        var loader = new InstructionLoader(_dir, _dir);

        Assert.True(loader.HasAny);
        Assert.Equal(Path.Combine(_dir, "CLAUDE.md"), loader.EffectiveProjectFile());
        Assert.Contains("project instructions (CLAUDE.md)", loader.Load());
    }

    [Fact]
    public void Loader_GlobalAndProject_MergedGlobalFirst()
    {
        var globalDir = Path.Combine(_dir, "global");
        var projDir = Path.Combine(_dir, "proj");
        Directory.CreateDirectory(globalDir);
        Directory.CreateDirectory(projDir);
        File.WriteAllText(Path.Combine(globalDir, "AGENTS.md"), "GLOBAL-RULE");
        File.WriteAllText(Path.Combine(projDir, "AGENTS.md"), "PROJECT-RULE");

        var loader = new InstructionLoader(globalDir, projDir);
        var merged = loader.Load();

        Assert.Contains("source=\"global instructions\"", merged);
        Assert.Contains("source=\"project instructions (AGENTS.md)\"", merged);
        Assert.True(merged.IndexOf("GLOBAL-RULE", StringComparison.Ordinal)
                    < merged.IndexOf("PROJECT-RULE", StringComparison.Ordinal),
            "全局段必须排在项目段之前");
    }

    [Fact]
    public void Loader_BlankFile_ExistsSoHasAnyTrue_ButNothingInjected()
    {
        // 空白指令文件：存在性判定为 true（文件真实存在），但 Load 不注入空段。
        File.WriteAllText(Path.Combine(_dir, "AGENTS.md"), "   \n  ");

        var loader = new InstructionLoader(_dir, _dir);

        Assert.True(loader.HasAny);
        Assert.Equal(string.Empty, loader.Load());
    }
}
