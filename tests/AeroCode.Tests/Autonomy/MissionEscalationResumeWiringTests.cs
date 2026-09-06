// Copyright (c) AeroCode
// R1 缝合窗口（#13）接线测试：MissionController 的 C-LOOP 升级受理（一次性凭据消费、
// 不自动代批、不重复消费）与 C-RESUME 恢复路径（ResumePlanner 构建计划 →
// CheckpointStore.Restore 重放；未接线/无 checkpoint 时诚实失败，不阻断、不伪造）。
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AeroAgent.Autonomy.Analysis;
using AeroAgent.Autonomy.Clarification;
using AeroAgent.Autonomy.Data;
using AeroAgent.Autonomy.Experience;
using AeroAgent.Autonomy.Llm;
using AeroAgent.Autonomy.Mission;
using AeroAgent.Autonomy.Retrospective;
using AeroAgent.Autonomy.Steelman;
using AeroAgent.Moa.LoopGuard;
using AeroAgent.Moa.Tools.Workspace;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AeroCode.Tests.Autonomy;

public sealed class MissionEscalationResumeWiringTests : IDisposable
{
    private readonly string _root;
    private readonly AutonomyDbContext _db;
    private readonly MissionStore _store;

    public MissionEscalationResumeWiringTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "aerocode-mission-stitch", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        var paths = new AutonomyDataPaths(_root);
        paths.EnsureDirectories();
        _db = new AutonomyDbContext(new DbContextOptionsBuilder<AutonomyDbContext>()
            .UseSqlite($"Data Source={paths.DatabaseFile}")
            .Options);
        _store = new MissionStore(_db);
        _store.EnsureCreatedAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_root, true); } catch { }
    }

    private MissionController BuildController(
        IEscalationPolicy? escalationPolicy = null, CheckpointStore? checkpoints = null)
    {
        var llm = new AutonomyLlmClient(registry: null); // deterministic paths, honest [DEGRADED]
        return new MissionController(
            analyzer: new TaskAnalyzer(llm),
            strategySelector: new StrategySelector(),
            clarificationGate: new ClarificationGate(llm),
            steelman: new SteelmanProtocol(llm),
            store: _store,
            executor: new NoopExecutor(),
            retrospective: new RetrospectiveEngine(),
            experience: new ExperienceInjector(_store),
            llm: llm,
            paths: new AutonomyDataPaths(_root),
            escalationPolicy: escalationPolicy,
            checkpoints: checkpoints);
    }

    private sealed class NoopExecutor : IMissionExecutor
    {
        public Task<MissionExecutionOutcome> ExecuteAsync(MissionExecutionContext context, CancellationToken ct)
            => Task.FromResult(new MissionExecutionOutcome(true, false, "noop", null, context.MissionId, 0, 0));
    }

    // ---- C-LOOP：升级凭据受理（订阅 → 记录 → 人审批一次性消费）----

    [Fact]
    public void EscalationRaised_Recorded_AndApproved_ExactlyOnce()
    {
        var policy = new HumanPauseEscalationPolicy();
        var controller = BuildController(escalationPolicy: policy);
        EscalationRequest? received = null;
        controller.EscalationReceived += r => received = r;

        var decision = policy.Handle(new EscalationContext(
            Turn: 3, Strikes: 2, GoalAnchor.Create("整理仓库文档")!, "test deviation reason", null, 0));

        Assert.Equal(EscalationDecision.PauseForHuman, decision);
        Assert.Equal(1, controller.EscalationCount);
        var pending = controller.PendingEscalations;
        Assert.Single(pending);
        Assert.NotNull(received);
        Assert.Equal(pending[0].Id, received!.Id);

        // 一次性消费：首次审批 true；重放/未知凭据诚实拒绝（不重复消费、不伪造成功）。
        Assert.True(controller.TryApproveEscalation(pending[0].Id));
        Assert.False(controller.TryApproveEscalation(pending[0].Id));
        Assert.False(controller.TryApproveEscalation("esc-unknown"));
        Assert.Empty(controller.PendingEscalations);
    }

    [Fact]
    public void SubscriberException_DoesNotBreakEscalationIntake()
    {
        var policy = new HumanPauseEscalationPolicy();
        var controller = BuildController(escalationPolicy: policy);
        controller.EscalationReceived += _ => throw new InvalidOperationException("ui blew up");

        policy.Handle(new EscalationContext(0, 1, GoalAnchor.Create("goal")!, "reason", null, 0));

        Assert.Equal(1, controller.EscalationCount);
        Assert.Single(controller.PendingEscalations);
    }

    // ---- C-RESUME：恢复路径（构建计划 → 重放；fail-closed，失败诚实可见）----

    [Fact]
    public async Task Resume_WithoutCheckpointStore_ReturnsHonestFailure()
    {
        var controller = BuildController();

        var result = await controller.ResumeLatestCheckpointAsync();

        Assert.False(result.Succeeded);
        Assert.Null(result.CheckpointSeq);
        Assert.Equal(0, result.RestoredFiles);
        Assert.Contains("not wired", result.Error);
    }

    [Fact]
    public async Task Resume_WithWiredButEmptyStore_ReturnsNoValidCheckpoint()
    {
        var controller = BuildController(
            checkpoints: new CheckpointStore(Path.Combine(_root, "checkpoints")));

        var result = await controller.ResumeLatestCheckpointAsync();

        Assert.False(result.Succeeded);
        Assert.Equal("no valid checkpoint available", result.Error);
    }

    [Fact]
    public async Task Resume_WithValidCheckpoint_BuildsPlanAndReplaysRestore()
    {
        var workspaceDir = Path.Combine(_root, "ws");
        Directory.CreateDirectory(workspaceDir);
        var tracked = Path.Combine(workspaceDir, "a.txt");
        var created = Path.Combine(workspaceDir, "b.txt");
        File.WriteAllText(tracked, "old-content"); // 捕获时已存在 → RestoreContent
        var store = new CheckpointStore(Path.Combine(_root, "checkpoints"));
        var seq = store.Track("write_file", new[] { tracked, created }); // created 捕获时不存在 → DeleteCreated
        File.WriteAllText(tracked, "new-content");
        File.WriteAllText(created, "created-later");

        var controller = BuildController(checkpoints: store);
        var result = await controller.ResumeLatestCheckpointAsync();

        Assert.True(result.Succeeded, result.Error ?? "resume failed");
        Assert.Equal(seq, result.CheckpointSeq);
        Assert.Equal(2, result.RestoredFiles);
        Assert.Equal(2, result.PlannedActions);
        Assert.Equal("old-content", File.ReadAllText(tracked)); // 重放 = 回滚到捕获时内容
        Assert.False(File.Exists(created)); // 新建回滚语义：捕获时不存在 → 删除
        Assert.Contains($"checkpoint {seq}", result.Summary);
    }
}
