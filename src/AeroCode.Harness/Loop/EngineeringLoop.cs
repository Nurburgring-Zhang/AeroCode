// Copyright (c) AeroCode V3.0
// EngineeringLoop — the real Plan → Build → Verify → Review → Fix engineering cycle.
//
// Wires the previously-dead harness primitives into a running loop:
//   - Plan   : existing Planner (LLM producer when available, deterministic fallback otherwise)
//   - Build  : LoopRunner executing the caller's step delegate, with a PatchEngine-backed
//              repair strategy (snapshot before patch, rollback on failed patch)
//   - Verify : QualityGate evaluating the pre-defined AcceptanceContract against real evidence
//   - Review : DualAiArena adversarial Builder/Reviewer/Judge review
//   - Fix    : caller-provided FixProposer + PatchEngine application with file snapshots
// Termination: gate passes (and review accepts) / max rounds / budget exhausted /
// no fix available (failure blocking — a failed gate can never just "continue") / cancellation.
// Every round is fully traced (LoopTrace) and serialized to JSON on disk.
using System.Diagnostics;
using System.Text;
using AeroCode.Harness.Blockade;
using AeroCode.Harness.Gates;
using AeroCode.Harness.Patch;
using AeroCode.Harness.Planner;
using AeroCode.Harness.Review;
using Microsoft.Extensions.Logging;

namespace AeroCode.Harness.Loop;

/// <summary>Thrown when the loop's LLM-call budget is spent.</summary>
public sealed class LoopBudgetExhaustedException : InvalidOperationException
{
    /// <summary>Creates the exception with a message.</summary>
    public LoopBudgetExhaustedException(string message) : base(message) { }
}

/// <summary>
/// Tracks the consumable budget of a loop run: rounds, LLM calls and wall-clock duration.
/// Thread-safe.
/// </summary>
public sealed class LoopBudget
{
    private readonly object _lock = new();
    private readonly DateTime _startedAtUtc = DateTime.UtcNow;
    private int _llmCallsUsed;
    private int _roundsUsed;

    /// <summary>Maximum number of engineering rounds.</summary>
    public int MaxRounds { get; }

    /// <summary>Maximum number of LLM calls across all loop components.</summary>
    public int MaxLlmCalls { get; }

    /// <summary>Maximum wall-clock duration of the whole run.</summary>
    public TimeSpan MaxDuration { get; }

    /// <summary>Creates a budget; all limits must be positive.</summary>
    public LoopBudget(int maxRounds, int maxLlmCalls, TimeSpan maxDuration)
    {
        if (maxRounds < 1) throw new ArgumentOutOfRangeException(nameof(maxRounds));
        if (maxLlmCalls < 1) throw new ArgumentOutOfRangeException(nameof(maxLlmCalls));
        if (maxDuration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(maxDuration));
        MaxRounds = maxRounds;
        MaxLlmCalls = maxLlmCalls;
        MaxDuration = maxDuration;
    }

    /// <summary>Derives a budget from loop options.</summary>
    public static LoopBudget FromOptions(EngineeringLoopOptions options) =>
        new(options.MaxRounds, options.MaxLlmCalls, options.MaxDuration);

    /// <summary>Rounds consumed so far.</summary>
    public int RoundsUsed { get { lock (_lock) return _roundsUsed; } }

    /// <summary>LLM calls consumed so far.</summary>
    public int LlmCallsUsed { get { lock (_lock) return _llmCallsUsed; } }

    /// <summary>Elapsed wall-clock time since the budget was created.</summary>
    public TimeSpan Elapsed => DateTime.UtcNow - _startedAtUtc;

    /// <summary>True when the wall-clock budget is spent.</summary>
    public bool IsDurationExhausted() => Elapsed > MaxDuration;

    /// <summary>True when at least one more LLM call fits the budget.</summary>
    public bool CanConsumeLlmCall { get { lock (_lock) return _llmCallsUsed < MaxLlmCalls; } }

    /// <summary>Consumes one LLM call; returns false when the budget is already spent.</summary>
    public bool TryConsumeLlmCall()
    {
        lock (_lock)
        {
            if (_llmCallsUsed >= MaxLlmCalls) return false;
            _llmCallsUsed++;
            return true;
        }
    }

    /// <summary>Consumes one round.</summary>
    public void ConsumeRound() { lock (_lock) _roundsUsed++; }
}

