// EngineeringLoop E2E tests — the real Plan→Build→Verify→Review→Fix cycle running
// against real temp files, real PatchEngine application and real gate evaluation.
using AeroCode.Harness.Gates;
using AeroCode.Harness.Loop;
using AeroCode.Harness.Patch;
using AeroCode.Harness.Planner;
using AeroCode.Harness.Review;
using Xunit;

namespace AeroCode.Tests.HarnessTests;

public sealed class EngineeringLoopTests : IDisposable
{
    private readonly string _workDir;
    private readonly string _traceDir;
    private readonly string _targetFile;

    public EngineeringLoopTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "aerocode-loop-" + Guid.NewGuid().ToString("N"));
        _traceDir = Path.Combine(_workDir, "traces");
        Directory.CreateDirectory(_workDir);
        _targetFile = Path.Combine(_workDir, "target.txt");
        File.WriteAllText(_targetFile, "BROKEN");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_workDir)) Directory.Delete(_workDir, true); } catch { }
    }

    private EngineeringLoop CreateLoop(bool enableReview, int maxRounds = 5)
    {
        var options = new EngineeringLoopOptions
        {
            MaxRounds = maxRounds,
            TraceDirectory = _traceDir,
            WorkingDirectory = _workDir,
            EnableReview = enableReview,
        };
        return new EngineeringLoop(
            planner: new Planner(producer: null),          // deterministic planning (no LLM in tests)
            gate: new QualityGate(),
            arena: DualAiArena.CreateDeterministic(new DualAiArenaOptions { TranscriptDirectory = _traceDir }),
            patchEngine: new PatchEngine(),
            budget: LoopBudget.FromOptions(options),
            options: options);
    }

    private AcceptanceContract FileFixedContract() => new()
    {
        Name = "file-fixed",
        Criteria = new[]
        {
            new AcceptanceCriterion
            {
                Id = "file-fixed",
                Description = "target.txt must contain FIXED",
                RequiredEvidence = new[] { EvidenceKind.ExecutionLog },
            },
        },
    };

    /// <summary>Real evidence: reads the actual file, writes a real log, signals truthfully.</summary>
    private EvidenceProvider TruthfulEvidence() => (round, _) =>
    {
        var content = File.ReadAllText(_targetFile);
        var fixedNow = content.Contains("FIXED");
        var logPath = Path.Combine(_workDir, $"evidence-r{round}.log");
        File.WriteAllText(logPath, $"round {round}: target.txt content = {content}");
        return Task.FromResult<IReadOnlyList<EvidenceArtifact>>(new[]
        {
            new EvidenceArtifact
            {
                Kind = EvidenceKind.ExecutionLog,
                Path = logPath,
                CriterionId = "file-fixed",
                Summary = $"target content check (round {round})",
                Signals = new[]
                {
                    new QualitySignal
                    {
                        Source = SignalSource.Execution,
                        IndicatesSuccess = fixedNow,
                        Detail = fixedNow ? "content contains FIXED" : "content still BROKEN",
                        CriterionId = "file-fixed",
                    },
                },
            },
        });
    };

    private static FixProposer ReplaceBrokenWithFixed(string targetFile) => (ctx, _) =>
        Task.FromResult<IReadOnlyList<AeroCode.Harness.Patch.Patch>>(new[]
        {
            new AeroCode.Harness.Patch.Patch
            {
                FilePath = Path.GetFileName(targetFile),
                Kind = PatchKind.Replace,
                OldText = "BROKEN",
                NewText = "FIXED",
                Fuzzy = false,
                Description = "replace BROKEN with FIXED",
            },
        });

    [Fact]
    public async Task E2E_BuildRepair_AppliesRealPatch_GatePasses_LoopSucceeds()
    {
        var loop = CreateLoop(enableReview: false);

        var result = await loop.RunAsync(
            goal: "make target.txt contain FIXED",
            contract: FileFixedContract(),
            buildStep: _ =>
            {
                var ok = File.ReadAllText(_targetFile).Contains("FIXED");
                return Task.FromResult(ok ? null : "target.txt still BROKEN");
            },
            fixProposer: ReplaceBrokenWithFixed(_targetFile),
            evidenceProvider: TruthfulEvidence());

        Assert.True(result.Succeeded, result.TerminationDetail);
        Assert.Equal(LoopTerminationReason.GatePassed, result.TerminationReason);
        Assert.Equal(1, result.RoundsExecuted);
        Assert.Contains("FIXED", File.ReadAllText(_targetFile)); // real patch really applied
        Assert.NotNull(result.TracePath);
        Assert.True(File.Exists(result.TracePath));               // real trace on disk
        Assert.Contains("FIXED", File.ReadAllText(_targetFile));
    }

    [Fact]
    public async Task E2E_RoundStageFix_SecondRoundPassesGate()
    {
        var loop = CreateLoop(enableReview: false);

        var result = await loop.RunAsync(
            goal: "fix target.txt between rounds",
            contract: FileFixedContract(),
            buildStep: _ => Task.FromResult<string?>(null), // build always succeeds
            fixProposer: ReplaceBrokenWithFixed(_targetFile),
            evidenceProvider: TruthfulEvidence());

        Assert.True(result.Succeeded, result.TerminationDetail);
        Assert.Equal(2, result.RoundsExecuted); // round 1 gate fails → fix → round 2 passes
        Assert.Contains("FIXED", File.ReadAllText(_targetFile));
    }

    [Fact]
    public async Task FailureBlocking_NoFixProposer_TerminatesHonestly()
    {
        var loop = CreateLoop(enableReview: false);

        var result = await loop.RunAsync(
            goal: "impossible without fixer",
            contract: FileFixedContract(),
            buildStep: _ => Task.FromResult<string?>("always fails"),
            fixProposer: null,
            evidenceProvider: null);

        Assert.False(result.Succeeded);
        Assert.Equal(LoopTerminationReason.NoFixAvailable, result.TerminationReason);
        Assert.Contains("failure blocking", result.TerminationDetail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FailureBlocking_EmptyPatchList_TerminatesHonestly()
    {
        var loop = CreateLoop(enableReview: false);

        var result = await loop.RunAsync(
            goal: "fixer has no ideas",
            contract: FileFixedContract(),
            buildStep: _ => Task.FromResult<string?>("always fails"),
            fixProposer: (_, _) => Task.FromResult<IReadOnlyList<AeroCode.Harness.Patch.Patch>>(
                Array.Empty<AeroCode.Harness.Patch.Patch>()),
            evidenceProvider: null);

        Assert.False(result.Succeeded);
        Assert.Equal(LoopTerminationReason.NoFixAvailable, result.TerminationReason);
    }

    [Fact]
    public async Task UnapplicablePatch_RollsBackAndTerminates_FileUnchanged()
    {
        var loop = CreateLoop(enableReview: false);
        var before = File.ReadAllText(_targetFile);

        var result = await loop.RunAsync(
            goal: "patch that cannot apply",
            contract: FileFixedContract(),
            buildStep: _ => Task.FromResult<string?>("fails"),
            fixProposer: (_, _) => Task.FromResult<IReadOnlyList<AeroCode.Harness.Patch.Patch>>(new[]
            {
                new AeroCode.Harness.Patch.Patch
                {
                    FilePath = Path.GetFileName(_targetFile),
                    Kind = PatchKind.Replace,
                    OldText = "TEXT_NOT_PRESENT_ANYWHERE",
                    NewText = "x",
                    Fuzzy = false,
                },
            }),
            evidenceProvider: null);

        Assert.False(result.Succeeded);
        Assert.Equal(LoopTerminationReason.NoFixAvailable, result.TerminationReason);
        Assert.Equal(before, File.ReadAllText(_targetFile)); // rollback kept state intact
    }

    [Fact]
    public async Task MaxRounds_ReachedWithoutGatePass_TerminatesWithMaxRoundsReached()
    {
        var loop = CreateLoop(enableReview: false, maxRounds: 2);

        // A patch that applies cleanly but changes nothing truthful about the gate:
        // evidence stays failing (file never becomes FIXED), so rounds run out.
        var result = await loop.RunAsync(
            goal: "spin without progress",
            contract: FileFixedContract(),
            buildStep: _ => Task.FromResult<string?>(null),
            fixProposer: (_, _) => Task.FromResult<IReadOnlyList<AeroCode.Harness.Patch.Patch>>(new[]
            {
                new AeroCode.Harness.Patch.Patch
                {
                    FilePath = Path.GetFileName(_targetFile),
                    Kind = PatchKind.Replace,
                    OldText = "BROKEN",
                    NewText = "BROKEN", // applies cleanly, fixes nothing
                    Fuzzy = false,
                },
            }),
            evidenceProvider: TruthfulEvidence());

        Assert.False(result.Succeeded);
        Assert.Equal(LoopTerminationReason.MaxRoundsReached, result.TerminationReason);
        Assert.Equal(2, result.RoundsExecuted);
    }

    [Fact]
    public async Task ReviewEnabled_DeterministicArena_RunsAndRecordsVerdict()
    {
        var loop = CreateLoop(enableReview: true);

        var result = await loop.RunAsync(
            goal: "fix with review enabled",
            contract: FileFixedContract(),
            buildStep: _ =>
            {
                var ok = File.ReadAllText(_targetFile).Contains("FIXED");
                return Task.FromResult(ok ? null : "still BROKEN");
            },
            fixProposer: ReplaceBrokenWithFixed(_targetFile),
            evidenceProvider: TruthfulEvidence());

        // The deterministic arena must have produced a real verdict either way.
        Assert.NotNull(result.FinalReviewResult);
        Assert.True(result.Trace.Rounds.Count >= 1);
        Assert.NotNull(result.Trace.Rounds[0].Review);
    }

    [Fact]
    public async Task EmptyGoal_Throws()
    {
        var loop = CreateLoop(enableReview: false);
        await Assert.ThrowsAsync<ArgumentException>(() => loop.RunAsync(
            "", FileFixedContract(), _ => Task.FromResult<string?>(null)));
    }

    [Fact]
    public async Task EmptyContract_Throws()
    {
        var loop = CreateLoop(enableReview: false);
        await Assert.ThrowsAsync<ArgumentException>(() => loop.RunAsync(
            "goal",
            new AcceptanceContract { Name = "empty", Criteria = Array.Empty<AcceptanceCriterion>() },
            _ => Task.FromResult<string?>(null)));
    }

    [Fact]
    public async Task PreCancelled_TerminatesAsCancelled()
    {
        var loop = CreateLoop(enableReview: false);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await loop.RunAsync(
            "cancelled goal", FileFixedContract(),
            _ => Task.FromResult<string?>(null), ct: cts.Token);

        Assert.False(result.Succeeded);
        Assert.Equal(LoopTerminationReason.Cancelled, result.TerminationReason);
    }

    [Fact]
    public async Task TraceJson_ContainsGoalAndRounds()
    {
        var loop = CreateLoop(enableReview: false);
        var result = await loop.RunAsync(
            goal: "trace-check goal text",
            contract: FileFixedContract(),
            buildStep: _ =>
            {
                var ok = File.ReadAllText(_targetFile).Contains("FIXED");
                return Task.FromResult(ok ? null : "BROKEN");
            },
            fixProposer: ReplaceBrokenWithFixed(_targetFile),
            evidenceProvider: TruthfulEvidence());

        Assert.True(File.Exists(result.TracePath));
        var json = File.ReadAllText(result.TracePath!);
        Assert.Contains("trace-check goal text", json);
        Assert.Equal("trace-check goal text", result.Trace.Goal);
    }

    private EngineeringLoop CreateLoopWithBlockade(bool enableReview, AeroCode.Harness.Blockade.BlockadeResolver resolver, int maxRounds = 5)
    {
        var options = new EngineeringLoopOptions
        {
            MaxRounds = maxRounds,
            TraceDirectory = _traceDir,
            WorkingDirectory = _workDir,
            EnableReview = enableReview,
            BlockadeResolver = resolver,
        };
        return new EngineeringLoop(
            planner: new Planner(producer: null),
            gate: new QualityGate(),
            arena: DualAiArena.CreateDeterministic(new DualAiArenaOptions { TranscriptDirectory = _traceDir }),
            patchEngine: new PatchEngine(),
            budget: LoopBudget.FromOptions(options),
            options: options);
    }

    private static AeroCode.Harness.Blockade.BlockadeResolver ResolverWithHits()
    {
        var provider = new BlockadeHitsProvider(new[]
        {
            new AeroCode.Skills.Research.WebSearchResult(
                "KB: replace BROKEN with FIXED", "https://kb.example.com/fix", "apply the replacement", "blockade-hits"),
        });
        return new AeroCode.Harness.Blockade.BlockadeResolver(
            new AeroCode.Skills.Research.SearchService(new AeroCode.Skills.Research.ISearchProvider[] { provider }));
    }

    private sealed class BlockadeHitsProvider : AeroCode.Skills.Research.ISearchProvider
    {
        private readonly IReadOnlyList<AeroCode.Skills.Research.WebSearchResult> _hits;
        public BlockadeHitsProvider(IReadOnlyList<AeroCode.Skills.Research.WebSearchResult> hits) => _hits = hits;
        public string Name => "blockade-hits";
        public Task<IReadOnlyList<AeroCode.Skills.Research.WebSearchResult>> SearchAsync(string query, int maxResults, CancellationToken ct)
            => Task.FromResult(_hits.Take(maxResults).ToList() as IReadOnlyList<AeroCode.Skills.Research.WebSearchResult>);
    }

    [Fact]
    public async Task BlockadeHook_ResearchGuidedFix_ResolvesTheLoop()
    {
        var loop = CreateLoopWithBlockade(enableReview: false, ResolverWithHits());

        // fixProposer only knows how to fix AFTER the blockade hook delivered research hints —
        // proving the hints really flow from BlockadeResolver into the fix context.
        FixProposer guidedProposer = (ctx, _) =>
        {
            if (ctx.BlockadeHints.Count == 0 || ctx.Stage != "blockade")
            {
                return Task.FromResult<IReadOnlyList<AeroCode.Harness.Patch.Patch>>(
                    Array.Empty<AeroCode.Harness.Patch.Patch>());
            }

            return Task.FromResult<IReadOnlyList<AeroCode.Harness.Patch.Patch>>(new[]
            {
                new AeroCode.Harness.Patch.Patch
                {
                    FilePath = Path.GetFileName(_targetFile),
                    Kind = PatchKind.Replace,
                    OldText = "BROKEN",
                    NewText = "FIXED",
                    Fuzzy = false,
                },
            });
        };

        var result = await loop.RunAsync(
            goal: "blockade-guided fix",
            contract: FileFixedContract(),
            buildStep: _ => Task.FromResult<string?>(null), // build ok; the GATE fails on round 1 (file BROKEN)
            fixProposer: guidedProposer,
            evidenceProvider: TruthfulEvidence());

        Assert.True(result.Succeeded, result.TerminationDetail);
        Assert.Contains("FIXED", File.ReadAllText(_targetFile));
        Assert.Equal("BlockadeResolved", result.Trace.Rounds[0].Fix!.Verdict);
    }

    [Fact]
    public async Task BlockadeHook_WithoutFixProposer_ResearchRunsThenTerminatesHonestly()
    {
        var loop = CreateLoopWithBlockade(enableReview: false, ResolverWithHits());

        var result = await loop.RunAsync(
            goal: "blockade without fixer",
            contract: FileFixedContract(),
            buildStep: _ => Task.FromResult<string?>(null),
            fixProposer: null,
            evidenceProvider: TruthfulEvidence());

        Assert.False(result.Succeeded);
        Assert.Equal(LoopTerminationReason.NoFixAvailable, result.TerminationReason);
        Assert.Contains("blockade research completed", result.TerminationDetail);
        Assert.Contains("attempts", result.TerminationDetail);
    }
}

public sealed class FileSnapshotStoreTests : IDisposable
{
    private readonly string _tempRoot;

    public FileSnapshotStoreTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "aerocode-snap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, true); } catch { }
    }

    [Fact]
    public void Rollback_RestoresOriginalContent_Really()
    {
        var file = Path.Combine(_tempRoot, "f.txt");
        File.WriteAllText(file, "original");

        var snapshot = FileSnapshotStore.Capture(new[] { file });
        File.WriteAllText(file, "mutated");
        snapshot.Rollback();

        Assert.Equal("original", File.ReadAllText(file));
        Assert.False(snapshot.IsActive);
    }

    [Fact]
    public void Commit_KeepsMutation()
    {
        var file = Path.Combine(_tempRoot, "g.txt");
        File.WriteAllText(file, "original");

        var snapshot = FileSnapshotStore.Capture(new[] { file });
        File.WriteAllText(file, "mutated");
        snapshot.Commit();

        Assert.Equal("mutated", File.ReadAllText(file));
    }

    [Fact]
    public void Capture_MissingFile_RollbackDoesNotCreateIt()
    {
        var missing = Path.Combine(_tempRoot, "missing.txt");
        var snapshot = FileSnapshotStore.Capture(new[] { missing });
        File.WriteAllText(missing, "appeared");
        snapshot.Rollback();
        Assert.False(File.Exists(missing));
    }
}
