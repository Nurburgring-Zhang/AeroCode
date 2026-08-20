// QualityGate tests — contract-first acceptance, three-state verdicts, evidence
// enforcement and signal-source priority (Execution > Test > Static > LlmReview).
using AeroCode.Harness.Gates;
using Xunit;

namespace AeroCode.Tests.HarnessTests;

public sealed class QualityGateTests : IDisposable
{
    private readonly string _tempRoot;

    public QualityGateTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "aerocode-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, true); } catch { }
    }

    private string WriteEvidence(string name, string content)
    {
        var path = Path.Combine(_tempRoot, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static AcceptanceContract Contract(params AcceptanceCriterion[] criteria) =>
        new() { Name = "test-contract", Criteria = criteria };

    private static AcceptanceCriterion Criterion(string id, params EvidenceKind[] kinds) =>
        new() { Id = id, Description = $"criterion {id}", RequiredEvidence = kinds };

    [Fact]
    public async Task MissingEvidence_CriterionIsInconclusive_NeverPass()
    {
        var gate = new QualityGate();
        var report = await gate.EvaluateAsync(
            Contract(Criterion("build-ok", EvidenceKind.ExecutionLog)),
            Array.Empty<EvidenceArtifact>());

        Assert.Equal(GateVerdict.Inconclusive, report.Overall);
        Assert.Equal(GateVerdict.Inconclusive, report.Criteria[0].Verdict);
        Assert.Contains("Missing required evidence", report.Criteria[0].Reason);
    }

    [Fact]
    public async Task ClaimedEvidenceNotOnDisk_IsRejected_NotCounted()
    {
        var gate = new QualityGate();
        var phantom = new EvidenceArtifact
        {
            Kind = EvidenceKind.ExecutionLog,
            Path = Path.Combine(_tempRoot, "does-not-exist.log"),
            CriterionId = "build-ok",
            Signals = new[] { new QualitySignal { Source = SignalSource.Execution, IndicatesSuccess = true } },
        };

        var report = await gate.EvaluateAsync(Contract(Criterion("build-ok", EvidenceKind.ExecutionLog)), new[] { phantom });

        Assert.Equal(GateVerdict.Inconclusive, report.Overall);
        Assert.Single(report.RejectedEvidence);
        Assert.Empty(report.AcceptedEvidence);
    }

    [Fact]
    public async Task RealEvidenceWithSuccessSignals_Passes()
    {
        var gate = new QualityGate();
        var log = WriteEvidence("build.log", "Build succeeded.");
        var artifact = new EvidenceArtifact
        {
            Kind = EvidenceKind.ExecutionLog,
            Path = log,
            CriterionId = "build-ok",
            Signals = new[]
            {
                new QualitySignal { Source = SignalSource.Execution, IndicatesSuccess = true, Detail = "exit 0" },
            },
        };

        var report = await gate.EvaluateAsync(Contract(Criterion("build-ok", EvidenceKind.ExecutionLog)), new[] { artifact });

        Assert.Equal(GateVerdict.Pass, report.Overall);
        Assert.Single(report.AcceptedEvidence);
    }

    [Fact]
    public async Task SignalPriority_ExecutionFailure_OverridesLlmReviewSuccess()
    {
        var gate = new QualityGate();
        var log = WriteEvidence("run.log", "crash");
        var artifact = new EvidenceArtifact
        {
            Kind = EvidenceKind.ExecutionLog,
            Path = log,
            CriterionId = "c1",
            Signals = new[]
            {
                new QualitySignal { Source = SignalSource.LlmReview, IndicatesSuccess = true, Detail = "looks fine" },
                new QualitySignal { Source = SignalSource.Execution, IndicatesSuccess = false, Detail = "exit 1" },
            },
        };

        var report = await gate.EvaluateAsync(Contract(Criterion("c1", EvidenceKind.ExecutionLog)), new[] { artifact });

        Assert.Equal(GateVerdict.Fail, report.Overall);
        Assert.Contains("Highest-priority", report.Criteria[0].Reason);
    }

    [Fact]
    public async Task SignalPriority_TestFailure_OverridesStaticSuccess_ButNotViceVersa()
    {
        var gate = new QualityGate();
        var log = WriteEvidence("t.log", "tests");
        var failing = new EvidenceArtifact
        {
            Kind = EvidenceKind.TestOutput,
            Path = log,
            CriterionId = "c1",
            Signals = new[]
            {
                new QualitySignal { Source = SignalSource.StaticAnalysis, IndicatesSuccess = true },
                new QualitySignal { Source = SignalSource.Test, IndicatesSuccess = false, Detail = "3 tests failed" },
            },
        };
        var reportFail = await gate.EvaluateAsync(Contract(Criterion("c1", EvidenceKind.TestOutput)), new[] { failing });
        Assert.Equal(GateVerdict.Fail, reportFail.Overall);

        var passing = new EvidenceArtifact
        {
            Kind = EvidenceKind.TestOutput,
            Path = log,
            CriterionId = "c1",
            Signals = new[]
            {
                new QualitySignal { Source = SignalSource.StaticAnalysis, IndicatesSuccess = false },
                new QualitySignal { Source = SignalSource.Test, IndicatesSuccess = true },
            },
        };
        var reportPass = await gate.EvaluateAsync(Contract(Criterion("c1", EvidenceKind.TestOutput)), new[] { passing });
        Assert.Equal(GateVerdict.Pass, reportPass.Overall);
    }

    [Fact]
    public async Task NoSignals_OnExistingEvidence_IsInconclusive()
    {
        var gate = new QualityGate();
        var log = WriteEvidence("silent.log", "nothing");
        var artifact = new EvidenceArtifact { Kind = EvidenceKind.ExecutionLog, Path = log, CriterionId = "c1" };

        var report = await gate.EvaluateAsync(Contract(Criterion("c1", EvidenceKind.ExecutionLog)), new[] { artifact });

        Assert.Equal(GateVerdict.Inconclusive, report.Overall);
    }

    [Fact]
    public async Task AnyCriterionFail_OverallFails_FailureBlocks()
    {
        var gate = new QualityGate();
        var okLog = WriteEvidence("ok.log", "ok");
        var badLog = WriteEvidence("bad.log", "bad");
        var ok = new EvidenceArtifact
        {
            Kind = EvidenceKind.ExecutionLog, Path = okLog, CriterionId = "ok",
            Signals = new[] { new QualitySignal { Source = SignalSource.Execution, IndicatesSuccess = true } },
        };
        var bad = new EvidenceArtifact
        {
            Kind = EvidenceKind.ExecutionLog, Path = badLog, CriterionId = "bad",
            Signals = new[] { new QualitySignal { Source = SignalSource.Execution, IndicatesSuccess = false } },
        };

        var report = await gate.EvaluateAsync(
            Contract(Criterion("ok", EvidenceKind.ExecutionLog), Criterion("bad", EvidenceKind.ExecutionLog)),
            new[] { ok, bad });

        Assert.Equal(GateVerdict.Fail, report.Overall);
    }

    [Fact]
    public async Task CustomEvaluator_IsHonored_WhenEvidencePresent()
    {
        var gate = new QualityGate();
        var log = WriteEvidence("custom.log", "42");
        var criterion = new AcceptanceCriterion
        {
            Id = "custom",
            Description = "content must contain 42",
            RequiredEvidence = new[] { EvidenceKind.ExecutionLog },
            Evaluator = (evidence, _) =>
            {
                var text = File.ReadAllText(evidence[0].Path);
                return Task.FromResult(text.Contains("42")
                    ? new CriterionOutcome(GateVerdict.Pass, "found 42")
                    : new CriterionOutcome(GateVerdict.Fail, "42 missing"));
            },
        };

        var report = await gate.EvaluateAsync(
            new AcceptanceContract { Name = "custom-contract", Criteria = new[] { criterion } },
            new[] { new EvidenceArtifact { Kind = EvidenceKind.ExecutionLog, Path = log, CriterionId = "custom" } });

        Assert.Equal(GateVerdict.Pass, report.Overall);
        Assert.Equal("found 42", report.Criteria[0].Reason);
    }

    [Fact]
    public async Task CustomEvaluatorStillRequiresEvidence()
    {
        var gate = new QualityGate();
        var criterion = new AcceptanceCriterion
        {
            Id = "needs-evidence",
            Description = "must have evidence regardless of evaluator",
            RequiredEvidence = new[] { EvidenceKind.TestOutput },
            Evaluator = (_, _) => Task.FromResult(new CriterionOutcome(GateVerdict.Pass, "should never run")),
        };

        var report = await gate.EvaluateAsync(
            new AcceptanceContract { Name = "c", Criteria = new[] { criterion } },
            Array.Empty<EvidenceArtifact>());

        Assert.Equal(GateVerdict.Inconclusive, report.Overall);
    }

    [Fact]
    public async Task Report_IsSerializedToDisk_AsRealArtifact()
    {
        var gate = new QualityGate();
        var log = WriteEvidence("r.log", "ok");
        var artifact = new EvidenceArtifact
        {
            Kind = EvidenceKind.ExecutionLog, Path = log, CriterionId = "c1",
            Signals = new[] { new QualitySignal { Source = SignalSource.Execution, IndicatesSuccess = true } },
        };

        var reportDir = Path.Combine(_tempRoot, "reports");
        var report = await gate.EvaluateAsync(
            Contract(Criterion("c1", EvidenceKind.ExecutionLog)), new[] { artifact }, reportDir);

        Assert.NotNull(report.ReportPath);
        Assert.True(File.Exists(report.ReportPath));
        var json = File.ReadAllText(report.ReportPath!);
        Assert.Contains("Pass", json);
    }

    [Fact]
    public async Task DuplicateCriterionIds_Throw()
    {
        var gate = new QualityGate();
        await Assert.ThrowsAsync<ArgumentException>(() => gate.EvaluateAsync(
            Contract(Criterion("dup", EvidenceKind.ExecutionLog), Criterion("dup", EvidenceKind.TestOutput)),
            Array.Empty<EvidenceArtifact>()));
    }

    [Fact]
    public async Task EmptyContract_Throws()
    {
        var gate = new QualityGate();
        await Assert.ThrowsAsync<ArgumentException>(() => gate.EvaluateAsync(
            new AcceptanceContract { Name = "empty", Criteria = Array.Empty<AcceptanceCriterion>() },
            Array.Empty<EvidenceArtifact>()));
    }
}
