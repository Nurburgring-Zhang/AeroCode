// Copyright (c) AeroCode V3.0
// SkillLoader — three-tier progressive loading (Hermes pattern).
// Level 0: list of (name, description) — ~3K tokens
// Level 1: full body — only when matched
// Level 2: specific reference file
using AeroCode.Skills.Models;
using AeroCode.Skills.Registry;

namespace AeroCode.Skills.Loader;

/// <summary>
/// Three-tier progressive skill loader (Hermes pattern).
/// Minimizes token consumption by only loading full content when needed.
/// </summary>
public sealed class SkillLoader
{
    private readonly SkillRegistry _registry;
    private readonly Dictionary<string, Skill> _skillCache = new();
    private readonly object _cacheLock = new();

    public SkillLoader(SkillRegistry registry)
    {
        _registry = registry;
    }

    /// <summary>
    /// Load all SKILL.md files from a directory tree.
    /// Bundled skills (in repo) or user skills (in workspace) or hub skills.
    /// </summary>
    /// <param name="root">Root directory to scan recursively.</param>
    /// <param name="category">Category to assign to all loaded skills.</param>
    /// <returns>Number of skills loaded successfully.</returns>
    public int LoadFromDirectory(string root, string category = "user")
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return 0;

        var loaded = 0;
        foreach (var file in Directory.EnumerateFiles(root, "SKILL.md", SearchOption.AllDirectories))
        {
            var result = SkillParser.ParseFile(file, category);
            if (result.IsSuccess && result.Skill is not null)
            {
                lock (_cacheLock)
                {
                    _skillCache[result.Skill.Id] = result.Skill;
                }
                loaded++;
            }
        }
        return loaded;
    }

    /// <summary>
    /// Level 0: List of (id, name, description) for system prompt.
    /// Hermes pattern: ~3K tokens for hundreds of skills.
    /// </summary>
    public IReadOnlyList<SkillListEntry> ListForPrompt(string? category = null, string? tag = null)
        => _registry.ListForPrompt(category, tag);

    /// <summary>
    /// Level 1: Load full skill body. Use only after matching.
    /// </summary>
    public Skill? GetFull(string id)
    {
        lock (_cacheLock)
        {
            return _skillCache.TryGetValue(id, out var s) ? s : null;
        }
    }

    /// <summary>
    /// Level 2: Load specific reference file inside a skill directory.
    /// E.g. "engineering/code-review/references/style-guide.md"
    /// </summary>
    public string? GetReference(string id, string referenceRelativePath)
    {
        var skill = GetFull(id);
        if (skill is null) return null;

        var refPath = Path.Combine(
            Path.GetDirectoryName(skill.SourcePath) ?? string.Empty,
            referenceRelativePath);

        if (!File.Exists(refPath)) return null;

        try
        {
            return File.ReadAllText(refPath);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Number of skills in the cache.</summary>
    public int CachedSkillCount
    {
        get { lock (_cacheLock) { return _skillCache.Count; } }
    }

    /// <summary>Inject Level-0 list as system prompt fragment (Hermes pattern).</summary>
    public string BuildSystemPromptFragment(string? category = null)
    {
        var entries = ListForPrompt(category);
        if (entries.Count == 0) return string.Empty;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## Available Skills");
        sb.AppendLine();
        foreach (var e in entries)
        {
            sb.AppendLine($"- `{e.Id}` — {e.Description}");
        }
        return sb.ToString();
    }
}
