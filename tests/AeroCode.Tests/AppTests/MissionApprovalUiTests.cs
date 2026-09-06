// Copyright (c) AeroCode
// MissionApprovalUiTests — F-M5 审批/恢复 UI 的 VM 级真实单测（R3 β builder）：
//   审批路由：真实 HumanPauseEscalationPolicy → MissionController 受理 → MissionViewModel 事件观察
//             → 卡片命令 → controller.TryApproveEscalation（一次性凭据真实消费）；
//   掩码：理由过敏感形态脱敏器、凭据恒为「（凭据已隐藏）」，绝不回显原文（纯函数直测）；
//   禁用条件：恢复可用性探针与控制器恢复同源（ResumePlanner fail-closed，真实 checkpoint 目录）；
//   防重入：恢复执行中二次触发被拒（resumeInvoker 闸门注入，产品路径默认直连控制器零改动）。
// 全链真实服务 + 临时目录，零桩数据（恢复断言直接读真实文件回滚结果）。
using System;
using System.IO;
using System.Linq;
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
using AeroCode.App.Services;
using AeroCode.App.ViewModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AeroCode.Tests.AppTests;

/// <summary>F-M5 测试共用宿主：真实 MissionController（可选升级策略/checkpoint 存储）+ 临时 AppData 根。</summary>
internal sealed class MissionApprovalTestHost : IDisposable
{
    private readonly string _root;

    public MissionApprovalTestHost()
    {
        _root = Path.Combine(Path.GetTempPath(), "aerocode-fm5-ui", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        var paths = new AutonomyDataPaths(_root);
        paths.EnsureDirectories();
        Db = new AutonomyDbContext(new DbContextOptionsBuilder<AutonomyDbContext>()
            .UseSqlite($"Data Source={paths.DatabaseFile}")
            .Options);
        Store = new MissionStore(Db);
        Store.EnsureCreatedAsync().GetAwaiter().GetResult();
        CheckpointRoot = Path.Combine(_root, "checkpoints");
    }

    public AutonomyDbContext Db { get; }

    public MissionStore Store { get; }

    public string CheckpointRoot { get; }

    public MissionController BuildController(
        HumanPauseEscalationPolicy? policy = null,
        CheckpointStore? checkpoints = null)
    {
        var llm = new AutonomyLlmClient(registry: null); // 无 provider：LLM 阶段诚实降级，与本测试无关
        return new MissionController(
            new TaskAnalyzer(llm),
            new StrategySelector(),
            new ClarificationGate(llm),
            new SteelmanProtocol(llm),
            Store,
            new NoopExecutor(),
            new RetrospectiveEngine(),
            new ExperienceInjector(Store),
            llm,
            new AutonomyDataPaths(_root),
            NullLogger<MissionController>.Instance,
            policy,
            checkpoints);
    }

    public MissionViewModel BuildViewModel(
        MissionController controller,
        OverlayService? overlay = null,
        CheckpointStore? checkpoints = null,
        Action<Action>? marshaller = null,
        Func<long?, CancellationToken, Task<MissionResumeResult>>? resumeInvoker = null)
        => new(controller, clarificationPresenter: null, overlay, checkpoints, marshaller, resumeInvoker);

    /// <summary>经真实 HumanPauseEscalationPolicy 触发一次升级（与 WorkerRunner 循环守卫同一入口形态）。</summary>
    public void Raise(HumanPauseEscalationPolicy policy, string reason, int turn = 3, int strikes = 2)
        => policy.Handle(new EscalationContext(turn, strikes, GoalAnchor.Create("整理仓库文档")!, reason, null, 0));

    public void Dispose()
    {
        Db.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // 临时目录清理失败不影响断言（个别平台文件锁）。
        }
    }

    private sealed class NoopExecutor : IMissionExecutor
    {
        public Task<MissionExecutionOutcome> ExecuteAsync(MissionExecutionContext context, CancellationToken ct)
            => Task.FromResult(new MissionExecutionOutcome(true, false, "noop", null, context.MissionId, 0, 0));
    }
}

