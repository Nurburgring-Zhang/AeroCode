// Copyright (c) AeroCode V3.0
// HarnessHost — main entry point for the Agent harness.
using System.Text;
using AeroCode.AI.Models;
using AeroCode.AI.Providers;
using AeroCode.Harness.Agent;
using AeroCode.Harness.Compaction;
using AeroCode.Harness.EventBus;
using AeroCode.Harness.Gates;
using AeroCode.Harness.Loop;
using AeroCode.Harness.Patch;
using AeroCode.Harness.Permission;
using AeroCode.Harness.PlanMode;
using AeroCode.Harness.Planner;
using AeroCode.Harness.Plugin;
using AeroCode.Harness.Presets;
using AeroCode.Harness.Review;
using AeroCode.Skills;
using Microsoft.Extensions.Logging;

namespace AeroCode.Harness;

/// <summary>
/// Main entry point for the Harness — wires EventBus, Permission, PlanMode, Compactor,
/// Presets, PluginLoader and the sub-agent factory (<see cref="CreateAgent"/>) together,
/// and can produce a fully wired <see cref="EngineeringLoop"/> via
/// <see cref="CreateEngineeringLoop"/>.
/// </summary>
public sealed class HarnessHost : IDisposable
{
    private readonly ILogger? _logger;
    private readonly string? _pluginsDirectory;
    private readonly object _pluginLock = new();
    private PluginLoader? _plugins;

    /// <summary>Cross-module event bus.</summary>
    public EventBus.EventBus EventBus { get; }

    /// <summary>Tool permission policy.</summary>
    public PermissionPolicy Permission { get; }

    /// <summary>Plan-mode manager.</summary>
    public PlanModeManager PlanMode { get; }

    /// <summary>Context compactor.</summary>
    public Compactor Compactor { get; }

    /// <summary>Preset catalog.</summary>
    public PresetService Presets { get; }

    /// <param name="compactionStrategy">Compaction strategy for the compactor.</param>
    /// <param name="triggerThresholdPercent">Compaction trigger threshold percentage.</param>
    /// <param name="logger">Optional logger used for [DEGRADED] reporting.</param>
    /// <param name="pluginsDirectory">
    /// Optional whitelist directory for plugins. When null, the loader default
    /// (LocalApplicationData/AeroCode/plugins) is used.
    /// </param>
    public HarnessHost(
        CompactionStrategy compactionStrategy = CompactionStrategy.SlidingWindow,
        int triggerThresholdPercent = 50,
        ILogger? logger = null,
        string? pluginsDirectory = null)
    {
        EventBus = new EventBus.EventBus();
        Permission = PermissionPolicy.CreateDefault(EventBus);
        PlanMode = new PlanModeManager(EventBus);
        Compactor = new Compactor(EventBus, compactionStrategy, triggerThresholdPercent);
        Presets = new PresetService();
        _logger = logger;
        _pluginsDirectory = pluginsDirectory;
    }

    /// <summary>
    /// The plugin loader (created on first access). Only DLLs inside its whitelist
    /// directory are accepted; a failing plugin is isolated and logged as [DEGRADED].
    /// </summary>
    public PluginLoader Plugins
    {
        get
        {
            lock (_pluginLock)
            {
                return _plugins ??= new PluginLoader(_pluginsDirectory, _logger);
            }
        }
    }

    /// <summary>
    /// Eagerly load all whitelisted plugins from the plugins directory.
    /// Individual plugin failures are isolated (other plugins still load) and logged.
    /// </summary>
    /// <returns>The number of plugins successfully loaded.</returns>
    public async Task<int> LoadPluginsAsync(CancellationToken ct = default)
    {
        var loader = Plugins;
        var loaded = await loader.LoadAllAsync(ct);
        _logger?.LogInformation("HarnessHost loaded {Count} plugin(s) from '{Dir}'.", loaded, loader.PluginsDirectory);
        return loaded;
    }

    /// <summary>
    /// Create a sub-agent with its own independent context (own session and message
    /// history), an optional role and system prompt, and optional SkillHub injection
    /// (the skill catalog is rendered into the agent's system prompt).
    /// </summary>
    /// <param name="provider">The LLM provider the agent calls.</param>
    /// <param name="presetId">Optional preset id (default "standard").</param>
    /// <param name="sessionId">Optional session id; a fresh one is generated when null (independent context).</param>
    /// <param name="role">Optional role description, rendered into the system prompt.</param>
    /// <param name="systemPrompt">Optional additional system prompt text.</param>
    /// <param name="skills">Optional SkillHub whose catalog is injected into the system prompt.</param>
    public Agent.Agent CreateAgent(
        IAiProvider provider,
        string? presetId = null,
        string? sessionId = null,
        string? role = null,
        string? systemPrompt = null,
        SkillHub? skills = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var agent = new Agent.Agent(
            provider,
            Presets,
            Permission,
            PlanMode,
            Compactor,
            EventBus,
            presetId,
            sessionId ?? Guid.NewGuid().ToString("N"));

        var composed = ComposeSystemPrompt(role, systemPrompt, skills);
        if (composed is not null)
            agent.SetSystemPrompt(composed);

        _logger?.LogDebug("HarnessHost created sub-agent {Session} (role={Role}).", agent.SessionId, role ?? "(none)");
        return agent;
    }

