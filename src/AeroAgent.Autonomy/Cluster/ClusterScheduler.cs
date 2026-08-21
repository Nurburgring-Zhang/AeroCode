// Copyright (c) AeroCode V3.0
// ClusterScheduler — asynchronous parallel scheduling of a branch DAG over a persistent
// expert pool.
//
// Guarantees (all enforced in code, all covered by tests):
//   * Real concurrency: every ready branch runs as its own async worker; experts are
//     leased from a shared pool, so N experts truly execute in parallel.
//   * Branch-level isolation: each attempt is bounded by AttemptTimeout. A stuck or
//     failed branch never blocks the others — its worker is abandoned (orphan observed)
//     and the branch is marked timed out/failed while the rest continue.
//   * Idle-expert swarm (会战): when a branch gets stuck, the experts that are idle at
//     that moment are drafted into a swarm on the stuck node; the first successful
//     attempt wins (the rest are cancelled), otherwise all outputs are aggregated.
//   * Orca-style fan-out race: a branch may dispatch N experts in parallel producing
//     candidates; a FanOutJudge delegate picks (or merges) the winner. Judge failures
//     degrade honestly to the first successful candidate with a [DEGRADED] log line.
//   * Full trace: every run produces a ClusterRunRecord persisted as JSON on disk.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace AeroAgent.Autonomy.Cluster;

/// <summary>
/// Schedules a <see cref="ClusterPlan"/> over an <see cref="ExpertPool"/> using an
/// <see cref="IExpertExecutor"/>. See the file header for the scheduling guarantees.
/// One scheduler instance can run plans sequentially; each run owns its own state.
/// </summary>
public sealed class ClusterScheduler
{
    private readonly ExpertPool _pool;
    private readonly IExpertExecutor _executor;
    private readonly ClusterSchedulerOptions _options;
    private readonly ILogger? _logger;

    /// <summary>Create a scheduler over the given pool and executor.</summary>
    /// <exception cref="ArgumentException">An option value is out of range.</exception>
    public ClusterScheduler(
        ExpertPool pool,
        IExpertExecutor executor,
        ClusterSchedulerOptions? options = null,
        ILogger? logger = null)
    {
        _pool = pool ?? throw new ArgumentNullException(nameof(pool));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _options = options ?? new ClusterSchedulerOptions();
        _logger = logger;

        if (_options.AttemptTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentException("AttemptTimeout must be positive.", nameof(options));
        }

        if (_options.MaxSwarmExperts < 1)
        {
            throw new ArgumentException("MaxSwarmExperts must be >= 1.", nameof(options));
        }

        if (_options.SwarmSettleGrace < TimeSpan.Zero)
        {
            throw new ArgumentException("SwarmSettleGrace must not be negative.", nameof(options));
        }

        if (_options.MaxMemoryEntriesInjected < 0)
        {
            throw new ArgumentException("MaxMemoryEntriesInjected must be >= 0.", nameof(options));
        }
    }

    /// <summary>
    /// Run the plan to completion (every branch terminal) and return the persisted run
    /// record. Stuck branches are isolated; the call itself only ends early on a
    /// scheduler-internal fault, never on a single branch failure.
    /// </summary>
    /// <exception cref="ArgumentException">The plan is empty.</exception>
    /// <exception cref="InvalidOperationException">
    /// The expert pool is empty, a fan-out branch has no judge, or a fan-out branch
    /// requests more candidates than there are experts (would deadlock the lease).
    /// </exception>
    public async Task<ClusterRunRecord> RunAsync(ClusterPlan plan, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Branches.Count == 0)
        {
            throw new ArgumentException("Cluster plan contains no branches.", nameof(plan));
        }

        var experts = _pool.ListExperts();
        if (experts.Count == 0)
        {
            throw new InvalidOperationException(
                "Expert pool is empty: register at least one expert before running the cluster scheduler.");
        }

