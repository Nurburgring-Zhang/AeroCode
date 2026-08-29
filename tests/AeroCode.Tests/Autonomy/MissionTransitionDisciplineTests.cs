// Copyright (c) AeroCode V3.0
// MissionController 状态迁移纪律测试 — 按实现真实契约编写。
// 代码事实（MissionController.cs，第一轮审计后已修复）：所有 State 变更都经 AdvanceAsync
// 单一咽喉点（含进入执行期与失败路径复盘），AdvanceAsync 内置 ValidateTransition 守卫：
// 禁止回退、禁止绕过留痕（轨迹末站必须与当前状态首尾相接）；取消路径以自迁移留痕。
using System.Text.Json;
using AeroAgent.Autonomy.Analysis;
using AeroAgent.Autonomy.Clarification;
using AeroAgent.Autonomy.Data;
using AeroAgent.Autonomy.Experience;
using AeroAgent.Autonomy.Llm;
using AeroAgent.Autonomy.Mission;
using AeroAgent.Autonomy.Retrospective;
using AeroAgent.Autonomy.Steelman;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AeroCode.Tests.Autonomy;

/// <summary>测试替身：阻塞在执行期直到外部取消——用于确定性地停在 Executing 阶段。</summary>
internal sealed class BlockingMissionExecutor : IMissionExecutor
{
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>执行器真正进入 ExecuteAsync 后置位的信号。</summary>
    public Task Entered => _entered.Task;

    public async Task<MissionExecutionOutcome> ExecuteAsync(MissionExecutionContext context, CancellationToken ct)
    {
        _entered.TrySetResult();
        // 取消时抛 TaskCanceledException（OperationCanceledException 子类）→ 触发控制器的取消路径
        await Task.Delay(Timeout.Infinite, ct);
        return new MissionExecutionOutcome(false, true, string.Empty, "unreachable", null, 0, 0);
    }
}

/// <summary>
/// 迁移纪律：轨迹终态一致、每步留痕非空、时间戳不回退；取消时状态落点停在取消处；
/// 执行异常时失败路径也必达复盘终态，且已记录的迁移链条首尾相接。
/// </summary>
public sealed class MissionTransitionDisciplineTests : IDisposable
{
    private readonly string _root;
    private readonly AutonomyDataPaths _paths;
    private readonly AutonomyDbContext _db;
    private readonly MissionStore _store;

    public MissionTransitionDisciplineTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "aerocode-mission-discipline-" + Guid.NewGuid().ToString("N"));
        _paths = new AutonomyDataPaths(_root);
        _paths.EnsureDirectories();
        _db = new AutonomyDbContext(
            new DbContextOptionsBuilder<AutonomyDbContext>().UseSqlite($"Data Source={_paths.DatabaseFile}").Options);
        _store = new MissionStore(_db);
        _store.EnsureCreatedAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _db.Dispose();
        _store.Dispose();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
    }

    private MissionController Controller(IMissionExecutor executor)
    {
        var llm = new AutonomyLlmClient(registry: null); // 确定性降级路径，诚实 [DEGRADED]
        return new MissionController(
            analyzer: new TaskAnalyzer(llm),
            strategySelector: new StrategySelector(),
            clarificationGate: new ClarificationGate(llm),
            steelman: new SteelmanProtocol(llm),
            store: _store,
            executor: executor,
            retrospective: new RetrospectiveEngine(),
            experience: new ExperienceInjector(_store),
            llm: llm,
            paths: _paths);
    }

    private static List<MissionTransition> Trail(MissionRecord record)
        => JsonSerializer.Deserialize<List<MissionTransition>>(record.TransitionsJson!)!;

    /// <summary>成功运行：轨迹首站 Received、末站与 record.State 一致、每步留痕非空、时间戳不回退。</summary>
    [Fact]
    public async Task SuccessRun_TrailTerminalConsistent_EveryStepHasArtifact()
    {
        var controller = Controller(new FakeMissionExecutor(_ =>
            new MissionExecutionOutcome(true, false, "真实执行产出内容，长度足够，可以直接进入校验与复盘阶段。", null, "sess-discipline", 2, 0.002)));

        var record = await controller.RunAsync("迁移纪律检查：完整跑一轮");

        Assert.Equal(MissionOutcome.Succeeded, record.Outcome);
        var trail = Trail(record);
        Assert.NotEmpty(trail);
        Assert.Equal(MissionState.Received, trail[0].To);              // 首站是 Received
        Assert.Equal(MissionState.ExperienceWritten, trail[^1].To);    // 轨迹终态
        Assert.Equal(record.State, trail[^1].To);                      // 最终状态与轨迹末站一致
        Assert.All(trail, t => Assert.False(string.IsNullOrWhiteSpace(t.Artifact))); // 每步留痕携带产物摘要
        for (var i = 1; i < trail.Count; i++)
        {
            Assert.True(trail[i].AtUtc >= trail[i - 1].AtUtc);         // 时间戳不回退
        }
    }

    /// <summary>执行期取消：状态落点停在取消处（不盲目前跳）→ Outcome=Cancelled，并以自迁移留痕。</summary>
    [Fact]
    public async Task CancelDuringExecution_StateLandsAtExecuting_SelfTransitionRecorded()
    {
        var executor = new BlockingMissionExecutor();
        var controller = Controller(executor);
        using var cts = new CancellationTokenSource();

        var runTask = controller.RunAsync("迁移纪律检查：中途取消", ct: cts.Token);
        await executor.Entered; // 确认真的进入 Executing 再取消
        cts.Cancel();
        var record = await runTask;

        Assert.Equal(MissionOutcome.Cancelled, record.Outcome);
        Assert.Equal(MissionState.Executing, record.State); // 落点 = 取消发生的阶段
        Assert.Equal("任务被取消", record.Error);

        var trail = Trail(record);
        var last = trail[^1];
        Assert.Equal("取消", last.Artifact);
        Assert.Equal(MissionState.Executing, last.From);
        Assert.Equal(last.From, last.To); // CancelAsync 以自迁移（state→state）留痕
    }

    /// <summary>执行期异常：失败路径也必达复盘终态（ExperienceWritten + Failed + 复盘产物），
    /// 且失败路径的状态推进同样留痕——轨迹完整、首尾相接、末站与最终 State 一致。</summary>
    [Fact]
    public async Task ExecutionThrows_FailurePath_EndsExperienceWritten_RecordedTrailChainConsistent()
    {
        var controller = Controller(new FakeMissionExecutor(_ =>
            throw new InvalidOperationException("模拟执行器故障")));

        var record = await controller.RunAsync("迁移纪律检查：执行期异常");

        Assert.Equal(MissionOutcome.Failed, record.Outcome);
        Assert.Equal(MissionState.ExperienceWritten, record.State); // 失败也必达复盘
        Assert.NotNull(record.RetrospectiveJson);
        Assert.Contains("Executing 阶段异常", record.Error);
        Assert.Contains("模拟执行器故障", record.Error);

        var trail = Trail(record);
        Assert.NotEmpty(trail);
        for (var i = 1; i < trail.Count; i++)
        {
            Assert.Equal(trail[i - 1].To, trail[i].From); // 轨迹逐条首尾相接，无断链
        }
        Assert.Equal(MissionState.Received, trail[0].To);                    // 首站 Received
        Assert.Equal(MissionState.ExperienceWritten, trail[^1].To);          // 失败路径也留痕到终态
        Assert.Equal(record.State, trail[^1].To);                            // 轨迹末站 == 最终 State
    }
}
