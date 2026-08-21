// ClusterScheduler tests — including the three P6-T1 acceptance gates:
//   GATE 1: 3 experts, 3 dependency-free branches, scripted IExpertExecutor with real
//           delays → all succeed, real concurrency observed, run record JSON on disk.
//   GATE 2: 4 branches (A/B/C independent + D depends on A), A's primary attempt
//           crashes → B/C keep running, idle expert(s) swarm A (AttemptKind=Swarm),
//           run record reflects everything honestly.
//   GATE 3: FanOutCount=2 race with a deterministic judge → ballot carries exactly the
//           successful candidates, winner output becomes the branch output,
//           ClusterResolution=FanOut.
// Plus option validation, timeout/cancellation isolation, dependency handling, swarm
// outcomes, judge degradation and memory injection. All IO uses temp directories.
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using AeroAgent.Autonomy.Cluster;
using AeroAgent.Autonomy.Data;
using Xunit;

namespace AeroCode.Tests.Autonomy.Cluster;

public sealed class ClusterSchedulerTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _root;
    private readonly AutonomyDataPaths _paths;
    private readonly ExpertPool _pool;
    private readonly string _runDir;

    public ClusterSchedulerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "aerocode-cluster-sched-" + Guid.NewGuid().ToString("N"));
        _paths = new AutonomyDataPaths(_root);
        _paths.EnsureDirectories();
        _pool = new ExpertPool(_paths);
        _runDir = Path.Combine(_root, "run-records");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort temp cleanup; never fail a test on cleanup.
        }
    }

    private static ClusterBranchSpec Branch(
        string id, string task, string[]? deps = null, int fanOut = 1, FanOutJudge? judge = null) => new()
    {
        Id = id,
        TaskText = task,
        DependsOn = deps ?? Array.Empty<string>(),
        FanOutCount = fanOut,
        Judge = judge,
    };

    private ClusterSchedulerOptions Options(
        TimeSpan? attemptTimeout = null,
        bool enableSwarm = true,
        int maxSwarmExperts = 3,
        TimeSpan? settleGrace = null,
        int maxMemoryEntries = 10,
        FanOutJudge? defaultJudge = null,
        string? runRecordDirectory = null) => new()
        {
            AttemptTimeout = attemptTimeout ?? TimeSpan.FromSeconds(30),
            EnableSwarm = enableSwarm,
            MaxSwarmExperts = maxSwarmExperts,
            SwarmSettleGrace = settleGrace ?? TimeSpan.FromSeconds(1),
            MaxMemoryEntriesInjected = maxMemoryEntries,
            DefaultJudge = defaultJudge,
            RunRecordDirectory = runRecordDirectory ?? _runDir,
        };

    private static ClusterRunRecord ReloadPersisted(ClusterRunRecord run)
    {
        Assert.NotNull(run.PersistedPath);
        Assert.True(File.Exists(run.PersistedPath), $"run record missing on disk: {run.PersistedPath}");
        var reloaded = JsonSerializer.Deserialize<ClusterRunRecord>(File.ReadAllText(run.PersistedPath!), JsonOpts);
        Assert.NotNull(reloaded);
        return reloaded!;
    }

    private static ClusterBranchRecord BranchOf(ClusterRunRecord run, string id) =>
        run.Branches.Single(b => b.BranchId == id);

    // ================= GATE 1 — 3 experts parallel async E2E =================

    [Fact]
    public async Task Gate1_ThreeExperts_ThreeBranches_RunInParallel_AndPersistRecord()
    {
        var e1 = _pool.RegisterExpert("后端工程师");
        var e2 = _pool.RegisterExpert("测试工程师");
        var e3 = _pool.RegisterExpert("文档工程师");
        var executor = new ScriptedExpertExecutor()
            .When("a", ScriptedExpertExecutor.SucceedAfter("output-a", 150))
            .When("b", ScriptedExpertExecutor.SucceedAfter("output-b", 200))
            .When("c", ScriptedExpertExecutor.SucceedAfter("output-c", 170));
        var scheduler = new ClusterScheduler(_pool, executor, Options());
        var plan = ClusterPlan.FromBranches(new[]
        {
            Branch("a", "任务A：实现接口"),
            Branch("b", "任务B：编写测试"),
            Branch("c", "任务C：撰写文档"),
        });

        var run = await scheduler.RunAsync(plan);

        // All three branches succeeded with their scripted outputs.
        Assert.Equal(ClusterRunStatus.Succeeded, run.Status);
        Assert.Equal("output-a", BranchOf(run, "a").Output);
        Assert.Equal("output-b", BranchOf(run, "b").Output);
        Assert.Equal("output-c", BranchOf(run, "c").Output);
        Assert.All(run.Branches, b =>
        {
            Assert.Equal(ClusterBranchStatus.Succeeded, b.Status);
            Assert.Equal(ClusterResolution.Primary, b.ResolvedBy);
            Assert.True(b.DurationMs >= 100, $"branch {b.BranchId} should reflect the real simulated work time");
            var assignment = Assert.Single(b.Assignments);
            Assert.Equal(ExpertAttemptKind.Primary, assignment.Kind);
            Assert.Equal(ExpertAttemptResult.Succeeded, assignment.Result);
        });

        // Real concurrency happened: at some instant >= 2 attempts were in flight
        // simultaneously. 证据取自执行轨迹本身（确定性），而非墙钟总耗时——
        // 若三分支串行执行，相邻 attempt 的启动间隔必然 ≥ 最短分支时长 150ms；
        // 并行时所有 attempt 几乎同时启动。墙钟总耗时受机器负载影响，不作断言。
        Assert.True(executor.MaxActiveObserved >= 2,
            $"expected overlapping execution, max simultaneous attempts observed: {executor.MaxActiveObserved}");
        Assert.True(run.MaxConcurrencyObserved >= 2,
            $"scheduler concurrency counter should observe overlap, got {run.MaxConcurrencyObserved}");
        const int minBranchDurationMs = 150;
        var startTicks = executor.ExecutionTrace.Select(t => t.StartedTicks).OrderBy(t => t).ToList();
        var maxStartGapMs = startTicks.Zip(startTicks.Skip(1), (a, b) => (b - a) / TimeSpan.TicksPerMillisecond).Max();
        Assert.True(maxStartGapMs < minBranchDurationMs,
            $"attempts appear to run serially: max start-to-start gap {maxStartGapMs}ms ≥ shortest branch duration {minBranchDurationMs}ms");
        Assert.Equal(3, executor.ExecutionTrace.Count);
        Assert.Equal(3, run.ExpertCount);

        // Three distinct experts were leased (one per branch, in parallel).
        var usedExperts = executor.ReceivedContexts.Select(c => c.ExpertId).Distinct().ToHashSet();
        Assert.Equal(new[] { e1.Id, e2.Id, e3.Id }.ToHashSet(), usedExperts);

        // Every attempt received the correct context.
        foreach (var (id, task) in new[] { ("a", "任务A：实现接口"), ("b", "任务B：编写测试"), ("c", "任务C：撰写文档") })
        {
            var ctx = executor.ReceivedContexts.Single(c => c.NodeId == id);
            Assert.Equal(task, ctx.TaskText);
            Assert.Equal(id, ctx.NodeId);
            Assert.Equal(ExpertAttemptKind.Primary, ctx.AttemptKind);
            Assert.Equal(string.Empty, ctx.MemorySnapshot); // fresh experts have no memory yet
        }

        // The run record JSON really exists on disk and contains every branch trace.
        var reloaded = ReloadPersisted(run);
        Assert.Equal(run.RunId, reloaded.RunId);
        Assert.Equal(ClusterRunStatus.Succeeded, reloaded.Status);
        Assert.Equal(3, reloaded.Branches.Count);
        foreach (var id in new[] { "a", "b", "c" })
        {
            var branch = BranchOf(reloaded, id);
            Assert.Equal(ClusterBranchStatus.Succeeded, branch.Status);
            Assert.Equal($"output-{id}", branch.Output);
            Assert.Equal(ExpertAttemptResult.Succeeded, branch.Assignments.Single().Result);
        }

        var raw = File.ReadAllText(run.PersistedPath!);
        Assert.Contains(run.RunId, raw);
        Assert.Contains("\"Status\": \"Succeeded\"", raw);
        Assert.Contains("\"ResolvedBy\": \"Primary\"", raw);
    }

    // ================= GATE 2 — stuck node, others continue, idle-expert swarm =================

    [Fact]
    public async Task Gate2_StuckNode_OthersContinue_IdleExpertsSwarm_RecordIsHonest()
    {
        _pool.RegisterExpert("专家甲");
        _pool.RegisterExpert("专家乙");
        _pool.RegisterExpert("专家丙");

        // Coordination: A's primary only crashes once B and C demonstrably hold experts,
        // so the swarm is mounted while B/C are still mid-flight.
        var bStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var executor = new ScriptedExpertExecutor();
        executor.When("a", async (ctx, ct) =>
        {
            if (ctx.AttemptKind == ExpertAttemptKind.Primary)
            {
                await Task.WhenAll(bStarted.Task, cStarted.Task);
                await Task.Delay(50, ct);
                return ClusterOutcomes.Fail("primary exploded");
            }

            // Swarm (会战) attempt by the idle expert.
            await Task.Delay(150, ct);
            return ClusterOutcomes.Ok("rescued-by-swarm");
        });
        executor.When("b", async (_, ct) =>
        {
            bStarted.TrySetResult();
            await Task.Delay(400, ct);
            return ClusterOutcomes.Ok("output-b");
        });
        executor.When("c", async (_, ct) =>
        {
            cStarted.TrySetResult();
            await Task.Delay(400, ct);
            return ClusterOutcomes.Ok("output-c");
        });
        executor.When("d", ScriptedExpertExecutor.SucceedAfter("output-d", 40));

        var scheduler = new ClusterScheduler(_pool, executor, Options(maxSwarmExperts: 3));
        var plan = ClusterPlan.FromBranches(new[]
        {
            Branch("a", "任务A（会卡住）"),
            Branch("b", "任务B（不受影响）"),
            Branch("c", "任务C（不受影响）"),
            Branch("d", "任务D（依赖A）", deps: new[] { "a" }),
        });

        var run = await scheduler.RunAsync(plan);

        // B and C completed normally despite A being stuck the whole time.
        Assert.Equal(ClusterBranchStatus.Succeeded, BranchOf(run, "b").Status);
        Assert.Equal("output-b", BranchOf(run, "b").Output);
        Assert.Equal(ClusterResolution.Primary, BranchOf(run, "b").ResolvedBy);
        Assert.Equal(ClusterBranchStatus.Succeeded, BranchOf(run, "c").Status);
        Assert.Equal("output-c", BranchOf(run, "c").Output);

        // A was rescued by a swarm: primary failed, a Swarm attempt succeeded.
        var a = BranchOf(run, "a");
        Assert.Equal(ClusterBranchStatus.Succeeded, a.Status);
        Assert.Equal(ClusterResolution.Swarm, a.ResolvedBy);
        Assert.Equal("rescued-by-swarm", a.Output);
        Assert.Null(a.Error);
        var primary = a.Assignments.Single(x => x.Kind == ExpertAttemptKind.Primary);
        Assert.Equal(ExpertAttemptResult.Failed, primary.Result);
        Assert.Equal("primary exploded", primary.Error);
        var swarmAttempt = a.Assignments.Single(x => x.Kind == ExpertAttemptKind.Swarm);
        Assert.Equal(ExpertAttemptResult.Succeeded, swarmAttempt.Result);

        // The swarm itself is recorded: triggered by A's crash, first success won.
        var swarm = Assert.Single(run.Swarms);
        Assert.Equal("a", swarm.NodeId);
        Assert.Contains("primary exploded", swarm.Trigger);
        Assert.Equal(SwarmOutcome.FirstSuccess, swarm.Outcome);
        Assert.NotNull(swarm.WinningExpertId);
        // B and C were busy, so exactly the one idle expert (A's released primary) swarmed.
        var idleExpert = Assert.Single(swarm.Participants);
        Assert.Equal(primary.ExpertId, idleExpert);
        Assert.Equal(swarm.WinningExpertId, idleExpert);

        // D (depends on A) ran after the rescue and succeeded.
        Assert.Equal(ClusterBranchStatus.Succeeded, BranchOf(run, "d").Status);
        Assert.Equal("output-d", BranchOf(run, "d").Output);

        Assert.Equal(ClusterRunStatus.Succeeded, run.Status);
        Assert.True(run.MaxConcurrencyObserved >= 2);

        // Persisted record mirrors the in-memory one (swarm included).
        var reloaded = ReloadPersisted(run);
        Assert.Equal(SwarmOutcome.FirstSuccess, Assert.Single(reloaded.Swarms).Outcome);
        Assert.Equal(ClusterBranchStatus.Succeeded, BranchOf(reloaded, "a").Status);
        Assert.Equal(ClusterResolution.Swarm, BranchOf(reloaded, "a").ResolvedBy);
        Assert.Equal(ClusterBranchStatus.Succeeded, BranchOf(reloaded, "d").Status);
    }

    // ================= GATE 3 — fan-out race with deterministic judge =================

    [Fact]
    public async Task Gate3_FanOutRace_JudgeGetsBallot_WinnerOutputBecomesBranchOutput()
    {
        _pool.RegisterExpert("候选专家甲");
        _pool.RegisterExpert("候选专家乙");
        _pool.RegisterExpert("候补专家");

        var judge = new RecordingFanOutJudge(b =>
            new FanOutDecision(b.Candidates[1].ExpertId, string.Empty, "deterministic pick: index 1"));
        var executor = new ScriptedExpertExecutor()
            .When("race", ScriptedExpertExecutor.SucceedAfter(ctx => $"candidate-{ctx.FanOutIndex}", 80));
        var scheduler = new ClusterScheduler(_pool, executor, Options());
        var plan = ClusterPlan.FromBranches(new[]
        {
            Branch("race", "竞赛任务：给出最优方案", fanOut: 2, judge: judge.AsDelegate()),
        });

        var run = await scheduler.RunAsync(plan);

        // The judge received exactly one ballot whose candidate count equals the number
        // of successful attempts (both succeeded here).
        Assert.Equal(1, judge.CallCount);
        var ballot = judge.ReceivedBallot!;
        Assert.Equal("race", ballot.NodeId);
        Assert.Equal(2, ballot.Candidates.Count);
        Assert.Equal(new[] { "candidate-0", "candidate-1" }, ballot.Candidates.Select(c => c.Output).ToArray());

        // Winner output becomes the branch output; resolution is FanOut.
        var branch = BranchOf(run, "race");
        Assert.Equal(ClusterBranchStatus.Succeeded, branch.Status);
        Assert.Equal(ClusterResolution.FanOut, branch.ResolvedBy);
        Assert.Equal("candidate-1", branch.Output);

        // Both attempts were dispatched as FanOut with distinct indices.
        var fanOutContexts = executor.ReceivedContexts.Where(c => c.NodeId == "race").ToList();
        Assert.Equal(2, fanOutContexts.Count);
        Assert.All(fanOutContexts, c => Assert.Equal(ExpertAttemptKind.FanOut, c.AttemptKind));
        Assert.Equal(new[] { 0, 1 }, fanOutContexts.Select(c => c.FanOutIndex).OrderBy(i => i).ToArray());
        Assert.Equal(2, fanOutContexts.Select(c => c.ExpertId).Distinct().Count());

        // The fan-out contest record is complete and honest.
        var contest = Assert.Single(run.FanOuts);
        Assert.Equal("race", contest.NodeId);
        Assert.Equal(2, contest.RequestedCandidates);
        Assert.Equal(ballot.Candidates[1].ExpertId, contest.WinnerExpertId);
        Assert.Equal("deterministic pick: index 1", contest.JudgeReason);
        Assert.False(contest.JudgeDegraded);
        Assert.Equal("candidate-1", contest.FinalOutputPreview);
        Assert.Equal(2, contest.Candidates.Count);
        Assert.All(contest.Candidates, c => Assert.True(c.Succeeded));
        Assert.Equal(2, branch.Assignments.Count);
        Assert.All(branch.Assignments, x => Assert.Equal(ExpertAttemptKind.FanOut, x.Kind));

        Assert.Equal(ClusterRunStatus.Succeeded, run.Status);
        var raw = File.ReadAllText(run.PersistedPath!);
        Assert.Contains("\"ResolvedBy\": \"FanOut\"", raw);
    }

    // ================= option & argument validation =================

    [Fact]
    public void Constructor_InvalidOptions_Throw()
    {
        var executor = new ScriptedExpertExecutor();

        Assert.Throws<ArgumentException>(() => new ClusterScheduler(
            _pool, executor, new ClusterSchedulerOptions { AttemptTimeout = TimeSpan.Zero }));
        Assert.Throws<ArgumentException>(() => new ClusterScheduler(
            _pool, executor, new ClusterSchedulerOptions { AttemptTimeout = TimeSpan.FromSeconds(-1) }));
        Assert.Throws<ArgumentException>(() => new ClusterScheduler(
            _pool, executor, new ClusterSchedulerOptions { MaxSwarmExperts = 0 }));
        Assert.Throws<ArgumentException>(() => new ClusterScheduler(
            _pool, executor, new ClusterSchedulerOptions { SwarmSettleGrace = TimeSpan.FromSeconds(-1) }));
        Assert.Throws<ArgumentException>(() => new ClusterScheduler(
            _pool, executor, new ClusterSchedulerOptions { MaxMemoryEntriesInjected = -1 }));
    }

    [Fact]
    public void Constructor_NullArguments_Throw()
    {
        var executor = new ScriptedExpertExecutor();
        Assert.Throws<ArgumentNullException>(() => new ClusterScheduler(null!, executor));
        Assert.Throws<ArgumentNullException>(() => new ClusterScheduler(_pool, null!));
    }

    [Fact]
    public async Task RunAsync_NullPlan_Throws()
    {
        _pool.RegisterExpert("专家");
        var scheduler = new ClusterScheduler(_pool, new ScriptedExpertExecutor(), Options());
        await Assert.ThrowsAsync<ArgumentNullException>(() => scheduler.RunAsync(null!));
    }

    [Fact]
    public async Task RunAsync_EmptyExpertPool_Throws()
    {
        // _pool has no experts registered in this test.
        var scheduler = new ClusterScheduler(_pool, new ScriptedExpertExecutor(), Options());
        var plan = ClusterPlan.FromBranches(new[] { Branch("a", "任务") });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => scheduler.RunAsync(plan));
        Assert.Contains("Expert pool is empty", ex.Message);
    }

    [Fact]
    public async Task RunAsync_FanOutBranchWithoutJudge_Throws()
    {
        _pool.RegisterExpert("专家甲");
        _pool.RegisterExpert("专家乙");
        var scheduler = new ClusterScheduler(_pool, new ScriptedExpertExecutor(), Options());
        var plan = ClusterPlan.FromBranches(new[] { Branch("race", "竞赛", fanOut: 2) });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => scheduler.RunAsync(plan));
        Assert.Contains("no judge is configured", ex.Message);
    }

    [Fact]
    public async Task RunAsync_FanOutCountExceedsExpertCount_Throws()
    {
        _pool.RegisterExpert("唯一的专家");
        var judge = new RecordingFanOutJudge(b => new FanOutDecision(b.Candidates[0].ExpertId, "", "x"));
        var scheduler = new ClusterScheduler(_pool, new ScriptedExpertExecutor(), Options());
        var plan = ClusterPlan.FromBranches(new[] { Branch("race", "竞赛", fanOut: 2, judge: judge.AsDelegate()) });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => scheduler.RunAsync(plan));
        Assert.Contains("deadlock", ex.Message);
    }

    // ================= timeout & cancellation isolation =================

    [Fact]
    public async Task AttemptTimeout_HangingAttempt_IsRecordedTimedOut()
    {
        _pool.RegisterExpert("慢专家");
        var executor = new ScriptedExpertExecutor()
            .When("hang", (_, ct) => ClusterOutcomes.HangUntilCancelled(ct));
        var scheduler = new ClusterScheduler(_pool, executor,
            Options(attemptTimeout: TimeSpan.FromMilliseconds(250), enableSwarm: false));
        var plan = ClusterPlan.FromBranches(new[] { Branch("hang", "会挂起的任务") });

        var sw = Stopwatch.StartNew();
        var run = await scheduler.RunAsync(plan);
        sw.Stop();

        var branch = BranchOf(run, "hang");
        Assert.Equal(ClusterBranchStatus.Failed, branch.Status);
        Assert.Null(branch.Output);
        Assert.Contains("timed out", branch.Error);
        var assignment = Assert.Single(branch.Assignments);
        Assert.Equal(ExpertAttemptResult.TimedOut, assignment.Result);
        Assert.Contains("timed out", assignment.Error);
        Assert.True(sw.ElapsedMilliseconds >= 200, "timeout should actually elapse before the branch is abandoned");
        Assert.True(sw.ElapsedMilliseconds < 10_000, "the run must not wait for the abandoned executor");
        Assert.Equal(ClusterRunStatus.Failed, run.Status);
    }

    [Fact]
    public async Task RunCancellation_BranchesRecordCancelled_RunStatusCancelled()
    {
        _pool.RegisterExpert("专家甲");
        _pool.RegisterExpert("专家乙");
        var executor = new ScriptedExpertExecutor()
            .When("slow", (_, ct) => ClusterOutcomes.HangUntilCancelled(ct))
            .When("fast", ScriptedExpertExecutor.SucceedAfter("fast-done", 60));
        var scheduler = new ClusterScheduler(_pool, executor, Options());
        var plan = ClusterPlan.FromBranches(new[] { Branch("slow", "长任务"), Branch("fast", "短任务") });

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(300);
        var run = await scheduler.RunAsync(plan, cts.Token);

        Assert.Equal(ClusterBranchStatus.Cancelled, BranchOf(run, "slow").Status);
        Assert.Equal(ClusterBranchStatus.Succeeded, BranchOf(run, "fast").Status);
        Assert.Equal("fast-done", BranchOf(run, "fast").Output);
        Assert.Equal(ClusterRunStatus.Cancelled, run.Status);
        // The run must end via cancellation, never by waiting out the 30s attempt
        // timeout of the hung branch. The bound is generous because under parallel
        // test-suite load the thread pool can delay Task.Run worker pickup — that is
        // environmental, not scheduler behavior.
        var elapsed = BranchOf(run, "slow").DurationMs;
        Assert.True(elapsed < 25_000, $"cancelled branch took {elapsed}ms, suspiciously close to the attempt timeout");
    }

    [Fact]
    public async Task PreCancelledToken_AllBranchesCancelled_NoExpertInvoked()
    {
        _pool.RegisterExpert("专家");
        var executor = new ScriptedExpertExecutor()
            .Fallback(ScriptedExpertExecutor.SucceedAfter("must-not-run", 10));
        var scheduler = new ClusterScheduler(_pool, executor, Options());
        var plan = ClusterPlan.FromBranches(new[] { Branch("a", "任务A"), Branch("b", "任务B") });

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var run = await scheduler.RunAsync(plan, cts.Token);

        Assert.Equal(ClusterRunStatus.Cancelled, run.Status);
        foreach (var id in new[] { "a", "b" })
        {
            var branch = BranchOf(run, id);
            Assert.Equal(ClusterBranchStatus.Cancelled, branch.Status);
            Assert.Contains("cancelled before start", branch.Error);
            Assert.Empty(branch.Assignments);
        }
        Assert.True(executor.ReceivedContexts.IsEmpty, "no expert attempt may run after pre-cancellation");
    }

    // ================= dependency chains =================

    [Fact]
    public async Task DependencyChain_UpstreamFailed_DownstreamSkippedAndRecorded()
    {
        _pool.RegisterExpert("专家甲");
        _pool.RegisterExpert("专家乙");
        var executor = new ScriptedExpertExecutor()
            .When("a", ScriptedExpertExecutor.FailAfter("a crashed", 30))
            .When("b", ScriptedExpertExecutor.SucceedAfter("output-b", 30));
        var scheduler = new ClusterScheduler(_pool, executor, Options(enableSwarm: false));
        var plan = ClusterPlan.FromBranches(new[]
        {
            Branch("a", "任务A"),
            Branch("b", "任务B"),
            Branch("d", "任务D依赖A", deps: new[] { "a" }),
        });

        var run = await scheduler.RunAsync(plan);

        var d = BranchOf(run, "d");
        Assert.Equal(ClusterBranchStatus.Skipped, d.Status);
        Assert.NotNull(d.Error);
        Assert.Contains("upstream dependency did not succeed", d.Error);
        Assert.Contains("a", d.Error);
        Assert.Null(d.Output);
        Assert.Empty(d.Assignments); // D never ran any expert attempt
        Assert.Equal(ClusterBranchStatus.Failed, BranchOf(run, "a").Status);
        Assert.Equal(ClusterBranchStatus.Succeeded, BranchOf(run, "b").Status);
        Assert.Equal(ClusterRunStatus.PartiallySucceeded, run.Status);

        var reloaded = ReloadPersisted(run);
        Assert.Equal(ClusterBranchStatus.Skipped, BranchOf(reloaded, "d").Status);
    }

    [Fact]
    public async Task DependencyChain_UpstreamSucceeded_DownstreamRunsAfter_AndSeesUpstreamMemory()
    {
        // One expert only: D must reuse the expert that ran A — also proves lease release.
        var expert = _pool.RegisterExpert("全才专家");
        var executor = new ScriptedExpertExecutor()
            .When("a", ScriptedExpertExecutor.SucceedAfter("output-a", 120))
            .When("d", ScriptedExpertExecutor.SucceedAfter("output-d", 20));
        var scheduler = new ClusterScheduler(_pool, executor, Options());
        var plan = ClusterPlan.FromBranches(new[]
        {
            Branch("a", "任务A"),
            Branch("d", "任务D依赖A", deps: new[] { "a" }),
        });

        var run = await scheduler.RunAsync(plan);

        var a = BranchOf(run, "a");
        var d = BranchOf(run, "d");
        Assert.Equal(ClusterBranchStatus.Succeeded, a.Status);
        Assert.Equal(ClusterBranchStatus.Succeeded, d.Status);
        Assert.Equal("output-d", d.Output);
        Assert.NotNull(a.FinishedAtUtc);
        Assert.NotNull(d.StartedAtUtc);
        Assert.True(d.StartedAtUtc >= a.FinishedAtUtc, "D must start only after A reached terminal state");

        // The scheduler wrote A's attempt into the expert's memory before D started,
        // so D's context carries that memory snapshot.
        var dContext = executor.ReceivedContexts.Single(c => c.NodeId == "d");
        Assert.Equal(expert.Id, dContext.ExpertId);
        Assert.Contains("node a attempt Primary: succeeded", dContext.MemorySnapshot);
    }

    // ================= swarm outcomes =================

    [Fact]
    public async Task SwarmDisabled_PrimaryFailure_BranchFails_WithoutSwarmRecord()
    {
        _pool.RegisterExpert("专家");
        var executor = new ScriptedExpertExecutor()
            .When("x", ScriptedExpertExecutor.FailAfter("x failed", 20));
        var scheduler = new ClusterScheduler(_pool, executor, Options(enableSwarm: false));
        var plan = ClusterPlan.FromBranches(new[] { Branch("x", "任务X") });

        var run = await scheduler.RunAsync(plan);

        Assert.Equal(ClusterBranchStatus.Failed, BranchOf(run, "x").Status);
        Assert.Empty(run.Swarms);
        Assert.Equal(ClusterRunStatus.Failed, run.Status);
    }

    [Fact]
    public async Task SwarmAllAttemptsFail_AggregatedFailure_IsRecorded()
    {
        _pool.RegisterExpert("专家甲");
        _pool.RegisterExpert("专家乙");
        var executor = new ScriptedExpertExecutor()
            .When("x", async (ctx, ct) =>
            {
                await Task.Delay(ctx.AttemptKind == ExpertAttemptKind.Primary ? 40 : 60, ct);
                return ClusterOutcomes.Fail(
                    ctx.AttemptKind == ExpertAttemptKind.Primary ? "primary exploded" : "swarm attempt failed too");
            });
        var scheduler = new ClusterScheduler(_pool, executor, Options(maxSwarmExperts: 3));
        var plan = ClusterPlan.FromBranches(new[] { Branch("x", "任务X") });

        var run = await scheduler.RunAsync(plan);

        var branch = BranchOf(run, "x");
        Assert.Equal(ClusterBranchStatus.Failed, branch.Status);
        Assert.Contains("swarm produced no successful attempt", branch.Error);

        var swarm = Assert.Single(run.Swarms);
        Assert.Equal(SwarmOutcome.AggregatedFailure, swarm.Outcome);
        Assert.Null(swarm.WinningExpertId);
        // Single branch: when the primary releases its expert, both experts are idle.
        Assert.Equal(2, swarm.Participants.Count);
        Assert.NotNull(swarm.AggregatedOutput);
        Assert.All(swarm.Participants, p => Assert.Contains(p, swarm.AggregatedOutput));
        Assert.Contains("swarm attempt failed too", swarm.AggregatedOutput);

        // Both swarm attempts are on record with Kind=Swarm and Failed result.
        var swarmAssignments = branch.Assignments.Where(x => x.Kind == ExpertAttemptKind.Swarm).ToList();
        Assert.Equal(2, swarmAssignments.Count);
        Assert.All(swarmAssignments, x => Assert.Equal(ExpertAttemptResult.Failed, x.Result));
        Assert.Equal(ClusterRunStatus.Failed, run.Status);
    }

    // ================= fan-out edge cases =================

    [Fact]
    public async Task FanOut_PartialFailure_BallotCarriesOnlySuccessfulCandidates()
    {
        _pool.RegisterExpert("专家甲");
        _pool.RegisterExpert("专家乙");
        _pool.RegisterExpert("候补");
        var judge = new RecordingFanOutJudge(b =>
            new FanOutDecision(b.Candidates[0].ExpertId, string.Empty, "only viable candidate"));
        var executor = new ScriptedExpertExecutor()
            .When("race", async (ctx, ct) =>
            {
                await Task.Delay(60, ct);
                return ctx.FanOutIndex == 0
                    ? ClusterOutcomes.Ok("good-candidate")
                    : ClusterOutcomes.Fail("candidate crashed");
            });
        var scheduler = new ClusterScheduler(_pool, executor, Options());
        var plan = ClusterPlan.FromBranches(new[] { Branch("race", "竞赛", fanOut: 2, judge: judge.AsDelegate()) });

        var run = await scheduler.RunAsync(plan);

        // Ballot candidate count == successful attempt count (1 of 2).
        var ballot = judge.ReceivedBallot!;
        var contest = Assert.Single(run.FanOuts);
        var succeededAttempts = contest.Candidates.Count(c => c.Succeeded);
        Assert.Equal(1, succeededAttempts);
        Assert.Equal(succeededAttempts, ballot.Candidates.Count);
        Assert.Equal("good-candidate", ballot.Candidates[0].Output);

        var branch = BranchOf(run, "race");
        Assert.Equal(ClusterBranchStatus.Succeeded, branch.Status);
        Assert.Equal(ClusterResolution.FanOut, branch.ResolvedBy);
        Assert.Equal("good-candidate", branch.Output);
        // Contest record keeps the failed attempt too, with its error.
        var failedRow = contest.Candidates.Single(c => !c.Succeeded);
        Assert.Equal("candidate crashed", failedRow.Error);
    }

    [Fact]
    public async Task FanOut_JudgeThrows_DegradesToFirstSuccessfulCandidate()
    {
        _pool.RegisterExpert("专家甲");
        _pool.RegisterExpert("专家乙");
        var judge = new RecordingFanOutJudge(_ => throw new InvalidOperationException("judge crashed"));
        var executor = new ScriptedExpertExecutor()
            .When("race", ScriptedExpertExecutor.SucceedAfter(ctx => $"candidate-{ctx.FanOutIndex}", 40));
        var scheduler = new ClusterScheduler(_pool, executor, Options());
        var plan = ClusterPlan.FromBranches(new[] { Branch("race", "竞赛", fanOut: 2, judge: judge.AsDelegate()) });

        var run = await scheduler.RunAsync(plan);

        var branch = BranchOf(run, "race");
        Assert.Equal(ClusterBranchStatus.Succeeded, branch.Status);
        Assert.Equal(ClusterResolution.FanOut, branch.ResolvedBy);
        Assert.Equal("candidate-0", branch.Output); // fallback: first successful candidate
        var contest = Assert.Single(run.FanOuts);
        Assert.True(contest.JudgeDegraded);
        Assert.Null(contest.JudgeReason);
        Assert.Equal(contest.Candidates.OrderBy(c => c.FanOutIndex).First().ExpertId, contest.WinnerExpertId);
    }

    [Fact]
    public async Task FanOut_JudgePicksUnknownExpert_DegradesToFirstSuccessfulCandidate()
    {
        _pool.RegisterExpert("专家甲");
        _pool.RegisterExpert("专家乙");
        var judge = new RecordingFanOutJudge(_ => new FanOutDecision("expert-ghost", "", "bad pick"));
        var executor = new ScriptedExpertExecutor()
            .When("race", ScriptedExpertExecutor.SucceedAfter(ctx => $"candidate-{ctx.FanOutIndex}", 40));
        var scheduler = new ClusterScheduler(_pool, executor, Options());
        var plan = ClusterPlan.FromBranches(new[] { Branch("race", "竞赛", fanOut: 2, judge: judge.AsDelegate()) });

        var run = await scheduler.RunAsync(plan);

        var branch = BranchOf(run, "race");
        Assert.Equal(ClusterBranchStatus.Succeeded, branch.Status);
        Assert.Equal("candidate-0", branch.Output);
        var contest = Assert.Single(run.FanOuts);
        Assert.True(contest.JudgeDegraded);
        Assert.NotEqual("expert-ghost", contest.WinnerExpertId);
    }

    [Fact]
    public async Task FanOut_AllCandidatesFail_BranchFails_JudgeNeverCalled()
    {
        _pool.RegisterExpert("专家甲");
        _pool.RegisterExpert("专家乙");
        var judge = new RecordingFanOutJudge(b => new FanOutDecision(b.Candidates[0].ExpertId, "", "x"));
        var executor = new ScriptedExpertExecutor()
            .When("race", ScriptedExpertExecutor.FailAfter("candidate exploded", 40));
        var scheduler = new ClusterScheduler(_pool, executor, Options());
        var plan = ClusterPlan.FromBranches(new[] { Branch("race", "竞赛", fanOut: 2, judge: judge.AsDelegate()) });

        var run = await scheduler.RunAsync(plan);

        var branch = BranchOf(run, "race");
        Assert.Equal(ClusterBranchStatus.Failed, branch.Status);
        Assert.Contains("fan-out produced no successful candidate", branch.Error);
        Assert.Equal(0, judge.CallCount);
        var contest = Assert.Single(run.FanOuts);
        Assert.Null(contest.WinnerExpertId);
        Assert.Equal(2, contest.Candidates.Count);
        Assert.All(contest.Candidates, c => Assert.False(c.Succeeded));
        Assert.Equal(ClusterRunStatus.Failed, run.Status);
    }

    [Fact]
    public async Task FanOut_DefaultJudgeFromOptions_IsUsedWhenBranchHasNone()
    {
        _pool.RegisterExpert("专家甲");
        _pool.RegisterExpert("专家乙");
        var judge = new RecordingFanOutJudge(b =>
            new FanOutDecision(b.Candidates[0].ExpertId, "judge-merged-output", "merged by default judge"));
        var executor = new ScriptedExpertExecutor()
            .When("race", ScriptedExpertExecutor.SucceedAfter(ctx => $"candidate-{ctx.FanOutIndex}", 40));
        var scheduler = new ClusterScheduler(_pool, executor, Options(defaultJudge: judge.AsDelegate()));
        var plan = ClusterPlan.FromBranches(new[] { Branch("race", "竞赛", fanOut: 2) });

        var run = await scheduler.RunAsync(plan);

        Assert.Equal(1, judge.CallCount);
        var branch = BranchOf(run, "race");
        Assert.Equal(ClusterResolution.FanOut, branch.ResolvedBy);
        // Non-empty FinalOutput from the judge overrides the winner's raw output.
        Assert.Equal("judge-merged-output", branch.Output);
    }

    // ================= memory injection & persistence =================

    [Fact]
    public async Task MemoryInjection_RespectsMaxEntries_AndAppendsAttemptMemoryToDisk()
    {
        var expert = _pool.RegisterExpert("记忆专家");
        _pool.AppendMemory(expert.Id, "lesson", "m-one");
        _pool.AppendMemory(expert.Id, "lesson", "m-two");
        _pool.AppendMemory(expert.Id, "lesson", "m-three");

        var executor = new ScriptedExpertExecutor()
            .When("mem", ScriptedExpertExecutor.SucceedAfter("done", 20));
        var scheduler = new ClusterScheduler(_pool, executor, Options(maxMemoryEntries: 2));
        var plan = ClusterPlan.FromBranches(new[] { Branch("mem", "记忆任务") });

        var run = await scheduler.RunAsync(plan);
        Assert.Equal(ClusterRunStatus.Succeeded, run.Status);

        // Only the 2 most recent entries were injected into the attempt context.
        var ctx = executor.ReceivedContexts.Single(c => c.NodeId == "mem");
        Assert.DoesNotContain("m-one", ctx.MemorySnapshot);
        Assert.Contains("(lesson) m-two", ctx.MemorySnapshot);
        Assert.Contains("(lesson) m-three", ctx.MemorySnapshot);

        // The scheduler appended the attempt outcome to the expert's memory.
        var memory = _pool.LoadMemory(expert.Id);
        Assert.Contains(memory, m => m.Kind == "cluster"
            && m.Content.Contains("node mem attempt Primary: succeeded")
            && m.Content.Contains("done"));

        // And that memory is really persisted: a fresh pool over the same root sees it.
        var pool2 = new ExpertPool(_paths);
        var reloadedMemory = pool2.LoadMemory(expert.Id);
        Assert.Contains(reloadedMemory, m => m.Kind == "cluster" && m.Content.Contains("node mem attempt Primary: succeeded"));
    }

    // ================= run record location & executor robustness =================

    [Fact]
    public async Task RunRecord_DefaultDirectory_IsUnderPoolClusterRuns()
    {
        _pool.RegisterExpert("专家");
        var executor = new ScriptedExpertExecutor()
            .When("a", ScriptedExpertExecutor.SucceedAfter("ok", 10));
        var scheduler = new ClusterScheduler(_pool, executor, new ClusterSchedulerOptions
        {
            AttemptTimeout = TimeSpan.FromSeconds(30),
            RunRecordDirectory = null, // default: {cluster}/runs under the pool
        });
        var plan = ClusterPlan.FromBranches(new[] { Branch("a", "任务") });

        var run = await scheduler.RunAsync(plan);

        var expectedDir = Path.Combine(_pool.ClusterDirectory, "runs");
        Assert.NotNull(run.PersistedPath);
        Assert.StartsWith(expectedDir, run.PersistedPath!, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(run.PersistedPath));
    }

    [Fact]
    public async Task ExecutorThrows_SchedulerCapturesItAsFailedAttempt()
    {
        _pool.RegisterExpert("专家");
        var executor = new ScriptedExpertExecutor()
            .When("boom", (_, _) => throw new InvalidOperationException("executor exploded"));
        var scheduler = new ClusterScheduler(_pool, executor, Options(enableSwarm: false));
        var plan = ClusterPlan.FromBranches(new[] { Branch("boom", "会炸的任务") });

        var run = await scheduler.RunAsync(plan);

        var branch = BranchOf(run, "boom");
        Assert.Equal(ClusterBranchStatus.Failed, branch.Status);
        var assignment = Assert.Single(branch.Assignments);
        Assert.Equal(ExpertAttemptResult.Failed, assignment.Result);
        Assert.Contains("executor exception", assignment.Error);
        Assert.Contains("executor exploded", assignment.Error);
        Assert.Equal(ClusterRunStatus.Failed, run.Status);
    }
}
