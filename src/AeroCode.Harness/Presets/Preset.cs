// Copyright (c) AeroCode V3.0
// Agent Preset system — DSH-style 4 modes (Standard / PTC / Minimal / Creative).
namespace AeroCode.Harness.Presets;

/// <summary>
/// An Agent Preset is a named configuration: system prompt, tool list, model routing, safety policy.
/// Inspired by DSH `--profile` and Avernet multi-bot profiles.
/// </summary>
public sealed class Preset
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string SystemPrompt { get; init; }
    public required IReadOnlyList<string> Tools { get; init; }
    public required string ModelRoutingStrategy { get; init; }  // "v4-flash" | "v4-pro" | "auto"
    public required string SafetyPolicy { get; init; }  // "strict" | "permissive" | "ask-for-dangerous"
    public string? Notes { get; init; }
}

/// <summary>
/// Built-in default presets matching DSH's 4 modes (Standard / PTC / Minimal / Creative).
/// </summary>
public static class BuiltInPresets
{
    public static readonly Preset Standard = new()
    {
        Id = "standard",
        Name = "Standard",
        Description = "General-purpose: full tools, auto model.",
        SystemPrompt = "You are a helpful, careful AI assistant. Use the available tools when needed. Ask before destructive operations.",
        Tools = new[] { "read_file", "write_file", "edit_file", "list_directory", "search_files", "grep_search", "web_search", "web_extract", "semantic_search", "summarize", "auto_tag" },
        ModelRoutingStrategy = "auto",
        SafetyPolicy = "ask-for-dangerous",
        Notes = "Default preset for most tasks.",
    };

    public static readonly Preset Ptc = new()
    {
        Id = "ptc",
        Name = "PTC (Programmatic Tool Calling)",
        Description = "Code generation: model writes code that orchestrates multi-round tool calls.",
        SystemPrompt = "You are an expert software engineer. Generate high-quality code with proper types, tests, and documentation. Use PTC: write code that calls multiple tools in sequence.",
        Tools = new[] { "read_file", "write_file", "edit_file", "list_directory", "search_files", "grep_search", "run_shell", "git_diff", "git_status", "code_review", "tdd", "grill_with_docs" },
        ModelRoutingStrategy = "v4-pro",
        SafetyPolicy = "strict",
        Notes = "DeepSeek V4-Pro for code generation. Strict safety.",
    };

    public static readonly Preset Minimal = new()
    {
        Id = "minimal",
        Name = "Minimal",
        Description = "Minimal: only shell + file edit. For benchmarks and constrained envs.",
        SystemPrompt = "You are a minimal agent. Only shell and file edit tools are available.",
        Tools = new[] { "run_shell", "read_file", "write_file" },
        ModelRoutingStrategy = "v4-flash",
        SafetyPolicy = "permissive",
        Notes = "Used for model benchmark tests.",
    };

    public static readonly Preset Creative = new()
    {
        Id = "creative",
        Name = "Creative",
        Description = "Exploratory: full tools + internal state inspection. For developers.",
        SystemPrompt = "You are a creative explorer. You can inspect internal state, run any tool, and propose novel solutions. Show your work.",
        Tools = new[] { "read_file", "write_file", "edit_file", "list_directory", "search_files", "grep_search", "web_search", "web_extract", "run_shell", "code_review", "diagnose_bugs", "grill_with_docs", "memory_inspect", "skill_inspect" },
        ModelRoutingStrategy = "auto",
        SafetyPolicy = "permissive",
        Notes = "Developer mode. Can inspect memory and skills.",
    };

    public static IReadOnlyList<Preset> All => new[] { Standard, Ptc, Minimal, Creative };
}

/// <summary>
/// Preset service — load, save, switch, delete.
/// </summary>
public sealed class PresetService
{
    private readonly Dictionary<string, Preset> _presets = new();

    public PresetService()
    {
        foreach (var p in BuiltInPresets.All)
            _presets[p.Id] = p;
    }

    public IReadOnlyList<Preset> List() => _presets.Values.OrderBy(p => p.Id).ToList();

    public Preset? Get(string id) => _presets.TryGetValue(id, out var p) ? p : null;

    public void Register(Preset preset)
    {
        if (string.IsNullOrWhiteSpace(preset.Id))
            throw new ArgumentException("Preset id is required");
        _presets[preset.Id] = preset;
    }

    public bool Unregister(string id) => _presets.Remove(id);
}
