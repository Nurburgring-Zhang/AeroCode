// Copyright (c) AeroCode V3.0
// SkillParser unit tests.
using AeroCode.Skills.Loader;
using AeroCode.Skills.Models;
using Xunit;

namespace AeroCode.Tests.SkillTests;

[Collection("RealLLM")]
public class SkillParserTests
{
    [Fact]
    public void Parse_ValidHermesStyleSkill_ReturnsSkill()
    {
        var raw = """
            ---
            name: hello-world
            description: A simple hello world skill.
            version: 1.0.0
            author: Tester
            license: MIT
            tags: [test, sample]
            ---

            # Hello World
            This is the body.
            """;

        var result = SkillParser.Parse(raw, @"C:\skills\hello-world\SKILL.md", "user");

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Skill);
        Assert.Equal("hello-world", result.Skill!.Name);
        Assert.Equal("A simple hello world skill.", result.Skill.Description);
        Assert.Contains("Hello World", result.Skill.Body);
    }

    [Fact]
    public void Parse_MissingFrontmatter_Fails()
    {
        var raw = "# Just a heading\nNo frontmatter here.";
        var result = SkillParser.Parse(raw, "test.md", "user");
        Assert.False(result.IsSuccess);
        Assert.Contains("frontmatter", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_DescriptionExceeds60Chars_Fails()
    {
        var raw = """
            ---
            name: bad-desc
            description: This description is way too long for a skill and should fail validation.
            version: 1.0.0
            author: Tester
            license: MIT
            ---

            # Body
            """;
        var result = SkillParser.Parse(raw, "test.md", "user");
        Assert.False(result.IsSuccess);
        Assert.Contains("60", result.Error);
    }

    [Fact]
    public void Parse_MattPocockStyle_Recognized()
    {
        var raw = """
            ---
            name: code-review
            description: Review code against 8 dimensions.
            version: 1.0.0
            author: Matt Pocock
            license: MIT
            when_to_use: After writing or modifying code.
            prerequisites: None.
            ---

            # Code Review
            """;
        var result = SkillParser.Parse(raw, "test.md", "user");
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Skill!.Frontmatter);
        Assert.Contains("modifying code", result.Skill.Frontmatter!.WhenToUse);
    }

    [Fact]
    public void Parse_ReasonixStyle_Recognized()
    {
        var raw = """
            ---
            name: my-skill
            description: A Reasonix-style skill.
            version: 1.0.0
            author: Tester
            license: MIT
            runAs: subagent
            allowed-tools: [read_file, write_file]
            ---

            # Body
            """;
        var result = SkillParser.Parse(raw, "test.md", "user");
        Assert.True(result.IsSuccess);
        Assert.Equal("subagent", result.Skill!.Frontmatter!.RunAs);
        Assert.Contains("write_file", result.Skill.Frontmatter.AllowedTools);
    }

    [Fact]
    public void Parse_PlatformGate_Preserved()
    {
        var raw = """
            ---
            name: macos-only
            description: macOS-only skill.
            version: 1.0.0
            author: Tester
            license: MIT
            platforms: [macos]
            ---

            # Body
            """;
        var result = SkillParser.Parse(raw, "test.md", "user");
        Assert.True(result.IsSuccess);
        Assert.Contains("macos", result.Skill!.Frontmatter!.Platforms);
    }

    [Fact]
    public void Parse_CrlfLineEndings_Handled()
    {
        var raw = "---\r\nname: crlf-test\r\ndescription: Tests CRLF.\r\nversion: 1.0.0\r\nauthor: T\r\nlicense: MIT\r\n---\r\n\r\n# Body\r\n";
        var result = SkillParser.Parse(raw, "test.md", "user");
        Assert.True(result.IsSuccess);
        Assert.Equal("crlf-test", result.Skill!.Name);
    }
}
