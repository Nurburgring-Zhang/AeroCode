// Copyright (c) AeroCode V3.0
// SkillCreator — auto-creates a SKILL.md after complex successful task (Hermes learning loop).
using AeroCode.Skills.Loader;
using AeroCode.Skills.Models;
using AeroCode.Skills.Registry;

namespace AeroCode.Skills.AutoCreate;

/// <summary>
/// Trigger conditions (Hermes hard rules):
///   1. Task complex (>= 5 tool calls) AND succeeded
///   2. Task had errors / dead ends AND a viable path was found
///   3. User corrected the agent
///   4. Non-obvious workflow discovered
/// </summary>
public sealed class SkillCreator
{
    private readonly SkillRegistry _registry;
    private readonly string _userSkillsRoot;
    private const int MinToolCallsForAutoCreate = 5;
    private const double MinSuccessRate = 0.6;

    public SkillCreator(SkillRegistry registry, string userSkillsRoot)
    {
        _registry = registry;
        _userSkillsRoot = userSkillsRoot;
    }

    /// <summary>
    /// Decide whether to auto-create a skill from a completed task.
    /// Returns null if no auto-create should happen.
    /// </summary>
    public Skill? TryCreate(AutoCreateCandidate candidate)
    {
        if (candidate.ToolCallCount < MinToolCallsForAutoCreate)
            return null;

        if (!candidate.Succeeded)
            return null;

        // If a similar skill already exists, do not create a duplicate.
        if (_registry.Get(candidate.SuggestedId) is not null)
            return null;

        var skill = new Skill
        {
            Id = candidate.SuggestedId,
            Name = candidate.SuggestedName,
            Description = TrimDescription(candidate.SuggestedDescription),
            Version = "0.1.0",
            Author = "AeroCode (auto-created)",
            License = "MIT",
            Tags = candidate.Tags,
            Body = candidate.SuggestedBody,
            // Write under <userSkillsRoot>/skills/<id>/SKILL.md so that DeriveId
            // (which looks for a "skills" ancestor) returns the right hierarchical id.
            SourcePath = Path.Combine(_userSkillsRoot, "skills", candidate.SuggestedId, "SKILL.md"),
            Category = "user",
            AutoCreated = true,
            LastModifiedUtc = DateTime.UtcNow,
        };

        var path = Path.GetDirectoryName(skill.SourcePath);
        if (!string.IsNullOrWhiteSpace(path) && !Directory.Exists(path))
            Directory.CreateDirectory(path);

        try
        {
            File.WriteAllText(skill.SourcePath, Serialize(skill));
        }
        catch
        {
            return null;
        }

        _registry.Register(new AutoCreatedSkillAdapter(skill));
        return skill;
    }

    private static string TrimDescription(string desc)
    {
        if (string.IsNullOrWhiteSpace(desc)) return string.Empty;
        desc = desc.Trim();
        if (!desc.EndsWith('.')) desc += ".";
        if (desc.Length > 60) desc = desc.Substring(0, 57) + "...";
        return desc;
    }

    private static string Serialize(Skill skill)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine($"name: {skill.Name}");
        sb.AppendLine($"description: {skill.Description}");
        sb.AppendLine($"version: {skill.Version}");
        sb.AppendLine($"author: {skill.Author}");
        sb.AppendLine($"license: {skill.License}");
        if (skill.Tags.Count > 0)
            sb.AppendLine($"tags: [{string.Join(", ", skill.Tags)}]");
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine(skill.Body);
        return sb.ToString();
    }
}

/// <summary>Input to SkillCreator.TryCreate().</summary>
public sealed class AutoCreateCandidate
{
    public string SuggestedId { get; init; } = string.Empty;
    public string SuggestedName { get; init; } = string.Empty;
    public string SuggestedDescription { get; init; } = string.Empty;
    public string SuggestedBody { get; init; } = string.Empty;
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();
    public int ToolCallCount { get; init; }
    public bool Succeeded { get; init; }
    public double SuccessRate { get; init; } = 1.0;
}

/// <summary>Adapter that exposes a Skill (parsed from disk) as an ISkill.</summary>
internal sealed class AutoCreatedSkillAdapter : ISkill
{
    private readonly Skill _skill;
    public AutoCreatedSkillAdapter(Skill skill) { _skill = skill; }
    public string Id => _skill.Id;
    public string Name => _skill.Name;
    public string Description => _skill.Description;
    public string Category => _skill.Category;
    public string Author => _skill.Author;
    public string Version => _skill.Version;
    public IReadOnlyList<string> Tags => _skill.Tags;
    public string GetSystemPrompt() => $"# {_skill.Name}\n\n{_skill.Body}";
    public bool IsAvailable() => true;

    public Task<SkillResult> ExecuteAsync(SkillInput input, SkillContext ctx, CancellationToken ct = default)
    {
        return Task.FromResult(new SkillResult
        {
            Text = $"Auto-created skill '{_skill.Id}' is declarative. Body:\n{_skill.Body}",
            Success = true,
        });
    }
}