/// <summary>Options for an <see cref="EngineeringLoop"/> run.</summary>
public sealed class EngineeringLoopOptions
{
    /// <summary>Maximum engineering rounds (default 5).</summary>
    public int MaxRounds { get; init; } = 5;

    /// <summary>Maximum build attempts per round inside the LoopRunner (default 3).</summary>
    public int MaxBuildAttemptsPerRound { get; init; } = 3;

    /// <summary>Maximum total LLM calls across planner/arena (default 200).</summary>
    public int MaxLlmCalls { get; init; } = 200;

    /// <summary>Maximum wall-clock duration of the run (default 30 minutes).</summary>
    public TimeSpan MaxDuration { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>Directory for loop traces, gate reports, arena transcripts and build-error logs.</summary>
    public string? TraceDirectory { get; init; }

    /// <summary>Root directory that patch file paths are resolved against (default: current directory).</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Whether the Review (dual-AI arena) phase runs (default true).</summary>
    public bool EnableReview { get; init; } = true;

    /// <summary>
    /// Optional blockade hook (G7): when the gate fails, run real web research on the
    /// failure and turn each research-grounded candidate into a guided fix attempt.
    /// </summary>
    public BlockadeResolver? BlockadeResolver { get; init; }
}

/// <summary>Why the engineering loop terminated.</summary>
public enum LoopTerminationReason
{
    /// <summary>Success: the quality gate passed and the review accepted (or review was disabled).</summary>
    GatePassed,
    /// <summary>The round limit was reached without the gate passing.</summary>
    MaxRoundsReached,
    /// <summary>The budget (duration or LLM calls) was exhausted.</summary>
    BudgetExhausted,
    /// <summary>
    /// Failure blocking: the gate failed (or review rejected) and no repair could be applied —
    /// the loop refuses to continue on a failing state and terminates honestly.
    /// </summary>
    NoFixAvailable,
    /// <summary>The run was cancelled.</summary>
    Cancelled,
}

/// <summary>Context handed to a <see cref="FixProposer"/> so it can produce concrete patches.</summary>
public sealed class FixContext
{
    /// <summary>The loop goal.</summary>
    public required string Goal { get; init; }

    /// <summary>Round that produced the failure (1-based).</summary>
    public required int Round { get; init; }

    /// <summary>The current plan, when available.</summary>
    public Plan? Plan { get; init; }

    /// <summary>Last build error, when the build failed.</summary>
    public string? LastBuildError { get; init; }

    /// <summary>The gate report with the failing/inconclusive criteria.</summary>
    public GateReport? GateReport { get; init; }

    /// <summary>Critique from the last valid arena round, when the review ran.</summary>
    public string? ReviewCritique { get; init; }

    /// <summary>Final review verdict, when the review ran.</summary>
    public ArenaVerdict? ReviewVerdict { get; init; }

    /// <summary>Root directory patch paths resolve against.</summary>
    public string WorkingDirectory { get; init; } = string.Empty;

    /// <summary>Where the fix was triggered: "build" (inside the build repair), "round" (between rounds) or "blockade" (blockade hook).</summary>
    public string Stage { get; init; } = "round";

    /// <summary>
    /// Research-grounded fix approaches harvested by the blockade hook (G7).
    /// Empty when the blockade resolver did not run.
    /// </summary>
    public IReadOnlyList<string> BlockadeHints { get; init; } = Array.Empty<string>();
}

/// <summary>Proposes concrete patches for a failure. Must return real patches (empty list = cannot fix).</summary>
public delegate Task<IReadOnlyList<AeroCode.Harness.Patch.Patch>> FixProposer(FixContext context, CancellationToken ct);

/// <summary>Supplies the real evidence artifacts for the given round (test outputs, logs, …).</summary>
public delegate Task<IReadOnlyList<EvidenceArtifact>> EvidenceProvider(int round, CancellationToken ct);

/// <summary>Result of an engineering loop run.</summary>
public sealed class EngineeringLoopResult
{
    /// <summary>True only when the gate passed and the review accepted (or review disabled).</summary>
    public bool Succeeded { get; init; }

    /// <summary>Why the loop terminated.</summary>
    public LoopTerminationReason TerminationReason { get; init; }

    /// <summary>Human-readable termination detail.</summary>
    public string TerminationDetail { get; init; } = string.Empty;

    /// <summary>Rounds executed.</summary>
    public int RoundsExecuted { get; init; }

