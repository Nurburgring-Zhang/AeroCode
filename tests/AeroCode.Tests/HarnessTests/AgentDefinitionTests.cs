// Copyright (c) AeroCode
// AgentDefinitionLoader 测试（批次 B G4，builder-γ）：真实 agents 目录（临时文件），
// 加载/校验/冲突拒绝全覆盖；YAML 解析复用 YamlDotNet 同款路径。
using System;
using System.IO;
using System.Linq;
using AeroCode.Harness.Agents;
using Xunit;

namespace AeroCode.Tests.HarnessTests;

public sealed class AgentDefinitionTests : IDisposable
{
    private readonly string _dir;
    private readonly System.Collections.Generic.List<string> _warnings;

    public AgentDefinitionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "aerocode-agents-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _warnings = new System.Collections.Generic.List<string>();
        Loader = new AgentDefinitionLoader(_warnings.Add);
    }

    private AgentDefinitionLoader Loader { get; }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort 清理 */ }
    }

    private void WriteAgent(string fileName, string frontmatter, string? body = "You are a helpful agent.")
        => File.WriteAllText(Path.Combine(_dir, fileName), $"---\n{frontmatter}\n---\n{body}\n");

    private const string ValidFm = """
        name: code-reviewer
        description: Reviews code changes for quality and safety
        model: deepseek-v4-flash
        tools:
          - read_file
          - run_shell
        maxTurns: 40
        """;

    [Fact]
    public void Load_ValidAgent_ParsesAllFields()
    {
        WriteAgent("code-reviewer.md", ValidFm);

        var result = Loader.LoadFromDirectory(_dir);

        Assert.Empty(result.Warnings);
        var agent = Assert.Single(result.Agents);
        Assert.Equal("code-reviewer", agent.Name);
        Assert.Equal("Reviews code changes for quality and safety", agent.Description);
        Assert.Equal("deepseek-v4-flash", agent.Model);
        Assert.Equal(new[] { "read_file", "run_shell" }, agent.Tools);
        Assert.Equal(40, agent.MaxTurns);
        Assert.Equal(Path.Combine(_dir, "code-reviewer.md"), agent.SourcePath);
    }

    [Fact]
    public void Load_ToolsFlowStyle_Parses()
    {
        WriteAgent("flow.md", """
            name: flowy
            description: Flow style tools list
            model: mini-max-m2
            tools: [read_file, write_file]
            maxTurns: 10
            """);

        var result = Loader.LoadFromDirectory(_dir);
        Assert.Empty(result.Warnings);
        var agent = Assert.Single(result.Agents);
        Assert.Equal(new[] { "read_file", "write_file" }, agent.Tools);
        Assert.Equal(10, agent.MaxTurns);
    }

    [Fact]
    public void Load_OptionalFieldsAbsent_ToolsEmpty_MaxTurnsDefault()
    {
        WriteAgent("minimal.md", """
            name: minimal
            description: Only required fields
            model: default-model
            """);

        var result = Loader.LoadFromDirectory(_dir);
        Assert.Empty(result.Warnings);
        var agent = Assert.Single(result.Agents);
        Assert.Empty(agent.Tools);
        Assert.Equal(AgentDefinitionLoader.DefaultMaxTurns, agent.MaxTurns);
    }

    [Fact]
    public void Load_MissingName_RejectedWithWarning()
    {
        WriteAgent("noname.md", """
            description: A nameless agent
            model: m
            """);

        var result = Loader.LoadFromDirectory(_dir);
        Assert.Empty(result.Agents);
        Assert.Contains(result.Warnings, w => w.Contains("noname.md") && w.Contains("'name'"));
        Assert.Single(_warnings);
    }

    [Fact]
    public void Load_MissingDescription_RejectedWithWarning()
    {
        WriteAgent("nodesc.md", """
            name: nodesc
            model: m
            """);

        var result = Loader.LoadFromDirectory(_dir);
        Assert.Empty(result.Agents);
        Assert.Contains(result.Warnings, w => w.Contains("'description'"));
    }

    [Fact]
    public void Load_MissingModel_RejectedWithWarning()
    {
        WriteAgent("nomodel.md", """
            name: nomodel
            description: No model specified
            """);

        var result = Loader.LoadFromDirectory(_dir);
        Assert.Empty(result.Agents);
        Assert.Contains(result.Warnings, w => w.Contains("'model'"));
    }

    [Fact]
    public void Load_InvalidMaxTurns_RejectedWithWarning()
    {
        WriteAgent("badturns.md", """
            name: badturns
            description: zero turns is meaningless
            model: m
            maxTurns: 0
            """);

        var result = Loader.LoadFromDirectory(_dir);
        Assert.Empty(result.Agents);
        Assert.Contains(result.Warnings, w => w.Contains("maxTurns"));
    }

    [Fact]
    public void Load_DuplicateName_SecondRejected_FirstKept()
    {
        WriteAgent("a-first.md", """
            name: dup-agent
            description: first definition wins
            model: model-a
            """);
        WriteAgent("b-second.md", """
            name: dup-agent
            description: duplicate rejected
            model: model-b
            """);

        var result = Loader.LoadFromDirectory(_dir);

        var agent = Assert.Single(result.Agents);
        Assert.Equal("model-a", agent.Model); // 文件名序先到先得
        Assert.Contains(result.Warnings, w => w.Contains("dup-agent") && w.Contains("b-second.md"));
    }

    [Fact]
    public void Load_InvalidYaml_RejectedWithWarning()
    {
        // 未闭合 flow 序列：YamlDotNet 真实解析错误
        WriteAgent("broken.yaml.md", """
            name: broken
            description: unclosed bracket
            model: m
            tools: [a, b
            """);

        var result = Loader.LoadFromDirectory(_dir);
        Assert.Empty(result.Agents);
        Assert.Contains(result.Warnings, w => w.Contains("invalid YAML") && w.Contains("broken.yaml.md"));
    }

    [Fact]
    public void Load_NoFrontmatter_RejectedWithWarning()
    {
        File.WriteAllText(Path.Combine(_dir, "plain.md"), "# Just a markdown note\nNo frontmatter here.\n");

        var result = Loader.LoadFromDirectory(_dir);
        Assert.Empty(result.Agents);
        Assert.Contains(result.Warnings, w => w.Contains("plain.md") && w.Contains("frontmatter"));
    }

    [Fact]
    public void Load_NonMdFiles_Ignored()
    {
        File.WriteAllText(Path.Combine(_dir, "agent.txt"), $"---\n{ValidFm}\n---\n");
        File.WriteAllText(Path.Combine(_dir, "notes.json"), "{}");

        var result = Loader.LoadFromDirectory(_dir);
        Assert.Empty(result.Agents);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Load_ValidAndBrokenMixed_BrokenSkipped_ValidLoaded()
    {
        WriteAgent("good.md", ValidFm);
        WriteAgent("bad.md", "name: only-name\n");

        var result = Loader.LoadFromDirectory(_dir);

        // 单文件失败 fail-safe：只拒坏文件，好文件照常加载
        Assert.Single(result.Agents);
        Assert.Equal("code-reviewer", result.Agents[0].Name);
        Assert.Single(result.Warnings);
    }

    [Fact]
    public void Load_MissingDirectory_ThrowsDirectoryNotFoundException()
    {
        Assert.Throws<DirectoryNotFoundException>(() => Loader.LoadFromDirectory(Path.Combine(_dir, "no-such-dir")));
    }

    [Fact]
    public void Load_WarningsAlsoRoutedToCallback()
    {
        WriteAgent("bad.md", "name: only-name\n");
        Loader.LoadFromDirectory(_dir);
        Assert.NotEmpty(_warnings);
        Assert.All(_warnings, w => Assert.Contains("bad.md", w));
    }
}
