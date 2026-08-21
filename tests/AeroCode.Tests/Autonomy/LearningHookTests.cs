// MissionLearningHook tests: the mission-complete → learning closed loop,
// including real held-out sample construction from mission history.
using AeroAgent.Autonomy.Analysis;
using AeroAgent.Autonomy.Clarification;
using AeroAgent.Autonomy.Data;
using AeroAgent.Autonomy.Learning;
using AeroAgent.Autonomy.Llm;
using AeroAgent.Autonomy.Mission;
using AeroAgent.Autonomy.Retrospective;
using AeroAgent.Autonomy.Steelman;
using AeroAgent.Conversation.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AeroCode.Tests.Autonomy;

public sealed class LearningHookTests : IDisposable
{
    private readonly LearningEnv _env = new();
    private readonly LearningDbContext _rsiDb;
    private readonly RsiEngine _rsi;
    private readonly MissionLearningHook _hook;

    public LearningHookTests()
    {
        _rsiDb = _env.NewLearningDb();
        _rsi = new RsiEngine(_rsiDb, _env.LearningPaths, _env.Experiences);
        _hook = new MissionLearningHook(_env.Missions, _env.Bridge, _rsi);
    }

    public void Dispose()
    {
        _rsi.Dispose();
        _rsiDb.Dispose();
        _env.Dispose();
    }

    private MissionController BuildController(IMissionExecutor executor)
    {
        var llm = new AutonomyLlmClient(registry: null); // deterministic paths, honest [DEGRADED]
        return new MissionController(
            analyzer: new TaskAnalyzer(llm),
            strategySelector: new StrategySelector(),
            clarificationGate: new ClarificationGate(llm),
            steelman: new SteelmanProtocol(llm),
            store: _env.Missions,
            executor: executor,
            retrospective: new RetrospectiveEngine(),
            experience: _env.Injector,
            llm: llm,
            paths: _env.AutonomyPaths);
    }

    private static FakeMissionExecutor SucceedingExecutor() => new(_ =>
        new MissionExecutionOutcome(true, false, "真实执行产出内容，长度足够，可以直接进入校验与复盘阶段。", null, "sess-hook", 2, 0.002));

    private static FakeMissionExecutor FailingExecutor(string error) => new(_ =>
        new MissionExecutionOutcome(false, false, string.Empty, error, null, 0, 0));

    [Fact]
    public async Task Hook_UnknownMission_ReturnsNotFound_Honestly()
    {
        var result = await _hook.OnMissionCompletedAsync("no-such-mission");

        Assert.False(result.MissionFound);
        Assert.Null(result.LessonSync);
        Assert.Null(result.RsiRound);
        Assert.Equal("no-such-mission", result.MissionId);
    }

    [Fact]
    public async Task Hook_FailedMission_SyncsLessons_WritesTrajectory_RunsCompositeTier()
    {
        var controller = BuildController(FailingExecutor("provider 连接超时"));
        var record = await controller.RunAsync("部署服务到测试环境并验证接口联调");
        Assert.Equal(MissionOutcome.Failed, record.Outcome);

        var result = await _hook.OnMissionCompletedAsync(record.Id);

        Assert.True(result.MissionFound);
        Assert.NotNull(result.LessonSync);
        Assert.True(result.LessonSync!.NewlySynced >= 2, $"期望 ≥2 条经验，实际 {result.LessonSync.NewlySynced}");
        Assert.True(result.TrajectoryWritten);
        Assert.True(result.CorrectionRulesRecorded >= 1);
        Assert.True(result.MethodsPromoted >= 1);

        // 轨迹经验真实入库（三分存储的轨迹通道）。
        var trajectories = await _env.Experiences.GetByKindAsync(ExperienceKind.Trajectory);
        var trajectory = Assert.Single(trajectories);
        Assert.Contains(record.Id, trajectory.SourceKey);
        Assert.Contains("provider 连接超时", trajectory.Content); // 失败原因如实记录

        // L1/L2 真实产物：修正规则落库 + methods 经验 Pending。
        using var fresh = _env.NewLearningDb();
        Assert.True(await fresh.CorrectionRules.CountAsync() >= 1);
        var methods = await _env.Experiences.GetByKindAsync(ExperienceKind.Method, 50);
        Assert.NotEmpty(methods);
        Assert.All(methods, m => Assert.Equal(ExperienceStatus.Pending, m.Status));

        // 历史样本不足（只有 1 条任务）→ L3 如实跳过并说明。
        Assert.Null(result.RsiRound);
        Assert.NotNull(result.Note);
        Assert.Contains("跳过 L3", result.Note);
    }