        foreach (var branch in plan.Branches)
        {
            if (branch.FanOutCount >= 2 && (branch.Judge ?? _options.DefaultJudge) is null)
            {
                throw new InvalidOperationException(
                    $"Branch '{branch.Id}' requests fan-out x{branch.FanOutCount} but no judge is configured " +
                    "(set ClusterBranchSpec.Judge or ClusterSchedulerOptions.DefaultJudge).");
            }

            if (branch.FanOutCount > experts.Count)
            {
                throw new InvalidOperationException(
                    $"Branch '{branch.Id}' requests fan-out x{branch.FanOutCount} but the pool only has " +
                    $"{experts.Count} expert(s); the candidates would deadlock waiting for each other.");
            }
        }

        var run = new ClusterRunRecord
        {
            RunId = Guid.NewGuid().ToString("N"),
            StartedAtUtc = DateTime.UtcNow,
            ExpertCount = experts.Count,
        };

        var nodes = plan.Branches.ToDictionary(b => b.Id, b => new NodeRuntime(b), StringComparer.Ordinal);

        run.Branches = plan.Branches.Select(b => nodes[b.Id].Record).ToList();

        var state = new RunState
        {
            Run = run,
            Lease = new ExpertLease(experts.Select(e => e.Id)),
            Nodes = nodes,
            Experts = experts.ToDictionary(e => e.Id, StringComparer.Ordinal),
            CancellationToken = ct,
        };

        // One async worker per branch: independent progress, branch-level isolation.
        var workers = nodes.Values
            .Select(node => Task.Run(() => RunNodeAsync(state, node), CancellationToken.None))
            .ToArray();
        await Task.WhenAll(workers);

