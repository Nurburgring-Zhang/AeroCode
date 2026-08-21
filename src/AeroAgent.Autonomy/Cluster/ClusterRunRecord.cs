// Copyright (c) AeroCode V3.0
// ClusterRunRecord — full trace of one cluster run (per-branch status/timing/expert
// assignments, swarm records, fan-out contests) plus the JSON serialization settings
// used to persist it. The scheduler writes one JSON file per run; tests and later UI
// can reload the record with the same options.
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AeroAgent.Autonomy.Cluster;

/// <summary>Overall outcome of a cluster run.</summary>
public enum ClusterRunStatus
{
    /// <summary>Every branch succeeded.</summary>
    Succeeded = 0,

    /// <summary>At least one branch succeeded and at least one did not.</summary>
    PartiallySucceeded = 1,

    /// <summary>No branch succeeded (and none was cancelled).</summary>
    Failed = 2,

    /// <summary>The run was cancelled (at least one branch recorded cancellation).</summary>
    Cancelled = 3,
}

/// <summary>Lifecycle status of one branch node.</summary>
public enum ClusterBranchStatus
{
    /// <summary>Not started yet (waiting for dependencies or an expert).</summary>
    Pending = 0,

    /// <summary>An expert attempt is in flight.</summary>
    Running = 1,

    /// <summary>The branch produced an accepted output (primary, swarm or fan-out).</summary>
    Succeeded = 2,

    /// <summary>All attempts (including any swarm) failed or timed out.</summary>
    Failed = 3,

    /// <summary>An upstream dependency did not succeed, so the branch never ran.</summary>
    Skipped = 4,

    /// <summary>The run was cancelled before or during this branch.</summary>
    Cancelled = 5,
}

/// <summary>Outcome of a multi-expert swarm on a stuck node.</summary>
public enum SwarmOutcome
{
    /// <summary>The swarm is still running (never present in a persisted final record).</summary>
    Pending = 0,

    /// <summary>One swarm attempt succeeded first; the others were cancelled.</summary>
    FirstSuccess = 1,

    /// <summary>Every swarm attempt failed; their outputs were aggregated for the record.</summary>
    AggregatedFailure = 2,

    /// <summary>The node was stuck but no expert was idle, so no swarm could be mounted.</summary>
    NoIdleExperts = 3,

    /// <summary>The run was cancelled during the swarm.</summary>
    Cancelled = 4,
}

/// <summary>One expert's assignment to a branch attempt, with its outcome once finished.</summary>
public sealed class ExpertAssignmentRecord
{
    /// <summary>The assigned expert.</summary>
    public string ExpertId { get; set; } = string.Empty;

    /// <summary>Primary, swarm or fan-out attempt.</summary>
    public ExpertAttemptKind Kind { get; set; }

    /// <summary>When the attempt started.</summary>
    public DateTime StartedAtUtc { get; set; }

    /// <summary>When the attempt finished (null while running/abandoned).</summary>
    public DateTime? FinishedAtUtc { get; set; }

    /// <summary>Attempt wall-clock duration in milliseconds.</summary>
    public double DurationMs { get; set; }

    /// <summary>Terminal result of the attempt (null while running).</summary>
    public ExpertAttemptResult? Result { get; set; }

    /// <summary>Failure description (null on success).</summary>
    public string? Error { get; set; }

    /// <summary>Truncated output preview for traceability.</summary>
    public string? OutputPreview { get; set; }
}

/// <summary>Trace of one branch node in the run.</summary>
public sealed class ClusterBranchRecord
{
    /// <summary>Branch/node id.</summary>
    public string BranchId { get; set; } = string.Empty;

    /// <summary>Human-readable branch name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Final lifecycle status.</summary>
    public ClusterBranchStatus Status { get; set; } = ClusterBranchStatus.Pending;

    /// <summary>How the output was produced (null when the branch did not succeed).</summary>
    public ClusterResolution? ResolvedBy { get; set; }

    /// <summary>When execution started (null when skipped/cancelled before start).</summary>
    public DateTime? StartedAtUtc { get; set; }

    /// <summary>When the branch reached its terminal status.</summary>
    public DateTime? FinishedAtUtc { get; set; }

    /// <summary>Wall-clock duration from start to terminal status, in milliseconds.</summary>
    public double DurationMs { get; set; }

    /// <summary>Accepted deliverable text (null when not successful).</summary>
    public string? Output { get; set; }

    /// <summary>Failure/skip description (null on success).</summary>
    public string? Error { get; set; }

    /// <summary>All expert assignments (primary, swarm, fan-out) in dispatch order.</summary>
    public List<ExpertAssignmentRecord> Assignments { get; set; } = new();
}

