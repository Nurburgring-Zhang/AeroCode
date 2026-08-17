// Copyright (c) AeroCode V3.0
// Skill entity — represents one SKILL.md file in the registry.
namespace AeroCode.Skills.Models;

/// <summary>
/// A Skill is a self-contained reusable procedure, defined as a SKILL.md file.
/// Inspired by Hermes (NousResearch), Matt Pocock skills, and Reasonix.
/// </summary>
public sealed class Skill
{
    /// <summary>Unique skill id (e.g. "engineering/code-review").</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Skill name (from frontmatter).</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Short description (from frontmatter, &lt;= 60 chars).</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>Version string (e.g. "1.0.0").</summary>
    public string Version { get; init; } = "1.0.0";

    /// <summary>Author (human first per Hermes hard rule).</summary>
    public string Author { get; init; } = string.Empty;

    /// <summary>License.</summary>
    public string License { get; init; } = "MIT";

    /// <summary>Tags for discovery.</summary>
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    /// <summary>Full markdown body (after the frontmatter).</summary>
    public string Body { get; init; } = string.Empty;

    /// <summary>Absolute path to the SKILL.md file on disk.</summary>
    public string SourcePath { get; init; } = string.Empty;

    /// <summary>Skill category: "engineering" | "productivity" | "bundled" | "user" | "hub".</summary>
    public string Category { get; init; } = "user";

    /// <summary>Whether this skill was auto-created by the agent (Hermes learning loop).</summary>
    public bool AutoCreated { get; init; }

    /// <summary>UTC time this skill was last modified.</summary>
    public DateTime LastModifiedUtc { get; init; }

    /// <summary>
    /// Hermes skill_success_rate (0.0 - 1.0). Tracked across invocations.
    /// Used to improve skill ranking.
    /// </summary>
    public double SuccessRate { get; set; } = 1.0;

    /// <summary>Number of times this skill has been invoked.</summary>
    public int UsageCount { get; set; } = 0;

    /// <summary>Frontmatter metadata (loaded for advanced use).</summary>
    public SkillFrontmatter? Frontmatter { get; init; }

    /// <summary>Stable cache key for prefix-cache-friendly injection (Reasonix pattern).</summary>
    public string CacheKey => $"{Category}/{Id}@{Version}";
}