/// <summary>掩码纯函数：凭据永不明文回显（契约 B-APPROVAL 硬门）。</summary>
public sealed class MissionApprovalRedactionTests
{
    [Fact]
    public void MaskReason_ScrubsCredentialForms()
    {
        const string raw = "偏离原因 sk-abcdefgh1234567890 继续";

        var masked = MissionApprovalRedaction.MaskReason(raw);

        Assert.Contains("[REDACTED]", masked, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-abcdefgh1234567890", masked, StringComparison.Ordinal);
    }

    [Fact]
    public void MaskReason_Empty_ReturnsHonestPlaceholder()
    {
        Assert.Equal("（无升级理由）", MissionApprovalRedaction.MaskReason(null));
        Assert.Equal("（无升级理由）", MissionApprovalRedaction.MaskReason("   "));
    }

    [Fact]
    public void EscalationItem_CredentialNeverRaw()
    {
        const string raw = "循环偏离 token=supersecretvalue123";
        var request = new EscalationRequest("esc-abc123", 2, 3, raw, new DateTime(2030, 1, 2, 3, 4, 5, DateTimeKind.Utc));

        var item = MissionEscalationItem.From(request);

        Assert.Equal("esc-abc123", item.Id);
        Assert.Equal(2, item.Turn);
        Assert.Equal(3, item.Strikes);
        Assert.Equal(MissionApprovalRedaction.CredentialMasked, item.Credential); // 结构无掩码字段 → 恒为掩码文案
        Assert.Contains("[REDACTED]", item.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("supersecretvalue123", item.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("supersecretvalue123", item.ToString(), StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(item.RaisedAtLocal));
    }
}

/// <summary>恢复可用性探针：与控制器恢复同源（ResumePlanner fail-closed）。</summary>
public sealed class MissionResumeProbeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aerocode-fm5-probe", Guid.NewGuid().ToString("N"));

    [Fact]
    public void HasCandidate_NullOrEmptyRoot_False()
    {
        Assert.False(MissionResumeProbe.HasCandidate(null));
        Assert.False(MissionResumeProbe.HasCandidate("   "));
    }

    [Fact]
    public void HasCandidate_EmptyStore_False()
    {
        Directory.CreateDirectory(_root);

        Assert.False(MissionResumeProbe.HasCandidate(_root)); // 空目录：无有效 checkpoint → 禁用
    }

    [Fact]
    public void HasCandidate_ValidCheckpoint_True()
    {
        Directory.CreateDirectory(_root);
        var tracked = Path.Combine(_root, "a.txt");
        File.WriteAllText(tracked, "old");
        var store = new CheckpointStore(Path.Combine(_root, "checkpoints"));
        store.Track("write_file", new[] { tracked });

        Assert.True(MissionResumeProbe.HasCandidate(store.Root));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
        }
    }
}

/// <summary>单张审批卡片的决策守卫：决策只允许一次（防双击重放一次性凭据）。</summary>
public sealed class MissionEscalationCardVmTests
{
    private static MissionEscalationCardViewModel MakeCard(
        Func<string, bool>? approve = null,
        Action<MissionEscalationCardViewModel>? reject = null)
        => new(
            new MissionEscalationItem("esc-card-1", 1, 2, "理由", "（凭据已隐藏）", "01-02 03:04:05"),
            approve ?? (_ => true),
            reject ?? (_ => { }));

    [Fact]
    public void Approve_InvokesCallbackOnce_AndLocksDecision()
    {
        var calls = 0;
        var card = MakeCard(approve: _ =>
        {
            calls++;
            return true;
        });

        card.ApproveCommand.Execute(null);
        card.ApproveCommand.Execute(null); // 双击第二次

        Assert.Equal(1, calls);
        Assert.True(card.ApproveResult);
        Assert.True(card.IsDecided);
        Assert.False(card.ApproveCommand.CanExecute(null));
        Assert.False(card.RejectCommand.CanExecute(null));
    }

    [Fact]
    public void Reject_LocksDecision_ApproveBlockedAfter()
    {
        var approveCalls = 0;
        MissionEscalationCardViewModel? rejected = null;
        var card = MakeCard(
            approve: _ =>
            {
                approveCalls++;
                return true;
            },
            reject: c => rejected = c);

        card.RejectCommand.Execute(null);
        card.ApproveCommand.Execute(null); // 拒绝后再点批准：已决策，不再触发

        Assert.Same(card, rejected);
        Assert.Equal(0, approveCalls);
        Assert.True(card.IsDecided);
    }
}

/// <summary>审批路由：真实策略 → 控制器受理 → VM 事件观察 → 卡片命令 → TryApproveEscalation。</summary>
public sealed class MissionApprovalRoutingTests : IDisposable
{
    private readonly MissionApprovalTestHost _host = new();