/// <summary>Trace of one swarm (idle-expert assault) on a stuck node.</summary>
public sealed class SwarmRecord
{
    /// <summary>The stuck node that was assaulted.</summary>
    public string NodeId { get; set; } = string.Empty;

    /// <summary>Why the swarm was triggered (the primary attempt's failure description).</summary>
    public string Trigger { get; set; } = string.Empty;

    /// <summary>When the swarm started.</summary>
    public DateTime StartedAtUtc { get; set; }

    /// <summary>When the swarm concluded.</summary>
    public DateTime? FinishedAtUtc { get; set; }

    /// <summary>Ids of the experts drafted into the swarm.</summary>
    public List<string> Participants { get; set; } = new();

    /// <summary>How the swarm concluded.</summary>
    public SwarmOutcome Outcome { get; set; } = SwarmOutcome.Pending;

    /// <summary>The expert whose attempt succeeded first (null unless FirstSuccess).</summary>
    public string? WinningExpertId { get; set; }

    /// <summary>Aggregated attempt outputs (only for AggregatedFailure).</summary>
    public string? AggregatedOutput { get; set; }
}

/// <summary>One candidate row inside a <see cref="FanOutRecord"/>.</summary>
public sealed class FanOutCandidateRecord
{
    /// <summary>The expert that produced the candidate.</summary>
    public string ExpertId { get; set; } = string.Empty;

    /// <summary>0-based candidate index in the contest.</summary>
    public int FanOutIndex { get; set; }

    /// <summary>Whether the attempt succeeded (only successful candidates reach the judge).</summary>
    public bool Succeeded { get; set; }

    /// <summary>Truncated output preview.</summary>
    public string? OutputPreview { get; set; }

    /// <summary>Failure description (null on success).</summary>
    public string? Error { get; set; }
}

/// <summary>Trace of one Orca-style fan-out contest on a node.</summary>
public sealed class FanOutRecord
{
    /// <summary>The contested node.</summary>
    public string NodeId { get; set; } = string.Empty;

    /// <summary>How many candidates were requested.</summary>
    public int RequestedCandidates { get; set; }

    /// <summary>When the contest started.</summary>
    public DateTime StartedAtUtc { get; set; }

    /// <summary>When the contest concluded.</summary>
    public DateTime? FinishedAtUtc { get; set; }

    /// <summary>All attempts (successful and failed) with previews.</summary>
    public List<FanOutCandidateRecord> Candidates { get; set; } = new();

    /// <summary>The winning expert (null when no candidate succeeded).</summary>
    public string? WinnerExpertId { get; set; }

    /// <summary>The judge's stated reason (null when degraded to fallback).</summary>
    public string? JudgeReason { get; set; }

    /// <summary>True when the judge threw or picked an invalid winner and a fallback was used.</summary>
    public bool JudgeDegraded { get; set; }

    /// <summary>Truncated preview of the node's final output.</summary>
    public string? FinalOutputPreview { get; set; }
}

/// <summary>
/// Full trace of one cluster run: per-branch status/timing/expert assignments plus
/// every swarm and fan-out contest. Serialized to JSON and persisted by the scheduler.
/// </summary>
public sealed class ClusterRunRecord
{
    /// <summary>Unique run id.</summary>
    public string RunId { get; set; } = string.Empty;

    /// <summary>When the run started.</summary>
    public DateTime StartedAtUtc { get; set; }

    /// <summary>When the run finished (all branches terminal).</summary>
    public DateTime FinishedAtUtc { get; set; }

    /// <summary>Overall run status.</summary>
    public ClusterRunStatus Status { get; set; }

    /// <summary>Number of experts in the pool during the run.</summary>
    public int ExpertCount { get; set; }

    /// <summary>Maximum number of expert attempts observed executing simultaneously.</summary>
    public int MaxConcurrencyObserved { get; set; }

    /// <summary>Per-branch traces in plan order.</summary>
    public List<ClusterBranchRecord> Branches { get; set; } = new();

    /// <summary>All swarm records (one per stuck node that triggered a swarm attempt).</summary>
    public List<SwarmRecord> Swarms { get; set; } = new();

    /// <summary>All fan-out contest records.</summary>
    public List<FanOutRecord> FanOuts { get; set; } = new();

    /// <summary>Path of the persisted JSON trace (set before serialization).</summary>
    public string? PersistedPath { get; set; }
}

/// <summary>Shared JSON settings for all cluster persistence (records + enums as strings).</summary>
internal static class ClusterJson
{
    /// <summary>Indented JSON with enum-to-string conversion, used for every cluster artifact.</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };
}