        run.FinishedAtUtc = DateTime.UtcNow;
        run.MaxConcurrencyObserved = Volatile.Read(ref state.MaxConcurrency);
        run.Status = ComputeStatus(run);
        PersistRunRecord(run);
        return run;
    }

    // ================= node worker =================

    private async Task RunNodeAsync(RunState state, NodeRuntime node)
    {
        var ct = state.CancellationToken;
        try
        {
            if (node.Spec.DependsOn.Count > 0)
            {
                await Task.WhenAll(node.Spec.DependsOn.Select(d => state.Nodes[d].Completion.Task));
                var failedUpstream = node.Spec.DependsOn
                    .Where(d => state.Nodes[d].Record.Status != ClusterBranchStatus.Succeeded)
                    .ToList();
                if (failedUpstream.Count > 0)
                {
                    node.Record.Status = ClusterBranchStatus.Skipped;
                    node.Record.Error = "upstream dependency did not succeed: " + string.Join(", ", failedUpstream);
                    return;
                }
            }

            if (ct.IsCancellationRequested)
            {
                node.Record.Status = ClusterBranchStatus.Cancelled;
                node.Record.Error = "cancelled before start";
                return;
            }

            node.Record.Status = ClusterBranchStatus.Running;
            node.Record.StartedAtUtc = DateTime.UtcNow;

            if (node.Spec.FanOutCount >= 2)
            {
                await RunFanOutAsync(state, node, ct);
            }
            else
            {
                await RunSingleAsync(state, node, ct);
            }
        }
        catch (OperationCanceledException)
        {
            node.Record.Status = ClusterBranchStatus.Cancelled;
            node.Record.Error ??= "cancelled";
        }
        catch (Exception ex)
        {
            _logger?.LogError("ClusterScheduler node {Node} crashed: {Error}", node.Spec.Id, ex.Message);
            node.Record.Status = ClusterBranchStatus.Failed;
            node.Record.Error = "scheduler internal error: " + ex.Message;
        }
        finally
        {
            node.Record.FinishedAtUtc = DateTime.UtcNow;
            if (node.Record.StartedAtUtc is { } startedAt)
            {
                node.Record.DurationMs = (node.Record.FinishedAtUtc.Value - startedAt).TotalMilliseconds;
            }

            node.Completion.TrySetResult(true);
        }
    }

    // ================= single-expert path + swarm =================

    private async Task RunSingleAsync(RunState state, NodeRuntime node, CancellationToken ct)
    {
        string expertId;
        try
        {
            expertId = await state.Lease.AcquireAsync(ct);
        }
        catch (OperationCanceledException)
        {
            node.Record.Status = ClusterBranchStatus.Cancelled;
            node.Record.Error = "cancelled while waiting for an expert";
            return;
        }

        try
        {
            var assignment = RecordAssignment(node, expertId, ExpertAttemptKind.Primary);
            var outcome = await ExecuteAttemptAsync(state, node, expertId, ExpertAttemptKind.Primary, 0, ct);
            FillAssignment(assignment, outcome);
            AppendAttemptMemory(state, node, expertId, ExpertAttemptKind.Primary, outcome);

            if (outcome.Succeeded)
            {
                node.Record.Status = ClusterBranchStatus.Succeeded;
                node.Record.Output = outcome.Output;
                node.Record.ResolvedBy = ClusterResolution.Primary;
                return;
            }

            node.Record.Error = DescribeFailure(outcome);
        }
        finally
        {
            state.Lease.Release(expertId);
        }

        if (ct.IsCancellationRequested)
        {
            node.Record.Status = ClusterBranchStatus.Cancelled;
            return;
        }

        // Stuck node: draft whatever experts are idle right now into a swarm.
        if (!_options.EnableSwarm)
        {
            node.Record.Status = ClusterBranchStatus.Failed;
            return;
        }

        var idle = state.Lease.TryTakeIdle(_options.MaxSwarmExperts);
        if (idle.Count == 0)
        {
            _logger?.LogWarning(
                "[DEGRADED] Node {Node} is stuck but no expert is idle for a swarm; the node stays failed.",
                node.Spec.Id);
            lock (state.RecordLock)
            {
                state.Run.Swarms.Add(new SwarmRecord
                {
                    NodeId = node.Spec.Id,
                    Trigger = node.Record.Error ?? "primary attempt did not succeed",
                    StartedAtUtc = DateTime.UtcNow,
                    FinishedAtUtc = DateTime.UtcNow,
                    Outcome = SwarmOutcome.NoIdleExperts,
                });
            }

            node.Record.Status = ClusterBranchStatus.Failed;
            return;
        }

        await RunSwarmAsync(state, node, idle, ct);
    }

    private async Task RunSwarmAsync(RunState state, NodeRuntime node, List<string> experts, CancellationToken ct)
    {
        var swarm = new SwarmRecord
        {
            NodeId = node.Spec.Id,
            Trigger = node.Record.Error ?? "primary attempt did not succeed",
            StartedAtUtc = DateTime.UtcNow,
            Participants = experts.ToList(),
        };
        lock (state.RecordLock)
        {
            state.Run.Swarms.Add(swarm);
        }

        using var swarmCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var assignments = experts.Select(e => RecordAssignment(node, e, ExpertAttemptKind.Swarm)).ToList();
        var attempts = experts
            .Select((expertId, i) => RunSwarmAttemptAsync(state, node, expertId, assignments[i], swarmCts.Token))
            .ToList();

        // First success wins: watch attempts land and cancel the rest on the first win.
        ExpertExecutionOutcome? winnerOutcome = null;
        var pending = attempts.ToList();
        while (pending.Count > 0 && winnerOutcome is null)
        {
            var finished = await Task.WhenAny(pending);
            pending.Remove(finished);
            var (expertId, outcome) = await finished;
            if (outcome.Succeeded)
            {
                winnerOutcome = outcome;
                swarm.WinningExpertId = expertId;
                swarmCts.Cancel();
            }
        }

        if (winnerOutcome is not null)
        {
            await SettleAsync(Task.WhenAll(attempts), _options.SwarmSettleGrace);
            swarm.Outcome = SwarmOutcome.FirstSuccess;
            node.Record.Status = ClusterBranchStatus.Succeeded;
            node.Record.Output = winnerOutcome.Output;
            node.Record.ResolvedBy = ClusterResolution.Swarm;
            node.Record.Error = null;
        }
        else
        {
            swarm.Outcome = ct.IsCancellationRequested ? SwarmOutcome.Cancelled : SwarmOutcome.AggregatedFailure;
            swarm.AggregatedOutput = BuildSwarmAggregate(node);
            node.Record.Status = ct.IsCancellationRequested ? ClusterBranchStatus.Cancelled : ClusterBranchStatus.Failed;
            node.Record.Error = (node.Record.Error is null ? string.Empty : node.Record.Error + "; ")
                + "swarm produced no successful attempt";
        }

        swarm.FinishedAtUtc = DateTime.UtcNow;
    }

    private async Task<(string ExpertId, ExpertExecutionOutcome Outcome)> RunSwarmAttemptAsync(
        RunState state, NodeRuntime node, string expertId, ExpertAssignmentRecord assignment, CancellationToken ct)
    {
        try
        {
            var outcome = await ExecuteAttemptAsync(state, node, expertId, ExpertAttemptKind.Swarm, 0, ct);
            FillAssignment(assignment, outcome);
            AppendAttemptMemory(state, node, expertId, ExpertAttemptKind.Swarm, outcome);
            return (expertId, outcome);
        }
        finally
        {
            state.Lease.Release(expertId);
        }
    }

    private static string BuildSwarmAggregate(NodeRuntime node)
    {
        var lines = node.Record.Assignments
            .Where(a => a.Kind == ExpertAttemptKind.Swarm)
            .Select(a => $"{a.ExpertId}: {(a.Result?.ToString() ?? "Unknown")}"
                + (string.IsNullOrEmpty(a.Error) ? string.Empty : $" — {a.Error}")
                + (string.IsNullOrEmpty(a.OutputPreview) ? string.Empty : $" — {a.OutputPreview}"));
        return string.Join(Environment.NewLine, lines);
    }

    // ================= fan-out race =================

    private async Task RunFanOutAsync(RunState state, NodeRuntime node, CancellationToken ct)
    {
        var spec = node.Spec;
        var judge = spec.Judge ?? _options.DefaultJudge!; // validated non-null in RunAsync
        var count = spec.FanOutCount;

        var experts = new List<string>(count);
        try
        {
            for (var i = 0; i < count; i++)
            {
                experts.Add(await state.Lease.AcquireAsync(ct));
            }
        }
        catch (OperationCanceledException)
        {
            foreach (var acquired in experts)
            {
                state.Lease.Release(acquired);
            }

            node.Record.Status = ClusterBranchStatus.Cancelled;
            node.Record.Error = "cancelled while acquiring fan-out experts";
            return;
        }

        var assignments = experts.Select(e => RecordAssignment(node, e, ExpertAttemptKind.FanOut)).ToList();
        var fanOut = new FanOutRecord
        {
            NodeId = spec.Id,
            RequestedCandidates = count,
            StartedAtUtc = DateTime.UtcNow,
        };
        lock (state.RecordLock)
        {
            state.Run.FanOuts.Add(fanOut);
        }

        var attemptTasks = experts
            .Select((expertId, i) => RunFanOutAttemptAsync(state, node, expertId, assignments[i], i, ct))
            .ToList();
        var results = (await Task.WhenAll(attemptTasks)).ToList();
        fanOut.FinishedAtUtc = DateTime.UtcNow;
        fanOut.Candidates = results
            .OrderBy(r => r.FanOutIndex)
            .Select(r => new FanOutCandidateRecord
            {
                ExpertId = r.ExpertId,
                FanOutIndex = r.FanOutIndex,
                Succeeded = r.Outcome.Succeeded,
                OutputPreview = Truncate(r.Outcome.Output, 500),
                Error = r.Outcome.Error,
            })
            .ToList();

        var candidates = results
            .Where(r => r.Outcome.Succeeded)
            .OrderBy(r => r.FanOutIndex)
            .Select(r => new FanOutCandidate(r.ExpertId, r.Outcome.Output))
            .ToList();

        if (candidates.Count == 0)
        {
            node.Record.Status = ClusterBranchStatus.Failed;
            node.Record.Error = $"fan-out produced no successful candidate ({results.Count} attempt(s))";
            return;
        }

        FanOutDecision? decision = null;
        try
        {
            decision = await judge(new FanOutBallot(spec.Id, DisplayName(spec), candidates), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            node.Record.Status = ClusterBranchStatus.Cancelled;
            node.Record.Error = "cancelled during fan-out judging";
            return;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(
                "[DEGRADED] Fan-out judge for node {Node} threw ({Error}); falling back to the first successful candidate.",
                spec.Id, ex.Message);
            fanOut.JudgeDegraded = true;
        }

        FanOutCandidate? winner = null;
        if (decision is not null)
        {
            winner = candidates.FirstOrDefault(c => c.ExpertId == decision.WinnerExpertId);
            if (winner is null)
            {
                _logger?.LogWarning(
                    "[DEGRADED] Fan-out judge for node {Node} selected unknown expert '{Expert}'; falling back to the first successful candidate.",
                    spec.Id, decision.WinnerExpertId);
                fanOut.JudgeDegraded = true;
            }
        }

        winner ??= candidates[0];

        node.Record.Status = ClusterBranchStatus.Succeeded;
        node.Record.Output = decision is not null && !string.IsNullOrWhiteSpace(decision.FinalOutput)
            ? decision.FinalOutput
            : winner.Output;
        node.Record.ResolvedBy = ClusterResolution.FanOut;
        fanOut.WinnerExpertId = winner.ExpertId;
        fanOut.JudgeReason = decision?.Reason;
        fanOut.FinalOutputPreview = Truncate(node.Record.Output, 500);
    }

    private async Task<FanOutAttemptResult> RunFanOutAttemptAsync(
        RunState state, NodeRuntime node, string expertId, ExpertAssignmentRecord assignment, int index, CancellationToken ct)
    {
        try
        {
            var outcome = await ExecuteAttemptAsync(state, node, expertId, ExpertAttemptKind.FanOut, index, ct);
            FillAssignment(assignment, outcome);
            AppendAttemptMemory(state, node, expertId, ExpertAttemptKind.FanOut, outcome);
            return new FanOutAttemptResult(expertId, index, outcome);
        }
        finally
        {
            state.Lease.Release(expertId);
        }
    }

    // ================= attempt execution (timeout-isolated) =================

    private async Task<ExpertExecutionOutcome> ExecuteAttemptAsync(
        RunState state, NodeRuntime node, string expertId, ExpertAttemptKind kind, int fanOutIndex, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return new ExpertExecutionOutcome(false, true, false, string.Empty, "cancelled before start", 0);
        }

        var handle = state.Experts[expertId];
        var context = new ExpertExecutionContext(
            expertId,
            handle.SessionId,
            handle.Role,
            node.Spec.Id,
            DisplayName(node.Spec),
            node.Spec.TaskText,
            _pool.BuildMemorySnapshot(expertId, _options.MaxMemoryEntriesInjected),
            kind,
            fanOutIndex);

        state.EnterExecution();
        var startedAt = DateTime.UtcNow;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_options.AttemptTimeout);

        ExpertExecutionOutcome outcome;
        Task<ExpertExecutionOutcome>? execution = null;
        try
        {
            execution = _executor.ExecuteAsync(context, timeoutCts.Token)
                ?? throw new InvalidOperationException($"Expert executor returned null for node '{node.Spec.Id}'.");
            var timer = Task.Delay(_options.AttemptTimeout, ct);
            var finished = await Task.WhenAny(execution, timer);
            if (finished == execution)
            {
                outcome = await execution
                    ?? throw new InvalidOperationException($"Expert executor returned a null outcome for node '{node.Spec.Id}'.");
            }
            else if (ct.IsCancellationRequested)
            {
                timeoutCts.Cancel();
                ObserveOrphan(execution);
                outcome = new ExpertExecutionOutcome(false, true, false, string.Empty, "run cancelled during execution", 0);
            }
            else
            {
                timeoutCts.Cancel();
                ObserveOrphan(execution);
                outcome = new ExpertExecutionOutcome(
                    false, false, true, string.Empty,
                    $"attempt timed out after {_options.AttemptTimeout.TotalMilliseconds:0} ms", 0);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (execution is not null)
            {
                ObserveOrphan(execution);
            }

            outcome = new ExpertExecutionOutcome(false, true, false, string.Empty, "run cancelled during execution", 0);
        }
        catch (OperationCanceledException)
        {
            // The executor honored the per-attempt timeout token.
            outcome = new ExpertExecutionOutcome(
                false, false, true, string.Empty,
                $"attempt timed out after {_options.AttemptTimeout.TotalMilliseconds:0} ms", 0);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(
                "Expert {Expert} attempt on node {Node} threw: {Error}", expertId, node.Spec.Id, ex.Message);
            outcome = new ExpertExecutionOutcome(false, false, false, string.Empty, "executor exception: " + ex.Message, 0);
        }
        finally
        {
            state.ExitExecution();
        }

        return outcome with { DurationMs = (DateTime.UtcNow - startedAt).TotalMilliseconds };
    }

    /// <summary>
    /// Attach an observer to an abandoned task so its (late) exception can never surface
    /// as an UnobservedTaskException; the branch has already moved on.
    /// </summary>
    private static void ObserveOrphan(Task task) =>
        task.ContinueWith(
            static t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);

    private static async Task SettleAsync(Task all, TimeSpan grace)
    {
        try
        {
            var finished = await Task.WhenAny(all, Task.Delay(grace));
            if (finished == all)
            {
                await all;
            }
        }
        catch (Exception)
        {
            // Settling is best-effort: wrapped attempts never throw and release their
            // expert lease in a finally, so an unsettled loser cannot corrupt state.
        }
    }

    // ================= bookkeeping =================

    private ExpertAssignmentRecord RecordAssignment(NodeRuntime node, string expertId, ExpertAttemptKind kind)
    {
        var assignment = new ExpertAssignmentRecord
        {
            ExpertId = expertId,
            Kind = kind,
            StartedAtUtc = DateTime.UtcNow,
        };
        node.Record.Assignments.Add(assignment);
        return assignment;
    }

    private static void FillAssignment(ExpertAssignmentRecord assignment, ExpertExecutionOutcome outcome)
    {
        assignment.FinishedAtUtc = DateTime.UtcNow;
        assignment.DurationMs = outcome.DurationMs;
        assignment.Result = outcome.Succeeded
            ? ExpertAttemptResult.Succeeded
            : outcome.TimedOut
                ? ExpertAttemptResult.TimedOut
                : outcome.Cancelled
                    ? ExpertAttemptResult.Cancelled
                    : ExpertAttemptResult.Failed;
        assignment.Error = outcome.Error;
        assignment.OutputPreview = Truncate(outcome.Output, 500);
    }

    private void AppendAttemptMemory(
        RunState state, NodeRuntime node, string expertId, ExpertAttemptKind kind, ExpertExecutionOutcome outcome)
    {
        try
        {
            var result = outcome.Succeeded
                ? "succeeded"
                : outcome.TimedOut
                    ? "timed-out"
                    : outcome.Cancelled
                        ? "cancelled"
                        : "failed";
            var brief = Truncate(outcome.Succeeded ? outcome.Output : outcome.Error ?? string.Empty, 200);
            _pool.AppendMemory(
                expertId,
                "cluster",
                $"run {state.Run.RunId[..8]} node {node.Spec.Id} attempt {kind}: {result}. {brief}".Trim());
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("[DEGRADED] Failed to append cluster memory for expert {Expert}: {Error}", expertId, ex.Message);
        }
    }

    private void PersistRunRecord(ClusterRunRecord run)
    {
        try
        {
            var directory = _options.RunRecordDirectory ?? Path.Combine(_pool.ClusterDirectory, "runs");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"cluster-run-{run.RunId}.json");
            run.PersistedPath = path;
            var tmp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(tmp, JsonSerializer.Serialize(run, ClusterJson.Options));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("[DEGRADED] ClusterScheduler could not persist run record {RunId}: {Error}", run.RunId, ex.Message);
        }
    }

    private static ClusterRunStatus ComputeStatus(ClusterRunRecord run)
    {
        if (run.Branches.Any(b => b.Status == ClusterBranchStatus.Cancelled))
        {
            return ClusterRunStatus.Cancelled;
        }

        if (run.Branches.All(b => b.Status == ClusterBranchStatus.Succeeded))
        {
            return ClusterRunStatus.Succeeded;
        }

        if (run.Branches.Any(b => b.Status == ClusterBranchStatus.Succeeded))
        {
            return ClusterRunStatus.PartiallySucceeded;
        }

        return ClusterRunStatus.Failed;
    }

    private static string DisplayName(ClusterBranchSpec spec) =>
        string.IsNullOrWhiteSpace(spec.Name) ? spec.Id : spec.Name;

    private static string DescribeFailure(ExpertExecutionOutcome outcome) =>
        outcome.TimedOut
            ? outcome.Error ?? "attempt timed out"
            : outcome.Cancelled
                ? outcome.Error ?? "attempt cancelled"
                : outcome.Error ?? "attempt failed";

    private static string Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Length <= max ? value : value[..max];
    }

    // ================= per-run state =================

    private sealed class RunState
    {
        public required ClusterRunRecord Run { get; init; }
        public required ExpertLease Lease { get; init; }
        public required Dictionary<string, NodeRuntime> Nodes { get; init; }
        public required Dictionary<string, ExpertHandle> Experts { get; init; }
        public CancellationToken CancellationToken { get; init; }

        public int Concurrency;
        public int MaxConcurrency;
        public readonly object RecordLock = new();

        public void EnterExecution()
        {
            var current = Interlocked.Increment(ref Concurrency);
            int max;
            do
            {
                max = Volatile.Read(ref MaxConcurrency);
                if (current <= max)
                {
                    break;
                }
            }
            while (Interlocked.CompareExchange(ref MaxConcurrency, current, max) != max);
        }

        public void ExitExecution() => Interlocked.Decrement(ref Concurrency);
    }

    private sealed class NodeRuntime
    {
        public NodeRuntime(ClusterBranchSpec spec)
        {
            Spec = spec;
            Record = new ClusterBranchRecord
            {
                BranchId = spec.Id,
                Name = DisplayName(spec),
                Status = ClusterBranchStatus.Pending,
            };
        }

        public ClusterBranchSpec Spec { get; }
        public ClusterBranchRecord Record { get; }
        public TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>
    /// Expert lease: a bounded pool of expert ids. Acquire awaits an idle expert;
    /// release returns it. TryTakeIdle drains up to N currently-idle experts without
    /// blocking (used to mount swarms). Invariant: semaphore count == queued ids.
    /// </summary>
    private sealed class ExpertLease
    {
        private readonly object _gate = new();
        private readonly Queue<string> _idle = new();
        private readonly SemaphoreSlim _signal = new(0);

        public ExpertLease(IEnumerable<string> expertIds)
        {
            foreach (var id in expertIds)
            {
                _idle.Enqueue(id);
                _signal.Release();
            }
        }

        public async Task<string> AcquireAsync(CancellationToken ct)
        {
            await _signal.WaitAsync(ct);
            lock (_gate)
            {
                return _idle.Dequeue();
            }
        }

        public void Release(string expertId)
        {
            lock (_gate)
            {
                _idle.Enqueue(expertId);
            }

            _signal.Release();
        }

        public List<string> TryTakeIdle(int max)
        {
            var taken = new List<string>();
            while (taken.Count < max && _signal.Wait(0))
            {
                lock (_gate)
                {
                    taken.Add(_idle.Dequeue());
                }
            }

            return taken;
        }
    }

    private sealed record FanOutAttemptResult(string ExpertId, int FanOutIndex, ExpertExecutionOutcome Outcome);
}
