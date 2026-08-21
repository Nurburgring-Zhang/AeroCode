// Copyright (c) AeroCode V3.0
// Cluster models — expert execution contract, branch specification, fan-out contest
// types and scheduler options. The scheduler itself lives in ClusterScheduler.cs;
// persistence of experts in ExpertPool.cs; run traces in ClusterRunRecord.cs.
using System;
using System.Collections.Generic;

namespace AeroAgent.Autonomy.Cluster;

/// <summary>How an expert attempt participates in resolving a branch node.</summary>
public enum ExpertAttemptKind
{
    /// <summary>Primary attempt: the expert assigned to drive the branch.</summary>
    Primary = 0,

    /// <summary>Swarm attempt: an idle expert reinforcing a stuck node (会战).</summary>
    Swarm = 1,

    /// <summary>Fan-out attempt: one of N parallel candidates racing on the same node.</summary>
    FanOut = 2,
}

/// <summary>Terminal result of one expert attempt.</summary>
public enum ExpertAttemptResult
{
    /// <summary>The attempt produced an accepted output.</summary>
    Succeeded = 0,

    /// <summary>The attempt failed with an error.</summary>
    Failed = 1,

    /// <summary>The attempt exceeded the attempt timeout.</summary>
    TimedOut = 2,

    /// <summary>The attempt was cancelled (run-level cancellation).</summary>
    Cancelled = 3,
}

/// <summary>How a branch node reached its final output.</summary>
public enum ClusterResolution
{
    /// <summary>The primary expert attempt succeeded directly.</summary>
    Primary = 0,

    /// <summary>A multi-expert swarm on a stuck node produced the first success.</summary>
    Swarm = 1,

    /// <summary>A fan-out contest was judged and a winner selected.</summary>
    FanOut = 2,
}

/// <summary>
/// Execution context handed to an <see cref="IExpertExecutor"/> for one attempt.
/// The memory snapshot is loaded by the scheduler from the expert's persisted memory
/// (see <see cref="ExpertPool.BuildMemorySnapshot"/>) before each new task.
/// </summary>
/// <param name="ExpertId">Stable id of the expert executing the attempt.</param>
/// <param name="ExpertSessionId">Stable session id of the expert (one persistent context per expert).</param>
/// <param name="Role">Role description of the expert.</param>
/// <param name="NodeId">Branch node being executed.</param>
/// <param name="NodeName">Human-readable node name.</param>
/// <param name="TaskText">The task text the expert must produce a deliverable for.</param>
/// <param name="MemorySnapshot">Rendered persisted memory of the expert (may be empty).</param>
/// <param name="AttemptKind">Whether this is a primary, swarm or fan-out attempt.</param>
/// <param name="FanOutIndex">0-based candidate index for fan-out attempts; 0 otherwise.</param>
public sealed record ExpertExecutionContext(
    string ExpertId,
    string ExpertSessionId,
    string Role,
    string NodeId,
    string NodeName,
    string TaskText,
    string MemorySnapshot,
    ExpertAttemptKind AttemptKind,
    int FanOutIndex);

/// <summary>
/// Outcome of one expert attempt. Success and all failure modes are expressed in the
/// result — executors must not leak provider exceptions through the return path.
/// </summary>
/// <param name="Succeeded">True when the attempt produced an accepted deliverable.</param>
/// <param name="Cancelled">True when the attempt was cancelled before producing a result.</param>
/// <param name="TimedOut">True when the attempt exceeded its timeout.</param>
/// <param name="Output">Deliverable text (empty on failure).</param>
/// <param name="Error">Failure description (null on success).</param>
/// <param name="DurationMs">Wall-clock duration of the attempt in milliseconds.</param>
public sealed record ExpertExecutionOutcome(
    bool Succeeded,
    bool Cancelled,
    bool TimedOut,
    string Output,
    string? Error,
    double DurationMs);

/// <summary>
/// Expert execution abstraction (mirrors the Mission kernel's IMissionExecutor pattern).
/// Production implementations: <see cref="AgentExpertExecutor"/> (HarnessHost sub-agents)
/// and <see cref="FacadeExpertExecutor"/> (Conversation orchestration facade).
/// Tests inject scripted hand-written implementations of this interface.
/// </summary>
public interface IExpertExecutor
{
    /// <summary>
    /// Execute one attempt and return its outcome. Implementations must capture their
    /// own exceptions into a failed outcome; the scheduler still guards against throws.
    /// </summary>
    Task<ExpertExecutionOutcome> ExecuteAsync(ExpertExecutionContext context, CancellationToken ct);
}

