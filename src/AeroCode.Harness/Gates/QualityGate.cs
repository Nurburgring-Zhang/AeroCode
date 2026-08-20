// Copyright (c) AeroCode V3.0
// QualityGate — contract-based acceptance gating with evidence enforcement.
//
// The AcceptanceContract is defined BEFORE execution (criteria + required evidence kinds).
// An independent evaluator verifies each criterion against REAL evidence artifacts (files on
// disk). Verdicts are three-state: Pass / Fail / Inconclusive. A criterion without real,
// existing evidence is Inconclusive and can NEVER be counted as Pass. Signal conflicts are
// resolved by source priority: Execution > Test > StaticAnalysis > LlmReview.
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace AeroCode.Harness.Gates;

/// <summary>Three-state verdict produced by the gate (and per criterion).</summary>
public enum GateVerdict
{
    /// <summary>Criterion/contract verified against real evidence.</summary>
    Pass,
    /// <summary>Criterion/contract positively disproven — the loop must Fix or terminate, never continue as-is.</summary>
    Fail,
    /// <summary>Cannot be decided (missing evidence, missing signals, evaluator error). Never counted as Pass.</summary>
    Inconclusive,
}

/// <summary>Kind of an evidence artifact. Maps to the contract's per-criterion evidence requirements.</summary>
public enum EvidenceKind
{
    /// <summary>Log of an actual execution (build log, run log, build-error dump).</summary>
    ExecutionLog,
    /// <summary>Test runner output (trx / stdout capture).</summary>
    TestOutput,
    /// <summary>Static analysis result (linter/analyzer report).</summary>
    StaticAnalysis,
    /// <summary>LLM review transcript.</summary>
    LlmReview,
    /// <summary>Produced build artifact (binary, generated file).</summary>
    BuildArtifact,
}

/// <summary>Source of a quality signal. Determines conflict-resolution priority.</summary>
public enum SignalSource
{
    /// <summary>Direct execution result — highest priority.</summary>
    Execution,
    /// <summary>Test result.</summary>
    Test,
    /// <summary>Static check.</summary>
    StaticAnalysis,
    /// <summary>LLM review — lowest priority.</summary>
    LlmReview,
}

/// <summary>
/// A single quality signal extracted from an evidence artifact.
/// Signals from higher-priority sources override lower-priority ones on conflict.
/// </summary>
public sealed record QualitySignal
{
    /// <summary>Which source produced this signal (drives priority ordering).</summary>
    public required SignalSource Source { get; init; }

    /// <summary>True if the signal indicates the criterion is satisfied; false indicates violation.</summary>
    public required bool IndicatesSuccess { get; init; }

    /// <summary>Human-readable detail (e.g. the failing test name, the analyzer rule id).</summary>
    public string Detail { get; init; } = string.Empty;

    /// <summary>Optional criterion id this signal applies to; null means it applies to every criterion.</summary>
    public string? CriterionId { get; init; }

    /// <summary>Numeric priority used for conflict resolution (higher wins).</summary>
    public int Priority => Source switch
    {
        SignalSource.Execution => 4,
        SignalSource.Test => 3,
        SignalSource.StaticAnalysis => 2,
        SignalSource.LlmReview => 1,
        _ => 0,
    };
}

/// <summary>
/// A real evidence artifact: a file on disk plus the quality signals derived from it.
/// Artifacts whose file does not exist are discarded by the gate — claimed evidence that
/// is not on disk is treated as missing evidence (Inconclusive), never as Pass.
/// </summary>
public sealed record EvidenceArtifact
{
    /// <summary>What kind of evidence this artifact is.</summary>
    public required EvidenceKind Kind { get; init; }

    /// <summary>Absolute path to the evidence file. Must exist on disk to be accepted.</summary>
    public required string Path { get; init; }

    /// <summary>Short human-readable summary of the artifact content.</summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>UTC timestamp when the artifact was produced.</summary>
    public DateTime ProducedAtUtc { get; init; } = DateTime.UtcNow;

