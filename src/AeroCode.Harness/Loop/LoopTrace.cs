// Copyright (c) AeroCode V3.0
// LoopTrace — full per-round audit trail of an EngineeringLoop run, serialized to JSON.
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AeroCode.Harness.Loop;

/// <summary>Trace of a single loop phase (Plan / Build / Verify / Review / Fix).</summary>
public sealed class PhaseTrace
{
    /// <summary>Phase name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>UTC start time.</summary>
    public DateTime StartedAtUtc { get; init; }

    /// <summary>Wall-clock duration in milliseconds.</summary>
    public long DurationMs { get; set; }

    /// <summary>Summary of the phase input (goal fragment, error text, contract name…).</summary>
    public string? InputSummary { get; set; }

    /// <summary>Summary of the phase output (plan steps, gate verdict, arena verdict…).</summary>
    public string? OutputSummary { get; set; }

    /// <summary>Phase-level verdict when applicable (e.g. Pass/Fail/Inconclusive, Accept/Reject/Revise).</summary>
    public string? Verdict { get; set; }

    /// <summary>Paths of evidence artifacts produced or consumed by the phase.</summary>
    public List<string> EvidencePaths { get; set; } = new();

    /// <summary>Free-form notes (iteration details, rollback events, degradation markers).</summary>
    public List<string> Notes { get; set; } = new();
}

/// <summary>Trace of one engineering-loop round: all five phases with inputs/outputs/timings.</summary>
public sealed class RoundTrace
{
    /// <summary>1-based round index.</summary>
    public int Round { get; init; }

    /// <summary>Plan phase trace.</summary>
    public PhaseTrace? Plan { get; set; }

    /// <summary>Build phase trace (includes LoopRunner iteration detail in Notes).</summary>
    public PhaseTrace? Build { get; set; }

    /// <summary>Verify (quality gate) phase trace.</summary>
    public PhaseTrace? Verify { get; set; }

    /// <summary>Review (dual-AI arena) phase trace.</summary>
    public PhaseTrace? Review { get; set; }

    /// <summary>Fix phase trace (null when no fix was attempted this round).</summary>
    public PhaseTrace? Fix { get; set; }
}

/// <summary>Complete trace of one EngineeringLoop run.</summary>
public sealed class LoopTrace
{
    /// <summary>Unique loop run id.</summary>
    public string LoopId { get; init; } = string.Empty;

    /// <summary>The goal the loop works towards.</summary>
    public string Goal { get; init; } = string.Empty;

    /// <summary>UTC start time.</summary>
    public DateTime StartedAtUtc { get; init; }

    /// <summary>UTC finish time (null while running).</summary>
    public DateTime? FinishedAtUtc { get; set; }

    /// <summary>Whether the loop terminated successfully (gate passed and review accepted).</summary>
    public bool Succeeded { get; set; }

    /// <summary>Termination reason (see <see cref="LoopTerminationReason"/>).</summary>
    public string? TerminationReason { get; set; }

    /// <summary>Final gate verdict summary, if the gate ran.</summary>
    public string? FinalGateVerdict { get; set; }

    /// <summary>Final review verdict summary, if the arena ran.</summary>
    public string? FinalReviewVerdict { get; set; }

    /// <summary>Per-round traces.</summary>
    public List<RoundTrace> Rounds { get; set; } = new();
}

/// <summary>Serializer for loop traces (indented JSON, enums as strings).</summary>
public static class LoopTraceSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Serialize the trace to JSON and write it to <paramref name="path"/> (directory auto-created).</summary>
    public static void Write(LoopTrace trace, string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(trace, Options));
    }

    /// <summary>Read a trace back from disk (used by tests and downstream tooling).</summary>
    public static LoopTrace? Read(string path) =>
        File.Exists(path) ? JsonSerializer.Deserialize<LoopTrace>(File.ReadAllText(path), Options) : null;
}
