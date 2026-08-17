// Copyright (c) AeroCode V3.0
// SKILL.md parser — extracts YAML frontmatter + markdown body.
// Compatible with Hermes / Matt Pocock / Reasonix SKILL.md formats.
using AeroCode.Skills.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace AeroCode.Skills.Loader;

/// <summary>
/// Parses SKILL.md files into a <see cref="ParseResult"/> (frontmatter + body + source path).
/// Compatible with the three known SKILL.md dialects:
///   1. Hermes:  description, platforms, required_environment_variables
///   2. Matt Pocock: when_to_use, prerequisites
///   3. Reasonix: runAs, allowed-tools
/// </summary>
public static class SkillParser
{
    private const string FrontmatterDelimiter = "---";
    // Pure-alias deserialization: every field uses an explicit [YamlMember(Alias=...)].
    // We do NOT use a NamingConvention because SKILL.md frontmatter uses multiple dialects
    // (Hermes: snake_case, Matt Pocock: snake_case, Reasonix: camelCase, our default: kebab-case).
    // A single convention can't cover all of them, so each field is mapped explicitly.
    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>
    /// Parse a SKILL.md file's raw content.
    /// </summary>
    /// <param name="rawContent">Full SKILL.md file content.</param>
    /// <param name="sourcePath">Absolute path of the source file (for diagnostics).</param>
    /// <param name="category">Skill category (engineering/productivity/etc).</param>
    /// <returns>Parse result with skill or errors.</returns>
    public static ParseResult Parse(string rawContent, string sourcePath, string category = "user")
    {
        if (string.IsNullOrWhiteSpace(rawContent))
            return ParseResult.Failure("File content is empty", sourcePath);

        var parts = SplitFrontmatter(rawContent);
        if (parts is null)
            return ParseResult.Failure("Missing YAML frontmatter (expected --- at start)", sourcePath);

        SkillFrontmatter? fm;
        try
        {
            fm = Yaml.Deserialize<SkillFrontmatter>(parts.Value.Frontmatter);
        }
        catch (Exception ex)
        {
            return ParseResult.Failure($"YAML parse error: {ex.Message}", sourcePath);
        }

        if (fm is null)
            return ParseResult.Failure("Frontmatter deserialized to null", sourcePath);

        var validation = fm.Validate();
        if (!validation.IsValid)
            return ParseResult.Failure($"Frontmatter validation failed: {string.Join("; ", validation.Errors)}", sourcePath);

        var skill = new Skill
        {
            Id = DeriveId(sourcePath, fm),
            Name = fm.Name,
            Description = fm.Description,
            Version = fm.Version,
            Author = fm.Author,
            License = fm.License,
            Tags = fm.Tags,
            Body = parts.Value.Body.Trim(),
            SourcePath = sourcePath,
            Category = category,
            AutoCreated = false,
            LastModifiedUtc = File.GetLastWriteTimeUtc(sourcePath),
            Frontmatter = fm,
        };

        return ParseResult.Success(skill);
    }

    /// <summary>Read and parse a SKILL.md file from disk.</summary>
    public static ParseResult ParseFile(string filePath, string category = "user")
    {
        if (!File.Exists(filePath))
            return ParseResult.Failure($"File not found: {filePath}", filePath);

        string content;
        try
        {
            content = File.ReadAllText(filePath);
        }
        catch (Exception ex)
        {
            return ParseResult.Failure($"Read error: {ex.Message}", filePath);
        }

        return Parse(content, filePath, category);
    }

    private static (string Frontmatter, string Body)? SplitFrontmatter(string content)
    {
        // Normalize line endings (Hermes cross-platform rule).
        var normalized = content.Replace("\r\n", "\n").Replace("\r", "\n");
        var lines = normalized.Split('\n');

        if (lines.Length < 3 || lines[0].Trim() != FrontmatterDelimiter)
            return null;

        var endIdx = -1;
        for (var i = 1; i < lines.Length; i++)
        {
            if (lines[i].Trim() == FrontmatterDelimiter)
            {
                endIdx = i;
                break;
            }
        }

        if (endIdx < 0) return null;

        var fmLines = lines.Skip(1).Take(endIdx - 1);
        var bodyLines = lines.Skip(endIdx + 1);
        return (string.Join("\n", fmLines), string.Join("\n", bodyLines));
    }

    private static string DeriveId(string sourcePath, SkillFrontmatter fm)
    {
        // If the path is /skills/engineering/code-review/SKILL.md, the id is "engineering/code-review".
        var dir = Path.GetDirectoryName(sourcePath);
        if (string.IsNullOrWhiteSpace(dir)) return fm.Name;

        // Walk up to find a "skills" ancestor
        var segments = dir.Replace('\\', '/').Split('/').Reverse().ToList();
        for (var i = 0; i < segments.Count; i++)
        {
            if (string.Equals(segments[i], "skills", StringComparison.OrdinalIgnoreCase))
            {
                return string.Join("/", segments.Take(i).Reverse());
            }
        }
        // No "skills" ancestor — return everything except the basename to preserve the
        // hierarchical id (e.g. "test/will-fail" instead of just "will-fail").
        return string.Join("/", segments.AsEnumerable().Reverse().Skip(1));
    }
}

/// <summary>Result of a SKILL.md parse attempt.</summary>
public sealed class ParseResult
{
    public bool IsSuccess { get; }
    public Skill? Skill { get; }
    public string? Error { get; }
    public string SourcePath { get; }

    private ParseResult(bool ok, Skill? skill, string? error, string sourcePath)
    {
        IsSuccess = ok;
        Skill = skill;
        Error = error;
        SourcePath = sourcePath;
    }

    public static ParseResult Success(Skill skill) => new(true, skill, null, skill.SourcePath);
    public static ParseResult Failure(string error, string sourcePath) => new(false, null, error, sourcePath);
}