    /// <summary>
    /// Create a fully wired <see cref="EngineeringLoop"/>:
    /// Planner (LLM-backed when a provider is given) + QualityGate + DualAiArena
    /// (roles run as sub-agents created by <see cref="CreateAgent"/>, or a deterministic
    /// [DEGRADED] fallback without a provider) + PatchEngine fix executor + budget.
    /// </summary>
    /// <param name="provider">LLM provider, or null for the deterministic degraded path.</param>
    /// <param name="options">Loop options (defaults when null).</param>
    public EngineeringLoop CreateEngineeringLoop(IAiProvider? provider, EngineeringLoopOptions? options = null)
    {
        options ??= new EngineeringLoopOptions();

        // Blockade hook (G7): real web research on failures, wired by default.
        // Options are init-only, so a copy carrying the default resolver is created.
        if (options.BlockadeResolver is null)
        {
            options = new EngineeringLoopOptions
            {
                MaxRounds = options.MaxRounds,
                MaxBuildAttemptsPerRound = options.MaxBuildAttemptsPerRound,
                MaxLlmCalls = options.MaxLlmCalls,
                MaxDuration = options.MaxDuration,
                TraceDirectory = options.TraceDirectory,
                WorkingDirectory = options.WorkingDirectory,
                EnableReview = options.EnableReview,
                BlockadeResolver = new Blockade.BlockadeResolver(
                    Skills.Research.SearchService.CreateDefault(_logger), _logger),
            };
        }

        var budget = LoopBudget.FromOptions(options);

        // Plan: LLM producer when a provider exists, deterministic single-step otherwise.
        Planner.Planner planner;
        if (provider is not null)
        {
            planner = new Planner.Planner(Planner.Planner.FromLlm(async (prompt, ct) =>
            {
                if (!budget.TryConsumeLlmCall())
                    throw new LoopBudgetExhaustedException($"LLM call budget exhausted ({budget.MaxLlmCalls}) during planning.");
                var response = await provider.ChatAsync(new ChatRequest
                {
                    Messages = new List<ChatMessage> { new() { Role = "user", Content = prompt } },
                }, ct);
                return response.Content ?? string.Empty;
            }), _logger);
        }
        else
        {
            planner = new Planner.Planner(producer: null, _logger);
            _logger?.LogWarning("[DEGRADED] EngineeringLoop planning is deterministic (no LLM provider supplied).");
        }

        // Review: sub-agent roles via CreateAgent when a provider exists, else deterministic rules.
        DualAiArena arena;
        var arenaOptions = new DualAiArenaOptions { TranscriptDirectory = options.TraceDirectory };
        if (provider is not null)
        {
            arena = new DualAiArena(
                new BudgetedArenaRoleInvoker(new AgentArenaRoleInvoker(this, provider), budget),
                arenaOptions,
                _logger);
        }
        else
        {
            arena = DualAiArena.CreateDeterministic(arenaOptions, _logger);
        }

        return new EngineeringLoop(
            planner,
            new QualityGate(logger: _logger),
            arena,
            new PatchEngine(),
            budget,
            options,
            _logger);
    }

    private static string? ComposeSystemPrompt(string? role, string? custom, SkillHub? skills)
    {
        if (role is null && custom is null && skills is null) return null;
        var sb = new StringBuilder();
        if (role is not null)
        {
            sb.AppendLine("[ROLE]");
            sb.AppendLine(role);
        }
        if (skills is not null)
        {
            sb.AppendLine("[AVAILABLE SKILLS]");
            sb.AppendLine(skills.BuildSystemPromptFragment());
        }
        if (custom is not null)
        {
            sb.AppendLine("[INSTRUCTIONS]");
            sb.AppendLine(custom);
        }
        return sb.ToString().Trim();
    }

    /// <summary>Dispose host resources (plugin loader, if materialized).</summary>
    public void Dispose()
    {
        lock (_pluginLock)
        {
            _plugins?.Dispose();
            _plugins = null;
        }
    }
}