    /// <summary>Last gate report, if the gate ran.</summary>
    public GateReport? FinalGateReport { get; init; }

    /// <summary>Last arena result, if the review ran.</summary>
    public ArenaResult? FinalReviewResult { get; init; }

    /// <summary>Path of the persisted loop trace JSON.</summary>
    public string? TracePath { get; init; }

    /// <summary>The full in-memory trace.</summary>
    public LoopTrace Trace { get; init; } = new();
}

/// <summary>
/// The engineering loop: Plan → Build → Verify → Review → Fix, run until the acceptance
/// contract passes, the round limit is hit, the budget is exhausted, or failure blocking
/// applies. All phases really execute; every round is traced to disk.
/// </summary>
public sealed class EngineeringLoop
{
    private readonly AeroCode.Harness.Planner.Planner _planner;
    private readonly QualityGate _gate;
    private readonly DualAiArena _arena;
    private readonly PatchEngine _patchEngine;
    private readonly LoopBudget _budget;
    private readonly EngineeringLoopOptions _options;
    private readonly ILogger? _logger;

    /// <summary>Creates the loop from its (already wired) components.</summary>
    /// <param name="planner">Plan phase (existing Planner).</param>
    /// <param name="gate">Verify phase (contract-based QualityGate).</param>
    /// <param name="arena">Review phase (DualAiArena).</param>
    /// <param name="patchEngine">Fix executor (real patch application + rollback support).</param>
    /// <param name="budget">Budget for rounds / LLM calls / duration.</param>
    /// <param name="options">Loop options.</param>
    /// <param name="logger">Optional logger.</param>
    public EngineeringLoop(
        AeroCode.Harness.Planner.Planner planner,
        QualityGate gate,
        DualAiArena arena,
        PatchEngine patchEngine,
        LoopBudget budget,
        EngineeringLoopOptions? options = null,
        ILogger? logger = null)
    {
        _planner = planner ?? throw new ArgumentNullException(nameof(planner));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _arena = arena ?? throw new ArgumentNullException(nameof(arena));
        _patchEngine = patchEngine ?? throw new ArgumentNullException(nameof(patchEngine));
        _budget = budget ?? throw new ArgumentNullException(nameof(budget));
        _options = options ?? new EngineeringLoopOptions();
        if (_options.MaxBuildAttemptsPerRound < 1)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxBuildAttemptsPerRound must be >= 1.");
        _logger = logger;
    }

    /// <summary>The budget governing this loop.</summary>
    public LoopBudget Budget => _budget;

    /// <summary>The options governing this loop.</summary>
    public EngineeringLoopOptions Options => _options;

    /// <summary>
    /// Run the engineering loop against a goal and a pre-defined acceptance contract.
    /// </summary>
    /// <param name="goal">What must be achieved.</param>
    /// <param name="contract">The acceptance contract, defined BEFORE execution.</param>
    /// <param name="buildStep">The real build executor: returns null on success, an error message on failure.</param>
    /// <param name="fixProposer">Optional producer of repair patches for failures.</param>
    /// <param name="evidenceProvider">Optional supplier of real evidence artifacts per round.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<EngineeringLoopResult> RunAsync(
        string goal,
        AcceptanceContract contract,
        StepAttempt buildStep,
        FixProposer? fixProposer = null,
        EvidenceProvider? evidenceProvider = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(goal);
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(buildStep);
        if (contract.Criteria.Count == 0)
            throw new ArgumentException("AcceptanceContract must contain at least one criterion.", nameof(contract));

        var loopId = Guid.NewGuid().ToString("N");
        var traceDir = _options.TraceDirectory ?? Path.Combine(Path.GetTempPath(), "aerocode-loops");
        Directory.CreateDirectory(traceDir);
        var tracePath = Path.Combine(traceDir, $"loop-trace-{loopId}.json");
        var rootDir = string.IsNullOrEmpty(_options.WorkingDirectory)
            ? Directory.GetCurrentDirectory()
            : Path.GetFullPath(_options.WorkingDirectory);

        var trace = new LoopTrace
        {
            LoopId = loopId,
            Goal = goal,
            StartedAtUtc = DateTime.UtcNow,
        };

