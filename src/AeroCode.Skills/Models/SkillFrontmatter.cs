// Copyright (c) AeroCode V3.0
// SKILL.md frontmatter model — compatible with Hermes + Matt Pocock + Reasonix.
using YamlDotNet.Serialization;

namespace AeroCode.Skills.Models;

/// <summary>
/// YAML frontmatter of a SKILL.md file.
/// Compatible with Hermes (description/platforms), Matt Pocock (when_to_use), and Reasonix (runAs).
/// </summary>
public sealed class SkillFrontmatter
{
    /// <summary>Skill name (kebab-case). Required, &lt;= 60 chars total in description.</summary>
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Short description shown in skill search results. Required.
    /// Hermes hard rule: &lt;= 60 chars, one sentence, ends with period, no marketing words.
    /// </summary>
    [YamlMember(Alias = "description")]
    public string Description { get; set; } = string.Empty;

    /// <summary>Skill version, e.g. "1.0.0".</summary>
    [YamlMember(Alias = "version")]
    public string Version { get; set; } = "1.0.0";

    /// <summary>Author credits human first (Hermes hard rule).</summary>
    [YamlMember(Alias = "author")]
    public string Author { get; set; } = string.Empty;

    /// <summary>License identifier, e.g. "MIT".</summary>
    [YamlMember(Alias = "license")]
    public string License { get; set; } = "MIT";

    /// <summary>OS platform gate. Empty/missing = all platforms (Hermes default).</summary>
    [YamlMember(Alias = "platforms")]
    public List<string> Platforms { get; set; } = new();

    /// <summary>Required env vars for secure setup-on-load (Hermes pattern).</summary>
    [YamlMember(Alias = "required_environment_variables")]
    public List<RequiredEnvVar> RequiredEnvironmentVariables { get; set; } = new();

    /// <summary>Matt Pocock pattern: when should the agent use this skill?</summary>
    [YamlMember(Alias = "when_to_use")]
    public string WhenToUse { get; set; } = string.Empty;

    /// <summary>Prerequisites (deps, env vars, setup steps).</summary>
    [YamlMember(Alias = "prerequisites")]
    public string Prerequisites { get; set; } = string.Empty;

    /// <summary>Reasonix pattern: inline | subagent.</summary>
    [YamlMember(Alias = "runAs")]
    public string RunAs { get; set; } = "inline";

    /// <summary>Reasonix pattern: tools this skill is allowed to call.</summary>
    [YamlMember(Alias = "allowed-tools")]
    public List<string> AllowedTools { get; set; } = new();

    /// <summary>Hermes metadata: tags for discovery.</summary>
    [YamlMember(Alias = "tags")]
    public List<string> Tags { get; set; } = new();

    /// <summary>Validates this frontmatter against hard rules (Hermes-inspired).</summary>
    public ValidationResult Validate()
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(Name))
            errors.Add("name is required");

        if (string.IsNullOrWhiteSpace(Description))
            errors.Add("description is required");
        else if (Description.Length > 60)
            errors.Add($"description must be <= 60 chars (Hermes hard rule), got {Description.Length}");

        if (string.IsNullOrWhiteSpace(Version))
            errors.Add("version is required");

        if (string.IsNullOrWhiteSpace(Author))
            errors.Add("author is required (credit human first, Hermes hard rule)");

        if (string.IsNullOrWhiteSpace(License))
            errors.Add("license is required");

        return errors.Count == 0
            ? ValidationResult.Ok
            : ValidationResult.Invalid(errors);
    }
}

/// <summary>Result of validation, with optional list of error messages.</summary>
public sealed class ValidationResult
{
    public bool IsValid { get; }
    public IReadOnlyList<string> Errors { get; }

    private ValidationResult(bool ok, IReadOnlyList<string> errors)
    {
        IsValid = ok;
        Errors = errors;
    }

    public static readonly ValidationResult Ok = new(true, Array.Empty<string>());

    public static ValidationResult Invalid(IReadOnlyList<string> errors) => new(false, errors);

    public override string ToString() => IsValid ? "OK" : $"Invalid: {string.Join("; ", Errors)}";
}

/// <summary>Required environment variable metadata (Hermes pattern).</summary>
public sealed class RequiredEnvVar
{
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = string.Empty;

    [YamlMember(Alias = "prompt")]
    public string Prompt { get; set; } = string.Empty;

    [YamlMember(Alias = "help")]
    public string Help { get; set; } = string.Empty;

    [YamlMember(Alias = "required_for")]
    public string RequiredFor { get; set; } = string.Empty;
}