    [Fact]
    public void EscalationRaised_RealChain_CardAppearsWithMaskedFields()
    {
        var policy = new HumanPauseEscalationPolicy();
        var controller = _host.BuildController(policy);
        var vm = _host.BuildViewModel(controller, marshaller: a => a());

        _host.Raise(policy, "偏离原因 sk-abcdefgh1234567890");

        var card = Assert.Single(vm.PendingApprovalCards);
        Assert.Equal(controller.PendingEscalations.Single().Id, card.Id);
        Assert.Contains("turn 3", card.Title, StringComparison.Ordinal);
        Assert.Contains("strikes 2", card.Title, StringComparison.Ordinal);
        Assert.Equal(MissionApprovalRedaction.CredentialMasked, card.Credential);
        Assert.Contains("[REDACTED]", card.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-abcdefgh1234567890", card.Reason, StringComparison.Ordinal);
        Assert.True(vm.HasPendingApprovals);
    }

    [Fact]
    public void Approve_ThroughCardCommand_ConsumesCredentialExactlyOnce()
    {
        var policy = new HumanPauseEscalationPolicy();
        var controller = _host.BuildController(policy);
        var vm = _host.BuildViewModel(controller, marshaller: a => a());
        _host.Raise(policy, "偏离需要人审批");
        var card = vm.PendingApprovalCards.Single();

        card.ApproveCommand.Execute(null);

        // 一次性消费：UI 批准 = 凭据真实消费，控制器队列清空、重放被拒。
        Assert.Empty(controller.PendingEscalations);
        Assert.False(controller.TryApproveEscalation(card.Id));
        Assert.Empty(vm.PendingApprovalCards);
        Assert.False(vm.HasPendingApprovals);
        Assert.Contains("已批准", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void Reject_ThroughCardCommand_DoesNotConsume()
    {
        var policy = new HumanPauseEscalationPolicy();
        var controller = _host.BuildController(policy);
        var vm = _host.BuildViewModel(controller, marshaller: a => a());
        _host.Raise(policy, "偏离待复核");
        var card = vm.PendingApprovalCards.Single();

        card.RejectCommand.Execute(null);

        // 拒绝 = 不消费凭据（控制器没有拒绝 API，语义如实）：凭据仍在待审批队列且仍可被批准。
        Assert.Single(controller.PendingEscalations);
        Assert.True(controller.TryApproveEscalation(card.Id));
        Assert.Empty(vm.PendingApprovalCards);
        Assert.Contains("已拒绝", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectThenNewEscalation_ReSeedsStillPendingCard()
    {
        var policy = new HumanPauseEscalationPolicy();
        var controller = _host.BuildController(policy);
        var vm = _host.BuildViewModel(controller, marshaller: a => a());
        _host.Raise(policy, "第一次偏离");
        var firstId = vm.PendingApprovalCards.Single().Id;
        vm.PendingApprovalCards.Single().RejectCommand.Execute(null);

        _host.Raise(policy, "第二次偏离");

        // 拒绝过的凭据未消费仍在队列：新升级触发补弹时如实重新呈现（不静默吞掉待审批项）。
        Assert.Equal(2, controller.PendingEscalations.Count);
        Assert.Equal(2, vm.PendingApprovalCards.Count);
        Assert.Contains(vm.PendingApprovalCards, c => c.Id == firstId);
    }

    [Fact]
    public void Ctor_SeedsPreExistingPendingEscalations()
    {
        var policy = new HumanPauseEscalationPolicy();
        var controller = _host.BuildController(policy);
        _host.Raise(policy, "UI 尚未启动前的受理");

        var vm = _host.BuildViewModel(controller, marshaller: a => a());

        // 种子化快照：控制器先于 UI 受理的升级在 VM 构造时即入卡队列（后续触发补弹）。
        var card = Assert.Single(vm.PendingApprovalCards);
        Assert.Equal(controller.PendingEscalations.Single().Id, card.Id);
    }

    [Fact]
    public void Escalation_MarshalledThroughUiMarshaller()
    {
        var policy = new HumanPauseEscalationPolicy();
        var controller = _host.BuildController(policy);
        var queued = new System.Collections.Generic.List<Action>();
        var vm = _host.BuildViewModel(controller, marshaller: queued.Add);

        _host.Raise(policy, "任意线程触发");

        Assert.Empty(vm.PendingApprovalCards); // 未经 UI 线程编组，不入展示队列
        Assert.Single(queued);
        foreach (var action in queued)
        {
            action(); // 模拟 UI 线程执行
        }

        Assert.Single(vm.PendingApprovalCards);
    }

    [Fact]
    public void PendingCards_WithoutOverlayHost_StayQueued()
    {
        var policy = new HumanPauseEscalationPolicy();
        var controller = _host.BuildController(policy);
        var overlay = new OverlayService(); // 宿主未挂载（HasHost=false）：诚实降级路径
        var vm = _host.BuildViewModel(controller, overlay, marshaller: a => a());

        _host.Raise(policy, "宿主未挂载也不丢失");

        Assert.False(overlay.HasHost);
        var card = Assert.Single(vm.PendingApprovalCards); // 卡片留队列，后续触发补弹
        Assert.True(vm.HasPendingApprovals);
        Assert.Equal("esc-", card.Id[..4]);
    }

    public void Dispose() => _host.Dispose();
}

/// <summary>恢复入口：真实 ResumeLatestCheckpointAsync 链路、禁用条件与防重入。</summary>
public sealed class MissionResumeUiTests : IDisposable
{
    private readonly MissionApprovalTestHost _host = new();

    [Fact]
    public void NoCheckpointStore_AvailabilityUnknown_StillReachable()
    {
        var controller = _host.BuildController();
        var vm = _host.BuildViewModel(controller);

        // DI 未注入 store = 可用性未知：不虚假禁用可达路径，点击后由控制器如实判定。
        Assert.True(vm.CanResumeCheckpoint);
        Assert.Contains("未注入", vm.ResumeHint, StringComparison.Ordinal);
        Assert.True(vm.ResumeLastCheckpointCommand.CanExecute(null));
    }

    [Fact]
    public void EmptyStore_ButtonDisabled()
    {
        var store = new CheckpointStore(_host.CheckpointRoot); // 真实空检查点目录
        var controller = _host.BuildController(checkpoints: store);
        var vm = _host.BuildViewModel(controller, checkpoints: store);

        Assert.False(vm.CanResumeCheckpoint);
        Assert.Contains("暂无可恢复的检查点", vm.ResumeHint, StringComparison.Ordinal);
        Assert.False(vm.ResumeLastCheckpointCommand.CanExecute(null)); // 无 checkpoint → 按钮禁用
    }

    [Fact]
    public async Task ValidCheckpoint_Resume_RestoresFilesForReal()
    {
        var store = new CheckpointStore(_host.CheckpointRoot);
        var tracked = Path.Combine(_host.CheckpointRoot, "ws-a.txt");
        var created = Path.Combine(_host.CheckpointRoot, "ws-b.txt");
        File.WriteAllText(tracked, "old-content"); // 捕获时已存在 → RestoreContent
        var seq = store.Track("write_file", new[] { tracked, created }); // created 捕获时不存在 → DeleteCreated
        File.WriteAllText(tracked, "new-content");
        File.WriteAllText(created, "created-later");

        var controller = _host.BuildController(checkpoints: store);
        var vm = _host.BuildViewModel(controller, checkpoints: store); // resumeInvoker 默认 = 真实控制器路径

        await vm.ResumeLastCheckpointCommand.ExecuteAsync(null);

        // 真实恢复：文件系统回滚到检查点时刻（零桩数据，直接读文件断言）。
        Assert.Equal("old-content", File.ReadAllText(tracked));
        Assert.False(File.Exists(created));
        Assert.True(vm.CanResumeCheckpoint);
        Assert.False(vm.IsResuming);
        Assert.Contains("已重放", vm.StatusText, StringComparison.Ordinal);
        Assert.Contains($"{seq}", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resume_ReentrantDuringFlight_SecondCallRejected()
    {
        var controller = _host.BuildController();
        var gate = new TaskCompletionSource<MissionResumeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var vm = _host.BuildViewModel(
            controller,
            resumeInvoker: (_, _) =>
            {
                calls++;
                return gate.Task;
            });

        var run = vm.ResumeLastCheckpointCommand.ExecuteAsync(null); // 第一次：同步段直落 await（闸门挂起）
        vm.ResumeLastCheckpointCommand.ExecuteAsync(null);           // 第二次：执行中被拒

        Assert.Equal(1, calls);
        Assert.True(vm.IsResuming);
        Assert.False(vm.ResumeLastCheckpointCommand.CanExecute(null)); // 防重入：执行中禁用

        gate.SetResult(MissionResumeResult.Failed("gate released"));
        await run;

        Assert.Equal(1, calls);
        Assert.False(vm.IsResuming);
        Assert.True(vm.ResumeLastCheckpointCommand.CanExecute(null));
    }

    [Fact]
    public async Task Resume_FailureResult_ShownHonestly()
    {
        var controller = _host.BuildController();
        var vm = _host.BuildViewModel(
            controller,
            resumeInvoker: (_, _) => Task.FromResult(MissionResumeResult.Failed("no valid checkpoint available")));

        await vm.ResumeLastCheckpointCommand.ExecuteAsync(null);

        // 控制器诚实失败结果如实上屏（不伪造恢复成功）。
        Assert.Contains("恢复未完成", vm.StatusText, StringComparison.Ordinal);
        Assert.Contains("no valid checkpoint available", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void Resume_DisabledWhileMissionRunning()
    {
        var controller = _host.BuildController();
        var vm = _host.BuildViewModel(controller);

        vm.IsRunning = true;

        Assert.False(vm.ResumeLastCheckpointCommand.CanExecute(null)); // 任务运行中不恢复（防并发写竞态）

        vm.IsRunning = false;

        Assert.True(vm.ResumeLastCheckpointCommand.CanExecute(null));
    }

    public void Dispose() => _host.Dispose();
}