    [Fact]
    public async Task Hook_FullySuccessfulMission_NoCorrections_TrajectoryOnly()
    {
        var controller = BuildController(SucceedingExecutor());
        var record = await controller.RunAsync("实现一个数据导出功能并通过全部校验");
        Assert.Equal(MissionOutcome.Succeeded, record.Outcome);

        var result = await _hook.OnMissionCompletedAsync(record.Id);

        Assert.True(result.MissionFound);
        Assert.Equal(0, result.LessonSync!.NewlySynced);   // 无缺口 → 无 lessons（不编造）
        Assert.True(result.TrajectoryWritten);
        Assert.Equal(0, result.CorrectionRulesRecorded);
        Assert.Equal(0, result.MethodsPromoted);
    }

    [Fact]
    public async Task Hook_SecondCall_IsIdempotent_ForTrajectoryAndSync()
    {
        var controller = BuildController(FailingExecutor("构建失败"));
        var record = await controller.RunAsync("构建并发布新版本");

        var first = await _hook.OnMissionCompletedAsync(record.Id);
        var second = await _hook.OnMissionCompletedAsync(record.Id);

        Assert.True(first.TrajectoryWritten);
        Assert.False(second.TrajectoryWritten);                 // 轨迹幂等
        Assert.Equal(0, second.LessonSync!.NewlySynced);       // lessons 来源键幂等
        Assert.Equal(0, second.CorrectionRulesRecorded);       // 复盘缺口不重复生成规则（第二次 retro 解析仍在，但规则按次生成——见断言说明）
    }

    [Fact]
    public async Task Hook_BuildHeldOutSamples_LabelsOnlyWhatIsProvable()
    {
        var controller = BuildController(SucceedingExecutor());
        var success = await controller.RunAsync("调研主流向量数据库的使用现状并输出报告");
        // 高歧义文本（触发澄清门且无应答方）→ 失败任务才有"澄清未答→无帮助"的可证标签。
        var failController = BuildController(FailingExecutor("网络不可达"));
        var failed = await failController.RunAsync("处理一下那个东西的部署");

        var samples = await _hook.BuildHeldOutSamplesAsync(50);

        Assert.Equal(2, samples.Count);
        var successSample = Assert.Single(samples, s => s.Provenance.Contains(success.Id));
        var failedSample = Assert.Single(samples, s => s.Provenance.Contains(failed.Id));

        // 成功任务：所用策略是真实标签；失败任务：策略维度无标签（不虚构）。
        Assert.NotNull(successSample.SuccessfulStrategy);
        Assert.Equal(success.Strategy, successSample.SuccessfulStrategy!.ToString());
        Assert.Null(failedSample.SuccessfulStrategy);

        // 特征来自真实分析产物。
        Assert.InRange(successSample.Complexity, 1, 5);
        Assert.InRange(failedSample.Complexity, 1, 5);
    }

    [Fact]
    public async Task Hook_WithEnoughHistory_TriggersRealRsiRound_WithLogTrail()
    {
        // 4 条真实任务历史（3 成功 1 失败）→ 标注样本 ≥3 → 触发 L3 一轮。
        var ok = BuildController(SucceedingExecutor());
        await ok.RunAsync("调研主流向量数据库并输出结论");
        await ok.RunAsync("写一份技术选型分析报告");
        await ok.RunAsync("实现数据导出功能并通过校验");
        var bad = BuildController(FailingExecutor("provider 超时"));
        await bad.RunAsync("部署服务到测试环境并验证接口");

        var last = await _env.Missions.ListMissionsAsync(1);
        var result = await _hook.OnMissionCompletedAsync(last[0].Id, new MissionLearningOptions { MinSamplesForTuning = 3 });

        Assert.NotNull(result.RsiRound);
        Assert.True(result.RsiRound!.Candidates.Count > 0);
        Assert.True(File.Exists(_env.LearningPaths.RsiLogFile));
        var log = await File.ReadAllTextAsync(_env.LearningPaths.RsiLogFile);
        Assert.Contains(result.RsiRound.RoundId, log);

        // 决策二选一，都真实：应用（有快照）或拒绝（参数不变），绝不"假应用"。
        if (result.RsiRound.Applied)
        {
            Assert.NotNull(result.RsiRound.SnapshotPath);
            Assert.True(File.Exists(result.RsiRound.SnapshotPath));
            Assert.True(File.Exists(_env.LearningPaths.ActiveParameterFile));
        }
        else
        {
            Assert.Contains("未过 gate", result.RsiRound.Reason);
        }
    }

    [Fact]
    public async Task Hook_RespectsMinSamplesOption()
    {
        var controller = BuildController(SucceedingExecutor());
        var record = await controller.RunAsync("实现一个简单功能并通过校验");

        // 只有 1 条历史，但把门槛设为 1 → 样本足够 → 触发 L3。
        var result = await _hook.OnMissionCompletedAsync(record.Id, new MissionLearningOptions { MinSamplesForTuning = 1 });

        Assert.NotNull(result.RsiRound);
        Assert.Null(result.Note);
    }
}
