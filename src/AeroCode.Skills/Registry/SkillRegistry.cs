// Copyright (c) AeroCode V3.0
// SkillRegistry — central registry mirroring Hermes `tools/registry.py: registry.register`.
using System.Collections.Concurrent;
using AeroCode.Skills.Models;

namespace AeroCode.Skills.Registry;

/// <summary>
/// Central skill registry. Skills self-register at construction time
/// (mirrors Hermes `tools/registry.py: registry.register` at module import).
/// Supports three-tier progressive loading (Hermes pattern).
/// </summary>
public sealed class SkillRegistry
{
    private readonly ConcurrentDictionary<string, ISkill> _skills = new();
    private readonly ConcurrentDictionary<string, int> _invocationCounts = new();
    private readonly ConcurrentDictionary<string, int> _successCounts = new();

    /// <summary>Register a skill. Thread-safe. Throws on duplicate id.</summary>
    public void Register(ISkill skill)
    {
        if (string.IsNullOrWhiteSpace(skill.Id))
            throw new ArgumentException("Skill id cannot be empty", nameof(skill));

        if (!_skills.TryAdd(skill.Id, skill))
            throw new InvalidOperationException($"Skill '{skill.Id}' is already registered");
    }

    /// <summary>Unregister a skill by id.</summary>
    public bool Unregister(string id) => _skills.TryRemove(id, out _);

    /// <summary>Get a skill by id, or null if not found.</summary>
    public ISkill? Get(string id) => _skills.TryGetValue(id, out var s) ? s : null;

    /// <summary>List all registered skills, optionally filtered by category/tag.</summary>
    public IReadOnlyList<ISkill> List(string? category = null, string? tag = null)
    {
        var all = _skills.Values.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(category))
            all = all.Where(s => string.Equals(s.Category, category, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(tag))
            all = all.Where(s => s.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase));
        return all.OrderBy(s => s.Id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Level 0: List of (name, description) pairs only — used in system prompt.
    /// Returns ~3K tokens for hundreds of skills (Hermes pattern).
    /// </summary>
    public IReadOnlyList<SkillListEntry> ListForPrompt(string? category = null, string? tag = null)
    {
        return List(category, tag)
            .Where(s => s.IsAvailable())
            .Select(s => new SkillListEntry(s.Id, s.Name, s.Description, s.Category))
            .ToList();
    }

    /// <summary>Number of registered skills.</summary>
    public int Count => _skills.Count;

    /// <summary>Clear all skills. Used in tests.</summary>
    public void Clear()
    {
        _skills.Clear();
        _invocationCounts.Clear();
        _successCounts.Clear();
    }

    /// <summary>
    /// Record an invocation outcome (Hermes learning loop pattern).
    /// Used to compute SuccessRate for skill ranking.
    /// </summary>
    public void RecordInvocation(string id, bool success)
    {
        _invocationCounts.AddOrUpdate(id, 1, (_, v) => v + 1);
        if (success) _successCounts.AddOrUpdate(id, 1, (_, v) => v + 1);
    }

    /// <summary>Get invocation stats for a skill. Returns (total, successRate).</summary>
    public (int invocations, double successRate) GetStats(string id)
    {
        var inv = _invocationCounts.GetValueOrDefault(id, 0);
        if (inv == 0) return (0, 1.0);
        var success = _successCounts.GetValueOrDefault(id, 0);
        return (inv, (double)success / inv);
    }
}

/// <summary>Lightweight skill list entry for system prompt injection.</summary>
public sealed record SkillListEntry(string Id, string Name, string Description, string Category);
