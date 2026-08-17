// Copyright (c) AeroCode V3.0
// HarnessHost â€?main entry point for the Agent harness.
using AeroCode.AI.Providers;
using AeroCode.Harness.Agent;
using AeroCode.Harness.Compaction;
using AeroCode.Harness.EventBus;
using AeroCode.Harness.Permission;
using AeroCode.Harness.PlanMode;
using AeroCode.Harness.Presets;

namespace AeroCode.Harness;

/// <summary>
/// Main entry point for the Harness â€?wires EventBus, Permission, PlanMode, Compactor, Presets, and Agent together.
/// </summary>
public sealed class HarnessHost : IDisposable
{
    public EventBus.EventBus EventBus { get; }
    public PermissionPolicy Permission { get; }
    public PlanModeManager PlanMode { get; }
    public Compactor Compactor { get; }
    public PresetService Presets { get; }

    public HarnessHost(CompactionStrategy compactionStrategy = CompactionStrategy.SlidingWindow, int triggerThresholdPercent = 50)
    {
        EventBus = new EventBus.EventBus();
        Permission = PermissionPolicy.CreateDefault(EventBus);
        PlanMode = new PlanModeManager(EventBus);
        Compactor = new Compactor(EventBus, compactionStrategy, triggerThresholdPercent);
        Presets = new PresetService();
    }

    /// <summary>Create an Agent with the given LLM provider and optional preset.</summary>
    public Agent.Agent CreateAgent(AeroCode.AI.Providers.IAiProvider provider, string? presetId = null, string? sessionId = null)
    {
        return new Agent.Agent(provider, Presets, Permission, PlanMode, Compactor, EventBus, presetId, sessionId);
    }

    public void Dispose() { /* EventBus has no managed resources */ }
}