        try
        {
            return await RunCoreAsync(goal, contract, buildStep, fixProposer, evidenceProvider, trace, tracePath, traceDir, rootDir, ct);
        }
        catch (OperationCanceledException)
        {
            return Finalize(trace, tracePath, false, LoopTerminationReason.Cancelled, "The run was cancelled.");
        }
        catch (LoopBudgetExhaustedException ex)
        {
            return Finalize(trace, tracePath, false, LoopTerminationReason.BudgetExhausted, ex.Message);
        }
    }

    private async Task<EngineeringLoopResult> RunCoreAsync(
        string goal,
        AcceptanceContract contract,
        StepAttempt buildStep,
        FixProposer? fixProposer,
        EvidenceProvider? evidenceProvider,
        LoopTrace trace,
        string tracePath,
        string traceDir,
        string rootDir,
        CancellationToken ct)
    {
        GateReport? lastGate = null;
        ArenaResult? lastArena = null;
        string? lastBuildError = null;
        Plan? plan = null;

        for (var round = 1; round <= _options.MaxRounds; round++)
        {
            ct.ThrowIfCancellationRequested();
            _budget.ConsumeRound();

            if (_budget.IsDurationExhausted())
            {
                return Finalize(trace, tracePath, false, LoopTerminationReason.BudgetExhausted,
                    $"Duration budget exhausted before round {round} ({_budget.Elapsed:g} > {_budget.MaxDuration:g}).");
            }
            if (_planner.HasLlmProducer && !_budget.CanConsumeLlmCall)
            {
                return Finalize(trace, tracePath, false, LoopTerminationReason.BudgetExhausted,
                    $"LLM call budget exhausted before round {round} ({_budget.LlmCallsUsed}/{_budget.MaxLlmCalls} used).");
            }

            var roundTrace = new RoundTrace { Round = round };
            trace.Rounds.Add(roundTrace);
            _logger?.LogInformation("EngineeringLoop {LoopId} round {Round}/{Max} starting.", trace.LoopId, round, _options.MaxRounds);

            // ============ PLAN ============
            var planPhase = StartPhase("Plan");
            var planPrompt = round == 1 ? goal : AugmentGoal(goal, round, lastBuildError, lastGate, lastArena);
            plan = await _planner.DecomposeAsync(planPrompt, ct);
            planPhase.InputSummary = Truncate(planPrompt, 500);
            planPhase.OutputSummary = $"{plan.Steps.Count} step(s): " + string.Join("; ", plan.Steps.Select(s => $"{s.Id}:{s.Title}"));
            EndPhase(planPhase);
            roundTrace.Plan = planPhase;

            // ============ BUILD (LoopRunner with PatchEngine-backed repair) ============
            var buildPhase = StartPhase("Build");
            var strategies = new List<RepairStrategy>();
            if (fixProposer is not null)
                strategies.Add(BuildPatchRepairStrategy(buildStep, fixProposer, goal, round, plan, rootDir, buildPhase));
            var runner = new LoopRunner(_options.MaxBuildAttemptsPerRound, strategies, cache: null);
            var loopResult = await runner.RunAsync(buildStep, cacheKey: null, ct);
            lastBuildError = loopResult.History.LastOrDefault(i => i.Error is not null)?.Error;
            buildPhase.Verdict = loopResult.Succeeded ? "Pass" : "Fail";
            buildPhase.OutputSummary = loopResult.Succeeded
                ? $"Build succeeded ({loopResult.TerminationReason})."
                : $"Build failed: {lastBuildError ?? loopResult.TerminationReason}";
            foreach (var it in loopResult.History)
            {
                var note = $"iter {it.Index}: succeeded={it.Succeeded}, {it.Duration.TotalMilliseconds:F0}ms";
                if (it.Error is not null) note += $", error={Truncate(it.Error, 200)}";
                if (it.RepairApplied is not null) note += $", repair={it.RepairApplied}";
                buildPhase.Notes.Add(note);
            }
            EndPhase(buildPhase);
            roundTrace.Build = buildPhase;

            // ============ VERIFY (QualityGate) ============
            var verifyPhase = StartPhase("Verify");
            var evidence = evidenceProvider is not null
                ? (await evidenceProvider(round, ct)).ToList()
                : new List<EvidenceArtifact>();
            if (!loopResult.Succeeded)
            {
                // The build failure itself is a real execution signal: persist it as an artifact.
                var errPath = Path.Combine(traceDir, $"build-error-{trace.LoopId}-r{round}.log");
                File.WriteAllText(errPath,
                    $"Build failed in round {round}.\nTermination: {loopResult.TerminationReason}\nLast error: {lastBuildError}\n" +
                    $"Iterations: {loopResult.History.Count}\n");
                evidence.Add(new EvidenceArtifact
                {
                    Kind = EvidenceKind.ExecutionLog,
                    Path = errPath,
                    Summary = "Build failure execution log produced by the loop itself.",
                    Signals = new[]
                    {
                        new QualitySignal
                        {
                            Source = SignalSource.Execution,
                            IndicatesSuccess = false,
                            Detail = lastBuildError ?? "build failed",
                        },
                    },
                });
                verifyPhase.Notes.Add($"Build failed — wrote execution-log evidence: {errPath}");
            }
            var gateReport = await _gate.EvaluateAsync(contract, evidence, reportDirectory: traceDir, ct);
            lastGate = gateReport;
            verifyPhase.Verdict = gateReport.Overall.ToString();
            verifyPhase.OutputSummary = string.Join(" | ", gateReport.Criteria.Select(c => $"{c.CriterionId}={c.Verdict}"));
            verifyPhase.EvidencePaths.AddRange(evidence.Where(e => e.ExistsOnDisk()).Select(e => e.Path));
            if (gateReport.ReportPath is not null) verifyPhase.EvidencePaths.Add(gateReport.ReportPath);
            EndPhase(verifyPhase);
            roundTrace.Verify = verifyPhase;

            // ============ REVIEW (DualAiArena) ============
            ArenaResult? arenaResult = null;
            if (_options.EnableReview)
            {
                var reviewPhase = StartPhase("Review");
                var subject = BuildReviewSubject(goal, plan, loopResult, lastBuildError, gateReport);
                arenaResult = await _arena.RunAsync(subject, transcriptDirectory: traceDir, ct);
                lastArena = arenaResult;
                reviewPhase.Verdict = arenaResult.FinalVerdict.ToString();
                reviewPhase.OutputSummary = $"converged={arenaResult.Converged}; {arenaResult.TerminationReason}";
                if (arenaResult.TranscriptPath is not null) reviewPhase.EvidencePaths.Add(arenaResult.TranscriptPath);
                foreach (var r in arenaResult.Rounds)
                {
                    reviewPhase.Notes.Add(r.Valid
                        ? $"arena round {r.Index}: verdict={r.Verdict}, reason='{Truncate(r.VerdictReason ?? "", 160)}'"
                        : $"arena round {r.Index}: INVALID ({r.InvalidReason})");
                }
                EndPhase(reviewPhase);
                roundTrace.Review = reviewPhase;
            }

            // ============ DECIDE ============
            var gatePassed = gateReport.Overall == GateVerdict.Pass;
            var reviewAccepted = arenaResult is null || arenaResult.FinalVerdict == ArenaVerdict.Accept;
            if (gatePassed && reviewAccepted)
            {
                trace.FinalGateVerdict = gateReport.Overall.ToString();
                trace.FinalReviewVerdict = arenaResult?.FinalVerdict.ToString() ?? "(review disabled)";
                LoopTraceSerializer.Write(trace, tracePath);
                _logger?.LogInformation("EngineeringLoop {LoopId} succeeded in round {Round}.", trace.LoopId, round);
                return Finalize(trace, tracePath, true, LoopTerminationReason.GatePassed,
                    $"Quality gate passed in round {round}" +
                    (arenaResult is null ? " (review disabled)." : $" and review verdict was {arenaResult.FinalVerdict}."),
                    gateReport, arenaResult);
            }

            // ============ FIX (failure blocking: fix or terminate — never silently continue) ============
            if (fixProposer is null && _options.BlockadeResolver is null)
            {
                return Finalize(trace, tracePath, false, LoopTerminationReason.NoFixAvailable,
                    $"Round {round}: gate={gateReport.Overall}, review={arenaResult?.FinalVerdict.ToString() ?? "(disabled)"}; " +
                    "no fix proposer and no blockade resolver configured — failure blocking applies, the loop terminates instead of continuing.",
                    gateReport, arenaResult);
            }
            if (_budget.IsDurationExhausted())
            {
                return Finalize(trace, tracePath, false, LoopTerminationReason.BudgetExhausted,
                    $"Duration budget exhausted after round {round} verify/review.", gateReport, arenaResult);
            }

            var fixPhase = StartPhase("Fix");
            var fixContext = new FixContext
            {
                Goal = goal,
                Round = round,
                Plan = plan,
                LastBuildError = lastBuildError,
                GateReport = gateReport,
                ReviewCritique = arenaResult?.Rounds.LastOrDefault(r => r.Valid)?.Critique,
                ReviewVerdict = arenaResult?.FinalVerdict,
                WorkingDirectory = rootDir,
                Stage = "round",
            };

            // ---- Blockade hook (G7): real web research on the failure, then guided fix attempts ----
            if (_options.BlockadeResolver is { } resolver)
            {
                var resolution = await RunBlockadeAsync(resolver, fixContext, fixProposer, buildStep, rootDir, fixPhase, ct);
                if (resolution.Resolved)
                {
                    fixPhase.Verdict = "BlockadeResolved";
                    fixPhase.OutputSummary = resolution.Summary;
                    EndPhase(fixPhase);
                    roundTrace.Fix = fixPhase;
                    LoopTraceSerializer.Write(trace, tracePath);
                    _logger?.LogInformation("EngineeringLoop {LoopId} round {Round}: blockade resolved via research-guided fix; re-verifying next round.", trace.LoopId, round);
                    continue;
                }

                if (fixProposer is null)
                {
                    fixPhase.Verdict = "BlockadeUnresolved";
                    fixPhase.OutputSummary = resolution.Summary;
                    EndPhase(fixPhase);
                    roundTrace.Fix = fixPhase;
                    LoopTraceSerializer.Write(trace, tracePath);
                    return Finalize(trace, tracePath, false, LoopTerminationReason.NoFixAvailable,
                        $"Round {round}: blockade research completed ({resolution.Attempts.Count} attempts, {resolution.References.Count} references, degraded={resolution.SearchDegraded}) but no fix proposer configured — failure blocking applies.",
                        gateReport, arenaResult);
                }

                fixPhase.Notes.Add("blockade unresolved — falling back to the classic fix proposer");
            }

            if (fixProposer is null)
            {
                // Exhaustive safeguard: both-null case returned above; resolver-only case returned above.
                return Finalize(trace, tracePath, false, LoopTerminationReason.NoFixAvailable,
                    $"Round {round}: no fix proposer available.", gateReport, arenaResult);
            }

            IReadOnlyList<AeroCode.Harness.Patch.Patch> patches;
            try
            {
                patches = await fixProposer(fixContext, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                fixPhase.Verdict = "ProposerFailed";
                fixPhase.OutputSummary = $"Fix proposer threw: {ex.Message}";
                EndPhase(fixPhase);
                roundTrace.Fix = fixPhase;
                LoopTraceSerializer.Write(trace, tracePath);
                return Finalize(trace, tracePath, false, LoopTerminationReason.NoFixAvailable,
                    $"Round {round}: fix proposer failed ({ex.Message}); failure blocking applies.", gateReport, arenaResult);
            }

            fixPhase.InputSummary = $"{patches.Count} patch(es) proposed; gate={gateReport.Overall}, review={arenaResult?.FinalVerdict.ToString() ?? "(disabled)"}";
            if (patches.Count == 0)
            {
                fixPhase.Verdict = "NoPatches";
                fixPhase.OutputSummary = "Fix proposer returned no patches.";
                EndPhase(fixPhase);
                roundTrace.Fix = fixPhase;
                LoopTraceSerializer.Write(trace, tracePath);
                return Finalize(trace, tracePath, false, LoopTerminationReason.NoFixAvailable,
                    $"Round {round}: gate={gateReport.Overall} and the fix proposer returned no patches; failure blocking applies.",
                    gateReport, arenaResult);
            }

            var (applied, detail) = TryApplyFix(patches, rootDir, fixPhase);
            fixPhase.Verdict = applied ? "Applied" : "RolledBack";
            fixPhase.OutputSummary = detail;
            fixPhase.EvidencePaths.AddRange(patches.Select(p => Path.Combine(rootDir, p.FilePath)).Distinct());
            EndPhase(fixPhase);
            roundTrace.Fix = fixPhase;
            LoopTraceSerializer.Write(trace, tracePath);

            if (!applied)
            {
                return Finalize(trace, tracePath, false, LoopTerminationReason.NoFixAvailable,
                    $"Round {round}: fix could not be applied cleanly ({detail}); state was rolled back and the loop terminates instead of continuing on a failing state.",
                    gateReport, arenaResult);
            }

            _logger?.LogInformation("EngineeringLoop {LoopId} round {Round} fixed, proceeding to next round.", trace.LoopId, round);
        }

        return Finalize(trace, tracePath, false, LoopTerminationReason.MaxRoundsReached,
            $"Reached the maximum of {_options.MaxRounds} rounds without the quality gate passing.", lastGate, lastArena);
    }

    // ============ blockade hook (G7): research → guided fix attempts ============

    private async Task<BlockadeResolution> RunBlockadeAsync(
        BlockadeResolver resolver,
        FixContext baseContext,
        FixProposer? fixProposer,
        StepAttempt buildStep,
        string rootDir,
        PhaseTrace fixPhase,
        CancellationToken ct)
    {
        var errorText = baseContext.LastBuildError
            ?? $"quality gate verdict {baseContext.GateReport?.Overall}";
        var blockadeContext = new BlockadeContext(errorText, "engineering-loop", rootDir);
        var hints = new List<string>();

        var resolution = await resolver.ResolveAsync(blockadeContext, async (candidate, c) =>
        {
            hints.Add(candidate.Approach);

            if (fixProposer is null)
            {
                return (false, "no fix proposer configured; candidate recorded as reference only");
            }

            var guided = new FixContext
            {
                Goal = baseContext.Goal,
                Round = baseContext.Round,
                Plan = baseContext.Plan,
                LastBuildError = baseContext.LastBuildError,
                GateReport = baseContext.GateReport,
                ReviewCritique = baseContext.ReviewCritique,
                ReviewVerdict = baseContext.ReviewVerdict,
                WorkingDirectory = rootDir,
                Stage = "blockade",
                BlockadeHints = hints.ToList(),
            };

            IReadOnlyList<AeroCode.Harness.Patch.Patch> patches;
            try
            {
                patches = await fixProposer(guided, c);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return (false, $"fix proposer failed: {ex.Message}");
            }

            if (patches.Count == 0)
            {
                return (false, "fix proposer returned no patches for this candidate");
            }

            var (applied, detail) = TryApplyFix(patches, rootDir, fixPhase);
            if (!applied)
            {
                return (false, $"patch not applied: {detail}");
            }

            var err = await buildStep(c);
            return (err is null, err ?? "build passed after applying candidate fix");
        }, BlockadeResolver.DefaultMaxCandidates, ct);

        foreach (var attempt in resolution.Attempts)
        {
            fixPhase.Notes.Add(
                $"blockade attempt {attempt.Index} ({attempt.Candidate.Title}): " +
                $"{(attempt.Succeeded ? "resolved" : "failed")} — {attempt.Detail}");
        }

        return resolution;
    }

    // ============ build-phase repair strategy (LoopRunner RepairStrategy → PatchEngine) ============

    private RepairStrategy BuildPatchRepairStrategy(
        StepAttempt buildStep,
        FixProposer fixProposer,
        string goal,
        int round,
        Plan plan,
        string rootDir,
        PhaseTrace buildPhase)
    {
        return async (lastError, history, ct) =>
        {
            FixContext ctx = new()
            {
                Goal = goal,
                Round = round,
                Plan = plan,
                LastBuildError = lastError,
                WorkingDirectory = rootDir,
                Stage = "build",
            };

            IReadOnlyList<AeroCode.Harness.Patch.Patch> patches;
            try
            {
                patches = await fixProposer(ctx, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger?.LogWarning("[DEGRADED] Build-repair fix proposer threw: {Error}", ex.Message);
                buildPhase.Notes.Add($"build repair: proposer failed ({ex.Message})");
                return null;
            }

            if (patches.Count == 0)
            {
                buildPhase.Notes.Add("build repair: proposer returned no patches");
                return null;
            }

            var (applied, detail) = TryApplyFix(patches, rootDir, buildPhase);
            buildPhase.Notes.Add($"build repair: {detail}");
            if (!applied) return null;

            // Re-run the REAL build step now that the patch is applied.
            return buildStep;
        };
    }

    // ============ fix application with snapshot + rollback ============

    private (bool Applied, string Detail) TryApplyFix(IReadOnlyList<AeroCode.Harness.Patch.Patch> patches, string rootDir, PhaseTrace phase)
    {
        var absPaths = patches.Select(p => Path.Combine(rootDir, p.FilePath)).ToList();
        var snapshot = FileSnapshotStore.Capture(absPaths);

        PatchResult result;
        try
        {
            result = _patchEngine.ApplyBatch(patches.Select(p => (p.FilePath, p)).ToList(), rootDir);
        }
        catch (Exception ex)
        {
            snapshot.Rollback();
            _logger?.LogWarning("[DEGRADED] PatchEngine threw while applying a fix; snapshot rolled back: {Error}", ex.Message);
            phase.Notes.Add($"snapshot rollback after PatchEngine exception: {ex.Message}");
            return (false, $"PatchEngine threw, snapshot rolled back: {ex.Message}");
        }

        if (result.Failed > 0 || result.Applied == 0)
        {
            snapshot.Rollback();
            var errors = string.Join("; ", result.Errors);
            phase.Notes.Add($"snapshot rollback: applied={result.Applied}, failed={result.Failed} ({errors})");
            return (false, $"fix not fully applied (applied={result.Applied}, failed={result.Failed}: {errors}); snapshot rolled back.");
        }

        snapshot.Commit();
        var fileCount = patches.Select(p => p.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        return (true, $"applied {result.Applied} patch(es) across {fileCount} file(s).");
    }

    // ============ helpers ============

    private static PhaseTrace StartPhase(string name) => new() { Name = name, StartedAtUtc = DateTime.UtcNow };

    private static void EndPhase(PhaseTrace phase) =>
        phase.DurationMs = (long)(DateTime.UtcNow - phase.StartedAtUtc).TotalMilliseconds;

    private static string AugmentGoal(string goal, int round, string? lastBuildError, GateReport? gate, ArenaResult? arena)
    {
        var sb = new StringBuilder(goal);
        sb.AppendLine();
        sb.AppendLine($"Context: round {round - 1} did not succeed. Re-plan with a revised approach.");
        if (lastBuildError is not null) sb.AppendLine($"Last build error: {Truncate(lastBuildError, 300)}");
        if (gate is not null)
        {
            sb.AppendLine($"Gate verdict: {gate.Overall}.");
            foreach (var c in gate.Criteria.Where(c => c.Verdict != GateVerdict.Pass))
                sb.AppendLine($"  - [{c.Verdict}] {c.CriterionId}: {Truncate(c.Reason, 200)}");
        }
        if (arena is not null) sb.AppendLine($"Review verdict: {arena.FinalVerdict} ({arena.TerminationReason})");
        return sb.ToString();
    }

    private static string BuildReviewSubject(string goal, Plan? plan, LoopResult build, string? lastBuildError, GateReport gate)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"GOAL: {goal}");
        if (plan is not null)
        {
            sb.AppendLine("PLAN:");
            foreach (var s in plan.Steps) sb.AppendLine($"  - {s.Id}: {s.Title}");
        }
        sb.AppendLine(build.Succeeded
            ? "BUILD: succeeded."
            : $"BUILD: failed. Last error: {Truncate(lastBuildError ?? "(unknown)", 300)}");
        sb.AppendLine($"GATE: overall={gate.Overall}");
        foreach (var c in gate.Criteria)
            sb.AppendLine($"  - [{c.Verdict}] {c.CriterionId}: {Truncate(c.Reason, 200)}");
        var evidencePaths = gate.AcceptedEvidence.Select(e => e.Path).ToList();
        if (evidencePaths.Count > 0)
            sb.AppendLine("EVIDENCE: " + string.Join("; ", evidencePaths));
        return sb.ToString();
    }

    private EngineeringLoopResult Finalize(
        LoopTrace trace,
        string tracePath,
        bool succeeded,
        LoopTerminationReason reason,
        string detail,
        GateReport? gate = null,
        ArenaResult? arena = null)
    {
        trace.Succeeded = succeeded;
        trace.TerminationReason = reason.ToString();
        trace.FinishedAtUtc = DateTime.UtcNow;
        if (gate is not null) trace.FinalGateVerdict ??= gate.Overall.ToString();
        if (arena is not null) trace.FinalReviewVerdict ??= arena.FinalVerdict.ToString();
        try
        {
            LoopTraceSerializer.Write(trace, tracePath);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("[DEGRADED] Could not persist loop trace to '{Path}': {Error}", tracePath, ex.Message);
        }

        return new EngineeringLoopResult
        {
            Succeeded = succeeded,
            TerminationReason = reason,
            TerminationDetail = detail,
            RoundsExecuted = trace.Rounds.Count,
            FinalGateReport = gate,
            FinalReviewResult = arena,
            TracePath = tracePath,
            Trace = trace,
        };
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
