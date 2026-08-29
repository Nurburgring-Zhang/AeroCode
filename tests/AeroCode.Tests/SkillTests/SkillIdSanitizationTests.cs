// Copyright (c) AeroCode V3.0
// SanitizeId 安全闸门专项测试（Reviewer B P1 配套）：
// 路径穿越、Windows 保留设备名、Unicode 同形字符、超长段、超深层级、合法层级保留。
// 语义基线：SanitizeId 返回空串 = fail-closed 拒绝，TryCreate 必须不创建任何文件。
using AeroCode.Skills;
using AeroCode.Skills.AutoCreate;
using Xunit;

namespace AeroCode.Tests.SkillTests;

public sealed class SkillIdSanitizationTests : IDisposable
{
    private readonly string _tempRoot;

    public SkillIdSanitizationTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "aerocode-sanitize-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            try { Directory.Delete(_tempRoot, true); } catch { }
        }
    }

    // ============ 纯函数层：SanitizeId ============

    [Theory]
    [InlineData(@"..\..\evil", "evil")]
    [InlineData("../../evil", "evil")]
    [InlineData(@"..\..\..\x\..\y", "x/y")]
    [InlineData("a/./b", "a/b")]
    // 按段丢弃语义（非路径算术）：".." 段整体丢弃、不向上弹跳已保留段，
    // "a/../b" → "a/b"——这比路径解析更安全（不产生意外的层级归约）。
    [InlineData("a/../b", "a/b")]
    [InlineData("....//....//x", "x")] // 全点段清洗后为空 → 丢弃
    public void SanitizeId_TraversalAttempts_AreReducedInsideRoot(string raw, string expected)
    {
        Assert.Equal(expected, SkillCreator.SanitizeId(raw));
    }

    [Theory]
    [InlineData("test/auto-skill", "test/auto-skill")] // 合法层级 Id 原样保留
    [InlineData("User/My Skill", "user/my-skill")]     // 大小写 + 空格
    [InlineData("a/b/c/d", "a/b/c/d")]                  // 4 段 = 上限，允许
    public void SanitizeId_LegitimateIds_Preserved(string raw, string expected)
    {
        Assert.Equal(expected, SkillCreator.SanitizeId(raw));
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("con")]
    [InlineData("Nul")]
    [InlineData("PRN")]
    [InlineData("AUX")]
    [InlineData("com1")]
    [InlineData("COM9")]
    [InlineData("lpt9")]
    [InlineData("user/nul")]   // 设备名出现在任意层级都拒绝
    [InlineData("con/x")]
    public void SanitizeId_WindowsReservedDeviceNames_RejectEntireId(string raw)
    {
        Assert.Equal(string.Empty, SkillCreator.SanitizeId(raw));
    }

    [Fact]
    public void SanitizeId_DeviceNameWithExtension_SanitizedToSafeName()
    {
        // "con.txt" 在 Windows 上是保留名，但 '.' 会被映射为 '-'，
        // 清洗后 "con-txt" 不再是设备名 → 允许（与文档注释一致）。
        Assert.Equal("con-txt", SkillCreator.SanitizeId("con.txt"));
        Assert.Equal("nul-backup", SkillCreator.SanitizeId("nul.backup"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("---")]
    [InlineData("///")]
    [InlineData("тест")] // 全西里尔字符 → 段清洗后为空 → 整体为空
    public void SanitizeId_EmptyOrPunctuationOnly_ReturnsEmpty(string raw)
    {
        Assert.Equal(string.Empty, SkillCreator.SanitizeId(raw));
    }

    [Fact]
    public void SanitizeId_UnicodeHomoglyphs_NeverSurviveVerbatim()
    {
        // 西里尔 е (U+0435) 视觉同形于拉丁 e，但非 ASCII → 映射为 '-'，
        // 杜绝视觉欺骗性的 Id 混淆（"tеst" ≠ "test"）。
        var homoglyph = "t\u0435st"; // t + 西里尔 е + st
        var result = SkillCreator.SanitizeId(homoglyph);
        Assert.Equal("t-st", result);
        Assert.NotEqual("test", result);
    }

    [Fact]
    public void SanitizeId_OversizedSegment_DeterministicallyTruncatedTo64()
    {
        var longSegment = new string('a', 100);
        var result = SkillCreator.SanitizeId(longSegment);
        Assert.Equal(new string('a', SkillCreator.MaxSegmentLength), result);
        Assert.Equal(64, result.Length);
    }

    [Fact]
    public void SanitizeId_TruncationLandingOnHyphen_TrimsTrailingHyphen()
    {
        // 63 个 a + '-' + 40 个 b：第 64 位恰为 '-'，截断后去尾连字符 → 63 个 a。
        var segment = new string('a', 63) + "-" + new string('b', 40);
        var result = SkillCreator.SanitizeId(segment);
        Assert.Equal(new string('a', 63), result);
    }

    [Fact]
    public void SanitizeId_TooManySegments_RejectEntireId()
    {
        Assert.Equal("a/b/c/d", SkillCreator.SanitizeId("a/b/c/d"));       // 4 段允许
        Assert.Equal(string.Empty, SkillCreator.SanitizeId("a/b/c/d/e"));  // 5 段拒绝
        Assert.Equal(4, SkillCreator.MaxSegments);
    }

    // ============ 集成层：TryCreate 拒绝语义（不落盘、不注册） ============

    private static AutoCreateCandidate Candidate(string id) => new()
    {
        SuggestedId = id,
        SuggestedName = "Sanitization Probe",
        SuggestedDescription = "Probe for sanitize gate.",
        SuggestedBody = "# Probe\n",
        ToolCallCount = 10, // 高于阈值 5，确保门控差异只来自 Id 清洗
        Succeeded = true,
    };

    [Theory]
    [InlineData("user/NUL")]
    [InlineData("a/b/c/d/e")]
    [InlineData("///")]
    public void TryCreate_RejectedId_NoFileNoRegistration(string id)
    {
        var hub = new SkillHub(_tempRoot);
        var before = Directory.Exists(Path.Combine(_tempRoot, "skills"))
            ? Directory.GetFileSystemEntries(Path.Combine(_tempRoot, "skills"), "*", SearchOption.AllDirectories).Length
            : 0;

        var skill = hub.Creator.TryCreate(Candidate(id));

        Assert.Null(skill);
        var after = Directory.Exists(Path.Combine(_tempRoot, "skills"))
            ? Directory.GetFileSystemEntries(Path.Combine(_tempRoot, "skills"), "*", SearchOption.AllDirectories).Length
            : 0;
        Assert.Equal(before, after); // 一个字节都不落盘
    }

    [Fact]
    public void TryCreate_TraversalId_StaysInsideSkillsRoot()
    {
        var hub = new SkillHub(_tempRoot);

        var skill = hub.Creator.TryCreate(Candidate("../../evil"));

        Assert.NotNull(skill);
        Assert.Equal("evil", skill!.Id);
        var expected = Path.Combine(_tempRoot, "skills", "evil", "SKILL.md");
        Assert.True(File.Exists(expected), $"穿越 Id 应被归约到根目录内的 {expected}");
        Assert.NotNull(hub.Get("evil"));

        // 根目录之外不得出现任何产物
        var rootFull = Path.GetFullPath(Path.Combine(_tempRoot, "skills"));
        Assert.StartsWith(rootFull + Path.DirectorySeparatorChar, Path.GetFullPath(skill.SourcePath), StringComparison.OrdinalIgnoreCase);
    }
}