    /// <summary>Quality signals carried by this artifact.</summary>
    public IReadOnlyList<QualitySignal> Signals { get; init; } = Array.Empty<QualitySignal>();

    /// <summary>Optional criterion id this artifact exclusively supports; null means shared evidence.</summary>
    public string? CriterionId { get; init; }

    /// <summary>True if the evidence file actually exists on disk right now.</summary>
    public bool ExistsOnDisk() => File.Exists(Path);
}

/// <summary>
/// One acceptance criterion of the contract, defined before execution.
/// Each criterion declares which evidence kinds must back it; without real evidence of at
/// least one required kind the criterion is Inconclusive regardless of any signal.
/// </summary>
public sealed class AcceptanceCriterion
{
    /// <summary>Unique id of the criterion (referenced by signals/artifacts).</summary>
    public required string Id { get; init; }

    /// <summary>What must hold for the criterion to pass.</summary>
    public required string Description { get; init; }

    /// <summary>
    /// Evidence kinds required to back this criterion. At least one artifact of one of
    /// these kinds must exist on disk, otherwise the verdict is Inconclusive.
    /// </summary>
    public required IReadOnlyList<EvidenceKind> RequiredEvidence { get; init; }

    /// <summary>
    /// Optional custom evaluator. When set, it decides Pass/Fail/Inconclusive from the
    /// existing evidence (the evidence-presence rule above still applies first).
    /// </summary>
    public Func<IReadOnlyList<EvidenceArtifact>, CancellationToken, Task<CriterionOutcome>>? Evaluator { get; init; }
}

/// <summary>Outcome of evaluating a single criterion.</summary>
public sealed record CriterionOutcome(GateVerdict Verdict, string Reason);

/// <summary>
/// The acceptance contract: the full list of criteria that must hold, defined BEFORE the
/// engineering loop executes. Immutable once created.
/// </summary>
public sealed class AcceptanceContract
{
    /// <summary>Contract name (used in report file names).</summary>
    public required string Name { get; init; }

    /// <summary>The criteria; must contain at least one entry.</summary>
    public required IReadOnlyList<AcceptanceCriterion> Criteria { get; init; }

    /// <summary>When the contract was defined (must precede execution).</summary>
    public DateTime DefinedAtUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>Per-criterion evaluation result included in the gate report.</summary>
public sealed record CriterionResult(
    string CriterionId,
    string Description,
    GateVerdict Verdict,
    string Reason,
    IReadOnlyList<string> EvidencePaths);

/// <summary>
/// Full gate evaluation report. Serialized to disk as JSON when a report directory is
/// given — the report file itself is a real artifact that downstream stages can cite.
/// </summary>
public sealed class GateReport
{
    /// <summary>Name of the evaluated contract.</summary>
    public required string ContractName { get; init; }

    /// <summary>Overall three-state verdict.</summary>
    public required GateVerdict Overall { get; init; }

    /// <summary>Per-criterion results.</summary>
    public required IReadOnlyList<CriterionResult> Criteria { get; init; }

    /// <summary>Evidence artifacts that were accepted (existing on disk) during evaluation.</summary>
    public required IReadOnlyList<EvidenceArtifact> AcceptedEvidence { get; init; }

    /// <summary>Evidence artifacts that were rejected (file missing on disk).</summary>
    public required IReadOnlyList<EvidenceArtifact> RejectedEvidence { get; init; }

    /// <summary>UTC evaluation timestamp.</summary>
    public DateTime EvaluatedAtUtc { get; init; } = DateTime.UtcNow;

