// Copyright (c) AeroCode V3.0
// SkillPatcher — auto-patch a SKILL.md on failure (Hermes self-improvement loop).
using AeroCode.Skills.Loader;
using AeroCode.Skills.Models;
using AeroCode.Skills.Registry;

namespace AeroCode.Skills.AutoCreate;

/// <summary>
/// Self-patch a skill when it is invoked again and fails (Hermes pattern).
/// Scenario: a skill was created 2 weeks ago, then invoked again, and the
/// command/path/API has changed. SkillPatcher detects the failure and patches
/// the SKILL.md automatically — no user intervention required.
/// </summary>
public sealed class SkillPatcher
{
    private readonly SkillLoader _loader;
    private readonly SkillRegistry _registry;

    public SkillPatcher(SkillLoader loader, SkillRegistry registry)
    {
        _loader = loader;
        _registry = registry;
    }

    /// <summary>
    /// Record an invocation outcome and decide whether to patch.
    /// </summary>
    /// <param name="skillId">Skill that was invoked.</param>
    /// <param name="errorMessage">Failure message (null = success).</param>
    /// <returns>True if a patch was applied.</returns>
    public bool RecordFailureAndMaybePatch(string skillId, string? errorMessage)
    {
        var success = string.IsNullOrWhiteSpace(errorMessage);
        _registry.RecordInvocation(skillId, success);

        if (success) return false;

        // Try Loader cache first (skills loaded from disk), then Registry (auto-created).
        var skill = _loader.GetFull(skillId);
        if (skill is null)
        {
            // Auto-created skills are in the registry but not necessarily in the loader cache.
            // For them, we can't patch the SKILL.md on disk (it would be redundant), so we skip.
            return false;
        }

        var (_, currentRate) = _registry.GetStats(skillId);

        // Only patch if:
        //   1. at least 1 invocation has happened
        //   2. success rate dropped to 50% or below (i.e. >= 1 failure)
        //   3. failure message is not catastrophic (avoid masking real bugs)
        if (currentRate > 0.5) return false;
        if (IsCatastrophic(errorMessage)) return false;

        return TryPatch(skill, errorMessage!);
    }

    private static bool IsCatastrophic(string? msg)
    {
        if (string.IsNullOrWhiteSpace(msg)) return false;
        // Don't auto-patch security/catastrophic errors
        return msg.Contains("permission", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("denied", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("fatal", StringComparison.OrdinalIgnoreCase);
    }

    private bool TryPatch(Skill skill, string errorMessage)
    {
        // Simplest patch strategy: append the error to the body as a "Known Issues" note.
        // More sophisticated: parse the error, find the offending command, suggest a fix.
        var patchNote = $"""

            ## Known Issue (auto-patched {DateTime.UtcNow:yyyy-MM-dd})
            Last error: `{Truncate(errorMessage, 200)}`
            Suggestion: re-verify the procedure in this skill against the latest environment.
            """;

        var newBody = skill.Body + patchNote;

        // Re-serialize the SKILL.md.
        var newContent = Serialize(skill, newBody);
        try
        {
            File.WriteAllText(skill.SourcePath, newContent);
        }
        catch
        {
            return false;
        }
        return true;
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s.Substring(0, max - 3) + "...";

    /// <summary>
    /// Bump the minor segment of a semantic-ish version string (real increment, not a label).
    /// "1.2.3" → "1.3.0"; "1.2" → "1.3"; single-segment or non-numeric input
    /// conservatively gains a ".1" suffix (never loses information).
    /// </summary>
    public static string IncrementMinor(string version)
    {
        if (string.IsNullOrWhiteSpace(version)) return "0.1";
        var parts = version.Trim().Split('.');
        if (parts.Length >= 2 && int.TryParse(parts[0], out var major) && int.TryParse(parts[1], out var minor))
        {
            // Reset patch-level segments to 0 on a minor bump ("1.2.3" → "1.3.0").
            var tail = parts.Length > 2 ? "." + string.Join('.', Enumerable.Repeat("0", parts.Length - 2)) : string.Empty;
            return $"{major}.{minor + 1}{tail}";
        }
        return version.Trim() + ".1";
    }

    private static string Serialize(Skill skill, string newBody)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine($"name: {skill.Name}");
        sb.AppendLine($"description: {skill.Description}");
        sb.AppendLine($"version: {IncrementMinor(skill.Version)}");
        sb.AppendLine($"author: {skill.Author}");
        sb.AppendLine($"license: {skill.License}");
        if (skill.Tags.Count > 0)
            sb.AppendLine($"tags: [{string.Join(", ", skill.Tags)}]");
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine(newBody);
        return sb.ToString();
    }
}