/// <summary>One successful candidate in a fan-out contest.</summary>
/// <param name="ExpertId">The expert that produced the candidate.</param>
/// <param name="Output">The candidate deliverable text.</param>
public sealed record FanOutCandidate(string ExpertId, string Output);

/// <summary>Ballot presented to a fan-out judge: the node and all successful candidates.</summary>
/// <param name="NodeId">The contested branch node.</param>
/// <param name="NodeName">Human-readable node name.</param>
/// <param name="Candidates">Successful candidates (failed attempts are not on the ballot).</param>
public sealed record FanOutBallot(string NodeId, string NodeName, IReadOnlyList<FanOutCandidate> Candidates);

/// <summary>
/// Judge decision for a fan-out contest. <see cref="FinalOutput"/> may be the winner's
/// output verbatim or a judge-merged artifact; when empty, the winner's output is used.
/// </summary>
/// <param name="WinnerExpertId">Id of the winning candidate's expert (must be on the ballot).</param>
/// <param name="FinalOutput">Final output for the node (empty = use the winner's output).</param>
/// <param name="Reason">Why this candidate won (traceability).</param>
public sealed record FanOutDecision(string WinnerExpertId, string FinalOutput, string Reason);

/// <summary>
/// Orca-style fan-out judge: given the ballot of successful candidates, pick a winner.
/// Abstracted as a delegate so tests can inject a deterministic ruling and production
/// can plug in an LLM-backed or heuristic judge.
/// </summary>
public delegate Task<FanOutDecision> FanOutJudge(FanOutBallot ballot, CancellationToken ct);

/// <summary>
/// One branch of a cluster plan. A branch is a node in the dependency DAG; branches
/// whose dependencies all succeed are scheduled in parallel on the expert pool.
/// </summary>
public sealed class ClusterBranchSpec
{
    /// <summary>Unique branch/node id.</summary>
    public required string Id { get; init; }

    /// <summary>Human-readable name (falls back to <see cref="Id"/> when empty).</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>The task text the assigned expert(s) must produce a deliverable for.</summary>
    public required string TaskText { get; init; }

    /// <summary>Ids of branches that must succeed before this one starts.</summary>
    public IReadOnlyList<string> DependsOn { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Fan-out degree: 1 = single primary expert; N ≥ 2 = race N experts in parallel
    /// and let a judge pick the winner (requires a judge on the spec or in the options).
    /// </summary>
    public int FanOutCount { get; init; } = 1;

    /// <summary>Per-branch fan-out judge (overrides the scheduler-level default judge).</summary>
    public FanOutJudge? Judge { get; init; }
}

/// <summary>Options for a <see cref="ClusterScheduler"/>.</summary>
public sealed class ClusterSchedulerOptions
{
    /// <summary>
    /// Per-attempt timeout (primary, swarm and fan-out attempts alike). A still-running
    /// executor is abandoned after this window; the branch continues as timed out.
    /// Default 10 minutes.
    /// </summary>
    public TimeSpan AttemptTimeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Whether stuck nodes trigger an idle-expert swarm (default true).</summary>
    public bool EnableSwarm { get; init; } = true;

    /// <summary>Maximum number of idle experts drafted into one swarm (default 3).</summary>
    public int MaxSwarmExperts { get; init; } = 3;

    /// <summary>
    /// Grace window after a swarm's first success for the remaining attempts to settle
    /// before the run record is finalized (default 5 seconds).
    /// </summary>
    public TimeSpan SwarmSettleGrace { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How many recent memory entries are injected into each attempt context (default 10).</summary>
    public int MaxMemoryEntriesInjected { get; init; } = 10;

    /// <summary>
    /// Scheduler-level default fan-out judge, used by fan-out branches that do not
    /// carry their own judge. Null = fan-out branches must supply their own judge.
    /// </summary>
    public FanOutJudge? DefaultJudge { get; init; }

    /// <summary>
    /// Directory where ClusterRunRecord JSON traces are persisted. Null = the
    /// expert pool's cluster directory under a "runs" subfolder.
    /// </summary>
    public string? RunRecordDirectory { get; init; }
}
