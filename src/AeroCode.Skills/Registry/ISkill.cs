// Copyright (c) AeroCode V3.0
// ISkill contract — what every skill implementation must provide.
// Mirrors Hermes registry.register() but for C#.
namespace AeroCode.Skills.Registry;

/// <summary>
/// Contract for a self-contained reusable skill.
/// All skills (bundled, user-created, hub-installed) implement this interface.
/// Mirrors Hermes `tools/registry.py: registry.register(name, schema, handler, check_fn)`.
/// </summary>
public interface ISkill
{
    /// <summary>Unique id (e.g. "engineering/code-review").</summary>
    string Id { get; }

    /// <summary>Human-readable skill name.</summary>
    string Name { get; }

    /// <summary>Short description (&lt;= 60 chars, used in skill search UI).</summary>
    string Description { get; }

    /// <summary>Category: engineering | productivity | bundled | user.</summary>
    string Category { get; }

    /// <summary>Author (human first per Hermes hard rule).</summary>
    string Author { get; }

    /// <summary>Version string.</summary>
    string Version { get; }

    /// <summary>Tags for discovery.</summary>
    IReadOnlyList<string> Tags { get; }

    /// <summary>
    /// System prompt snippet to inject when this skill is loaded.
    /// Equivalent to Hermes `skill_view(name)` — full body returned.
    /// </summary>
    string GetSystemPrompt();

    /// <summary>
    /// Execute the skill with the given input. Returns a structured result.
    /// Mirrors Hermes tool handler signature: (args, **kwargs) -> str.
    /// </summary>
    /// <param name="input">Input payload (JSON-serializable dict).</param>
    /// <param name="ctx">Execution context (paths, llm, etc.).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Skill output (text + structured metadata).</returns>
    Task<SkillResult> ExecuteAsync(SkillInput input, SkillContext ctx, CancellationToken ct = default);

    /// <summary>
    /// Check whether this skill's dependencies are available.
    /// Mirrors Hermes `check_fn()` — returns False to hide skill from listing.
    /// </summary>
    bool IsAvailable();
}

/// <summary>Input to a skill (mirrors Hermes tool args).</summary>
public sealed class SkillInput
{
    /// <summary>Raw input dictionary (mirrors tool args).</summary>
    public IReadOnlyDictionary<string, object?> Args { get; init; } = new Dictionary<string, object?>();

    /// <summary>Optional user message that triggered this skill.</summary>
    public string UserMessage { get; init; } = string.Empty;
}

/// <summary>Execution context available to a skill.</summary>
public sealed class SkillContext
{
    /// <summary>Root directory of the project (Avernet-style sandbox root).</summary>
    public string WorkspaceRoot { get; init; } = string.Empty;

    /// <summary>Current user message (for diagnostics).</summary>
    public string UserMessage { get; init; } = string.Empty;

    /// <summary>Additional metadata (free-form dict).</summary>
    public IReadOnlyDictionary<string, object?> Metadata { get; init; } = new Dictionary<string, object?>();

    /// <summary>
    /// Optional LLM invoker — skills that need to reason (e.g. DeepAuditSkill) call this
    /// to get a model-generated analysis. Null = no LLM available, skill falls back to
    /// static / heuristic output.
    /// </summary>
    public LlmInvoker? LlmInvoker { get; init; }
}

/// <summary>
/// LLM invocation contract. Implementations should return a final string (no streaming);
/// the call should be cancellable. Null is a valid return for "no answer" — caller decides.
/// </summary>
public delegate Task<string> LlmInvoker(string prompt, IReadOnlyDictionary<string, object?>? options, CancellationToken ct);

/// <summary>Output of a skill (text + optional structured data).</summary>
public sealed class SkillResult
{
    /// <summary>Human-readable result text (always populated).</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>Optional structured data (JSON-serializable).</summary>
    public object? Data { get; init; }

    /// <summary>Whether the skill succeeded.</summary>
    public bool Success { get; init; } = true;

    /// <summary>Optional list of follow-up actions for the agent.</summary>
    public IReadOnlyList<string> NextActions { get; init; } = Array.Empty<string>();
}