    /// <summary>Path of the serialized JSON report, if one was written.</summary>
    public string? ReportPath { get; init; }
}

/// <summary>
/// Independent criterion evaluator. The default implementation resolves conflicting
/// signals by source priority (Execution &gt; Test &gt; StaticAnalysis &gt; LlmReview).
/// </summary>
public interface IContractEvaluator
{
    /// <summary>
    /// Evaluate one criterion against evidence that has already been verified to exist on
    /// disk. Implementations must return a three-state verdict with a reason.
    /// </summary>
    Task<CriterionOutcome> EvaluateCriterionAsync(
        AcceptanceCriterion criterion,
        IReadOnlyList<EvidenceArtifact> existingEvidence,
        CancellationToken ct);
}

/// <summary>
/// Default evaluator: resolves quality signals by source priority.
/// Rule: only the highest-priority source level present decides the verdict — a failure
/// signal at that level cannot be overridden by pass signals from lower levels.
/// No signals at all → Inconclusive.
/// </summary>
public sealed class SignalPriorityEvaluator : IContractEvaluator
{
    /// <inheritdoc />
    public Task<CriterionOutcome> EvaluateCriterionAsync(
        AcceptanceCriterion criterion,
        IReadOnlyList<EvidenceArtifact> existingEvidence,
        CancellationToken ct)
    {
        var signals = existingEvidence
            .SelectMany(e => e.Signals)
            .Where(s => s.CriterionId is null || s.CriterionId == criterion.Id)
            .ToList();

        if (signals.Count == 0)
        {
            return Task.FromResult(new CriterionOutcome(
                GateVerdict.Inconclusive,
                $"No quality signals attached to existing evidence for criterion '{criterion.Id}'."));
        }

        var topPriority = signals.Max(s => s.Priority);
        var deciding = signals.Where(s => s.Priority == topPriority).ToList();
        var failures = deciding.Where(s => !s.IndicatesSuccess).ToList();

        if (failures.Count > 0)
        {
            var detail = string.Join(" | ", failures.Select(f => $"{f.Source}: {f.Detail}"));
            return Task.FromResult(new CriterionOutcome(
                GateVerdict.Fail,
                $"Highest-priority signal source(s) report failure ({detail}). Lower-priority signals cannot override."));
        }

        var passes = deciding.Where(s => s.IndicatesSuccess).ToList();
        var passDetail = string.Join(" | ", passes.Select(p => $"{p.Source}: {p.Detail}"));
        return Task.FromResult(new CriterionOutcome(
            GateVerdict.Pass,
            $"All deciding signals (priority level {topPriority}) indicate success ({passDetail})."));
    }
}

/// <summary>
/// QualityGate — contract-based acceptance.
/// Pipeline per evaluation:
///   1. Partition evidence into existing-on-disk vs rejected (claimed but missing).
///   2. Per criterion: enforce evidence presence (missing → Inconclusive), then delegate
///      to the criterion's custom evaluator or the injected independent evaluator.
///   3. Aggregate: any Fail → Fail; else any Inconclusive → Inconclusive; else Pass.
///   4. Serialize the report to disk when a report directory is provided.
/// </summary>
public sealed class QualityGate
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IContractEvaluator _evaluator;
    private readonly ILogger? _logger;

    /// <param name="evaluator">Independent evaluator; defaults to <see cref="SignalPriorityEvaluator"/>.</param>
    /// <param name="logger">Optional logger.</param>
    public QualityGate(IContractEvaluator? evaluator = null, ILogger? logger = null)
    {
        _evaluator = evaluator ?? new SignalPriorityEvaluator();
        _logger = logger;
    }

    /// <summary>The evaluator used for criteria without a custom evaluator.</summary>
    public IContractEvaluator Evaluator => _evaluator;

    /// <summary>
    /// Evaluate the contract against the provided evidence.
    /// </summary>
    /// <param name="contract">The acceptance contract (defined before execution).</param>
    /// <param name="evidence">Evidence artifacts claimed by the executed work.</param>
    /// <param name="reportDirectory">When non-null, the JSON report is written here.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The full gate report including the overall three-state verdict.</returns>
    /// <exception cref="ArgumentException">Contract is null/empty or has duplicate criterion ids.</exception>
    public async Task<GateReport> EvaluateAsync(
        AcceptanceContract contract,
        IReadOnlyList<EvidenceArtifact> evidence,
        string? reportDirectory = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(evidence);
        if (contract.Criteria.Count == 0)
            throw new ArgumentException("AcceptanceContract must contain at least one criterion.", nameof(contract));
        var ids = contract.Criteria.Select(c => c.Id).ToList();
        if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Count)
            throw new ArgumentException("AcceptanceContract criterion ids must be unique.", nameof(contract));

        // 1. Evidence partition: only files that really exist count.
        var accepted = evidence.Where(e => e.ExistsOnDisk()).ToList();
        var rejected = evidence.Where(e => !e.ExistsOnDisk()).ToList();
        foreach (var r in rejected)
        {
            _logger?.LogWarning(
                "[DEGRADED] QualityGate rejected claimed evidence '{Path}' (kind={Kind}): file does not exist on disk.",
                r.Path, r.Kind);
        }

        // 2. Per-criterion evaluation.
        var results = new List<CriterionResult>(contract.Criteria.Count);
        foreach (var criterion in contract.Criteria)
        {
            ct.ThrowIfCancellationRequested();
            var matching = accepted
                .Where(e => e.CriterionId is null || e.CriterionId == criterion.Id)
                .ToList();

            // Evidence enforcement: required kind must be present among existing artifacts.
            var hasRequiredEvidence = matching.Any(e => criterion.RequiredEvidence.Contains(e.Kind));
            if (!hasRequiredEvidence)
            {
                var wanted = string.Join(", ", criterion.RequiredEvidence);
                results.Add(new CriterionResult(
                    criterion.Id,
                    criterion.Description,
                    GateVerdict.Inconclusive,
                    $"Missing required evidence (one of: {wanted}) — cannot be counted as Pass.",
                    matching.Select(e => e.Path).ToList()));
                continue;
            }

            CriterionOutcome outcome;
            if (criterion.Evaluator is not null)
            {
                try
                {
                    outcome = await criterion.Evaluator(matching, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    outcome = new CriterionOutcome(
                        GateVerdict.Inconclusive,
                        $"Custom evaluator threw: {ex.Message}");
                }
            }
            else
            {
                outcome = await _evaluator.EvaluateCriterionAsync(criterion, matching, ct);
            }

            results.Add(new CriterionResult(
                criterion.Id,
                criterion.Description,
                outcome.Verdict,
                outcome.Reason,
                matching.Select(e => e.Path).ToList()));
        }

        // 3. Aggregate.
        var overall = results.Any(r => r.Verdict == GateVerdict.Fail)
            ? GateVerdict.Fail
            : results.Any(r => r.Verdict == GateVerdict.Inconclusive)
                ? GateVerdict.Inconclusive
                : GateVerdict.Pass;

        var report = new GateReport
        {
            ContractName = contract.Name,
            Overall = overall,
            Criteria = results,
            AcceptedEvidence = accepted,
            RejectedEvidence = rejected,
        };

        // 4. Persist the report as a real artifact.
        if (reportDirectory is not null)
        {
            try
            {
                Directory.CreateDirectory(reportDirectory);
                var safeName = string.Concat(contract.Name.Select(ch => char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' ? ch : '_'));
                var path = Path.Combine(reportDirectory, $"gate-report-{safeName}-{DateTime.UtcNow:yyyyMMddHHmmssfff}.json");
                File.WriteAllText(path, JsonSerializer.Serialize(report, JsonOpts));
                report = new GateReport
                {
                    ContractName = report.ContractName,
                    Overall = report.Overall,
                    Criteria = report.Criteria,
                    AcceptedEvidence = report.AcceptedEvidence,
                    RejectedEvidence = report.RejectedEvidence,
                    EvaluatedAtUtc = report.EvaluatedAtUtc,
                    ReportPath = path,
                };
            }
            catch (Exception ex)
            {
                _logger?.LogWarning("[DEGRADED] QualityGate could not persist report to '{Dir}': {Error}", reportDirectory, ex.Message);
            }
        }

        _logger?.LogInformation("QualityGate '{Contract}' evaluated: overall={Verdict} ({Pass} pass / {Fail} fail / {Inc} inconclusive).",
            contract.Name, overall,
            results.Count(r => r.Verdict == GateVerdict.Pass),
            results.Count(r => r.Verdict == GateVerdict.Fail),
            results.Count(r => r.Verdict == GateVerdict.Inconclusive));

        return report;
    }
}
