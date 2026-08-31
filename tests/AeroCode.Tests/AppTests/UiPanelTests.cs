// Copyright (c) AeroCode
// UiPanelTests — 批次 B G5 十面板的 ViewModel 级测试（≥8 条，全部真实服务 + 临时目录）：
// 权限档位切换（真实 PermissionPolicy+PlanWorkflow）、Mission VM 推进/终止/投影（真实 MissionController 链）、
// Steer 插话（真实 SteerQueue）、todo 勾选/删除（真实 TodoStore+SQLite）、会话 fork（真实 SessionService+SQLite）、
// Memory 召回/人工沉淀（真实 SessionMemoryService+ExperienceStore+SQLite）、
// Hooks 设置段（真实 HookEngine+hooks.json）、调度管理段（真实 SchedulerService+jobs.json）、
// 专家团选择（6 策略 + 网关提示诚实性）。零桩数据——落盘断言直接读真实文件/数据库。
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AeroAgent.Autonomy.Analysis;
using AeroAgent.Autonomy.Clarification;
using AeroAgent.Autonomy.Data;
using AeroAgent.Autonomy.Experience;
using AeroAgent.Autonomy.Learning;
using AeroAgent.Autonomy.Llm;
using AeroAgent.Autonomy.Mission;
using AeroAgent.Autonomy.Retrospective;
using AeroAgent.Autonomy.Steelman;
using AeroAgent.Conversation.Data;
using AeroAgent.Conversation.Models;
using AeroAgent.Conversation.Orchestration;
using AeroAgent.Conversation.Services;
using AeroAgent.Moa.Profiles;
using AeroAgent.Moa.Strategies;
using AeroCode.App.Configuration;
using AeroCode.App.Services;
using AeroCode.App.ViewModels;
using AeroCode.Harness.EventBus;
using AeroCode.Harness.Hooks;
using AeroCode.Harness.Permission;
using AeroCode.Harness.PlanMode;
using AeroCode.Harness.Scheduler;
using AeroCode.Tests.ConversationTests;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AeroCode.Tests.AppTests;

/// <summary>每个测试独占的临时 AppData 根（不触碰用户真实数据）。</summary>
internal sealed class UiPanelTempRoot : IDisposable
{
    public string Root { get; }
    public AppDataPaths Paths { get; }

    public UiPanelTempRoot(string name)
    {
        Root = Path.Combine(Path.GetTempPath(), $"{name}_{Guid.NewGuid():N}");
        Paths = new AppDataPaths(Root);
        Paths.EnsureAll();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch
        {
            // 临时目录清理失败不影响断言（个别平台文件锁）。
        }
    }
}

/// <summary>ChatViewModel 最小真实构造（G5 面板测试共享）。</summary>
internal static class UiPanelChatFactory
{
    public static ChatViewModel Create(
        ISessionService? sessions = null,
        SteerQueue? steer = null,
        ISessionFork? fork = null,
        ITodoStore? todos = null,
        PlanWorkflow? plan = null,
        PermissionPolicy? policy = null)
        => new(
            sessions ?? new NullSessionService(),
            new UnusedFacade(),
            new TestProviderRegistry(),
            new MoaOptions(),
            policy ?? new PermissionPolicy(new EventBus()),
            new InstructionLoader("uipanel-tests-appdata", null),
            workspace: null,
            planWorkflow: plan,
            steerQueue: steer,
            sessionFork: fork,
            todoStore: todos);
}

/// <summary>G5-1 权限模式下拉：档位切换驱动真实 PlanWorkflow 状态机（切出 Plan 即 Approve）。</summary>
public sealed class UiPanelPermissionModeTests : IDisposable
{
    private readonly UiPanelTempRoot _root = new("uipanel_perm");
    private readonly PermissionPolicy _policy = new(new EventBus());
    private readonly PlanWorkflow _plan;
    private readonly ChatViewModel _vm;

    public UiPanelPermissionModeTests()
    {
        _plan = new PlanWorkflow(_policy, _root.Root);
        _vm = UiPanelChatFactory.Create(plan: _plan, policy: _policy);
    }

    [Fact]
    public void SwitchToPlan_EntersWorkflow_SwitchOut_Approves()
    {
        _vm.SelectedMode = PermissionMode.Plan;

        Assert.Equal(PermissionMode.Plan, _policy.CurrentMode); // 唯一裁决源随下拉切换
        Assert.Equal(PlanState.Planning, _plan.State);
        Assert.True(File.Exists(_plan.PlanPath)); // Enter 真实创建 PLAN.md 骨架
        Assert.Equal("规划（只读 + 计划）", _vm.ModeDescription);

        _vm.SelectedMode = PermissionMode.Default;

        Assert.Equal(PlanState.Approved, _plan.State); // 切出 Plan → 真实 Approve
        Assert.Equal(PermissionMode.Default, _policy.CurrentMode);
        Assert.Equal(PermissionMode.Default, _vm.SelectedMode);
    }

    [Fact]
    public void ModeDescription_CoversAllFourModes()
    {
        _vm.SelectedMode = PermissionMode.AcceptEdits;
        Assert.Equal("自动接受文件编辑", _vm.ModeDescription);
        _vm.SelectedMode = PermissionMode.Bypass;
        Assert.Contains("跳过询问", _vm.ModeDescription, StringComparison.Ordinal);
        Assert.Equal(4, _vm.PermissionModes.Count); // Default/AcceptEdits/Plan/Bypass
    }

    public void Dispose() => _root.Dispose();
}

/// <summary>G5-6 Steer 插话输入：真实 SteerQueue 入队/满队/非流式诚实语义。</summary>
public sealed class UiPanelSteerTests
{
    [Fact]
    public void Steer_WhileStreaming_EnqueuesAndClears()
    {
        var queue = new SteerQueue();
        var vm = UiPanelChatFactory.Create(steer: queue);
        vm.SelectedSession = new SessionItemViewModel { Id = "s1" };
        vm.IsStreaming = true;
        vm.SteerInput = "先补一个回归测试再继续";

        vm.SteerCommand.Execute(null);

        Assert.Equal(new[] { "先补一个回归测试再继续" }, queue.Drain("s1"));
        Assert.Equal(string.Empty, vm.SteerInput);
    }

    [Fact]
    public void Steer_NotStreaming_IsHonestNoop()
    {
        var queue = new SteerQueue();
        var vm = UiPanelChatFactory.Create(steer: queue);
        vm.SelectedSession = new SessionItemViewModel { Id = "s1" };
        vm.SteerInput = "非流式期间不该入队";

        vm.SteerCommand.Execute(null);

        Assert.Empty(queue.Drain("s1"));
        Assert.Equal("非流式期间不该入队", vm.SteerInput);
    }

    [Fact]
    public void Steer_QueueFull_ReportsHonestFailure()
    {
        var queue = new SteerQueue(capacity: 1);
        var vm = UiPanelChatFactory.Create(steer: queue);
        vm.SelectedSession = new SessionItemViewModel { Id = "s1" };
        vm.IsStreaming = true;
        Assert.True(queue.TryEnqueue("s1", "占位")); // 队列已满（容量 1）
        vm.SteerInput = "挤不进去";

        vm.SteerCommand.Execute(null);

        Assert.Contains("队列已满", vm.StatusText, StringComparison.Ordinal);
        Assert.Equal("挤不进去", vm.SteerInput);
    }
}

/// <summary>G5-9 Todo 清单面板：真实 TodoStore + SQLite 落库（真实会话行 + 真实待办）。</summary>
public sealed class UiPanelTodoTests : IDisposable
{
    private readonly UiPanelTempRoot _root = new("uipanel_todo");
    private readonly ChatViewModel _vm;
    private readonly ITodoStore _store;
    private readonly string _sessionId;

    public UiPanelTodoTests()
    {
        var options = new DbContextOptionsBuilder<ConversationDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_root.Root, "conv.db")}")
            .Options;
        var db = new ConversationDbContext(options);
        db.Database.EnsureCreated();
        // TodoStore 校验会话存在：用真实 SessionService 建会话，再对同一会话写待办
        var sessions = new SessionService(db);
        _sessionId = sessions.CreateSessionAsync(OrchestrationStrategy.Single, null, null)
            .GetAwaiter().GetResult().Value!.Id;
        _store = new TodoStore(() => new ConversationDbContext(options));
        _vm = UiPanelChatFactory.Create(todos: _store);
        _vm.SelectedSession = new SessionItemViewModel { Id = _sessionId };
    }

    [Fact]
    public async Task Toggle_PersistsCompleted_AndRefreshesPanel()
    {
        var added = (await _store.AddAsync(_sessionId, "写出 G5 报告")).Value!;

        await _vm.ToggleTodoCommand.ExecuteAsync(new TodoItemViewModel(added));

        var onDisk = (await _store.ListAsync(_sessionId)).Value!.Single();
        Assert.True(onDisk.IsCompleted);
        Assert.True(_vm.Todos.Single().IsCompleted); // 面板经真实重读刷新
    }

    [Fact]
    public async Task Delete_RemovesFromStore()
    {
        var added = (await _store.AddAsync(_sessionId, "会被删除的一条")).Value!;

        await _vm.DeleteTodoCommand.ExecuteAsync(new TodoItemViewModel(added));

        Assert.Empty((await _store.ListAsync(_sessionId)).Value!);
        Assert.Empty(_vm.Todos);
    }

    public void Dispose() => _root.Dispose();
}

/// <summary>G5-5 会话 fork 按钮：真实 SessionService（同时是 ISessionFork）+ SQLite。</summary>
public sealed class UiPanelForkTests : IDisposable
{
    private readonly UiPanelTempRoot _root = new("uipanel_fork");

    [Fact]
    public async Task ForkSession_CreatesForkedSession_AndSelectsIt()
    {
        var options = new DbContextOptionsBuilder<ConversationDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_root.Root, "conv.db")}")
            .Options;
        var db = new ConversationDbContext(options); // SessionService 持有同一上下文，测试结束一并回收
        db.Database.EnsureCreated();
        var sessions = new SessionService(db);
        var vm = UiPanelChatFactory.Create(sessions: sessions, fork: sessions);
        var created = (await sessions.CreateSessionAsync(OrchestrationStrategy.Single, null, null)).Value!;
        vm.SelectedSession = new SessionItemViewModel { Id = created.Id, Title = created.Title, Strategy = created.Strategy };

        await vm.ForkSessionCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.Sessions.Count);
        var fork = vm.Sessions.Single(s => s.Id != created.Id);
        Assert.Contains("fork", fork.Title, StringComparison.Ordinal);
        Assert.Equal(fork.Id, vm.SelectedSession!.Id); // fork 后自动切到新会话
    }

    public void Dispose() => _root.Dispose();
}

/// <summary>G5-2 Mission 面板 VM：空目标/真实推进失败链/终态投影/终止按钮语义。</summary>
public sealed class UiPanelMissionTests : IDisposable
{
    private readonly UiPanelTempRoot _root = new("uipanel_mission");

    private static MissionViewModel MakeViewModel(UiPanelTempRoot root)
    {
        var autonomyPaths = new AutonomyDataPaths(Path.Combine(root.Root, "autonomy"));
        autonomyPaths.EnsureDirectories();
        var autonomyDb = new AutonomyDbContext(new DbContextOptionsBuilder<AutonomyDbContext>()
            .UseSqlite($"Data Source={autonomyPaths.DatabaseFile}")
            .Options);
        var missionStore = new MissionStore(autonomyDb);
        var llm = new AutonomyLlmClient(null); // 无 provider：LLM 阶段诚实失败
        return new MissionViewModel(new MissionController(
            new TaskAnalyzer(llm),
            new StrategySelector(),
            new ClarificationGate(llm),
            new SteelmanProtocol(llm),
            missionStore,
            new ThrowingExecutor(),
            new RetrospectiveEngine(),
            new ExperienceInjector(missionStore),
            llm,
            autonomyPaths,
            NullLogger<MissionController>.Instance));
    }

    [Fact]
    public async Task StartMission_EmptyGoal_ReportsHonestError()
    {
        var vm = MakeViewModel(_root);
        vm.GoalInput = "   ";

        await vm.StartMissionCommand.ExecuteAsync(null);

        Assert.Equal("目标不能为空", vm.StatusText);
        Assert.False(vm.IsRunning);
    }

    [Fact]
    public async Task StartMission_WithoutProvider_FailsHonestly()
    {
        var vm = MakeViewModel(_root);
        vm.GoalInput = "把测试套件跑绿";

        await vm.StartMissionCommand.ExecuteAsync(null);

        Assert.False(vm.IsRunning);
        // 真实失败链（控制器终态记录或异常上抛都如实呈现），绝无伪造成功。
        Assert.True(
            vm.OutcomeBadge.StartsWith("❌", StringComparison.Ordinal)
            || vm.StatusText.Contains("失败", StringComparison.Ordinal),
            $"OutcomeBadge='{vm.OutcomeBadge}' StatusText='{vm.StatusText}'");
    }

    [Fact]
    public void ProjectRecord_RendersTransitionsOutcomeAndSummary()
    {
        var vm = MakeViewModel(_root);
        var record = new MissionRecord
        {
            TaskText = "真实终态投影",
            State = MissionState.ExperienceWritten,
            Outcome = MissionOutcome.Succeeded,
            TransitionsJson = JsonSerializer.Serialize(new System.Collections.Generic.List<MissionTransition>
            {
                new(MissionState.Received, MissionState.Analyzed, DateTime.UtcNow, "类型=bugfix 策略=Single"),
            }),
            ExecutionJson = JsonSerializer.Serialize(new
            {
                SessionId = "abcdef1234567890",
                AssistantMessages = 3,
                TotalCostUsd = 0.0123,
            }),
        };

        vm.ProjectRecord(record);

        var transition = Assert.Single(vm.Transitions);
        Assert.Equal("Received", transition.From);
        Assert.Equal("Analyzed", transition.To);
        Assert.Contains("类型=bugfix", transition.Artifact, StringComparison.Ordinal);
        Assert.Contains("成功", vm.OutcomeBadge, StringComparison.Ordinal);
        Assert.Contains("助手消息 3 条", vm.ExecutionSummary, StringComparison.Ordinal);
        Assert.Contains("$0.0123", vm.ExecutionSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void StopMission_WithoutRunningJob_ReportsHonestHint()
    {
        var vm = MakeViewModel(_root);

        vm.StopMissionCommand.Execute(null);

        Assert.Contains("没有正在运行", vm.StatusText, StringComparison.Ordinal);
    }

    private sealed class ThrowingExecutor : IMissionExecutor
    {
        public Task<MissionExecutionOutcome> ExecuteAsync(MissionExecutionContext context, CancellationToken ct)
            => throw new InvalidOperationException("UI 面板测试不执行 MOA 编排");
    }

    public void Dispose() => _root.Dispose();
}

/// <summary>G5-4 Memory 面板升级：真实 SessionMemoryService + ExperienceStore（SQLite）。</summary>
public sealed class UiPanelMemoryPanelTests : IDisposable
{
    private readonly UiPanelTempRoot _root = new("uipanel_memory");
    private readonly MemoryViewModel _vm;

    public UiPanelMemoryPanelTests()
    {
        var learningPaths = new LearningDataPaths(Path.Combine(_root.Root, "learning"));
        var experience = new ExperienceStore(LearningDbContext.Create(learningPaths), learningPaths);
        experience.EnsureCreatedAsync().GetAwaiter().GetResult();
        var memory = new SessionMemoryService(_root.Paths, new MemorySettings(), experience: experience);
        _vm = new MemoryViewModel(_root.Paths, memory);
    }

    [Fact]
    public async Task ConsolidateManual_WritesFactExperience()
    {
        _vm.ManualContent = "用户偏好深色主题与简洁回复";

        await _vm.ConsolidateManualCommand.ExecuteAsync(null);

        Assert.Contains("已沉淀", _vm.StatusText, StringComparison.Ordinal);
        Assert.Equal(string.Empty, _vm.ManualContent); // 成功后清空输入
    }

    [Fact]
    public async Task ConsolidateManual_EmptyContent_SkipsHonestly()
    {
        _vm.ManualContent = "   ";

        await _vm.ConsolidateManualCommand.ExecuteAsync(null);

        Assert.Contains("内容为空", _vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Recall_WithoutNoteService_ReportsZeroHonestly()
    {
        _vm.RecallQuery = "任何查询";

        await _vm.RecallCommand.ExecuteAsync(null);

        Assert.Empty(_vm.RecalledNotes);
        Assert.Contains("召回 0 条", _vm.StatusText, StringComparison.Ordinal);
    }

    public void Dispose() => _root.Dispose();
}

/// <summary>G5-7 Hooks 设置段：真实 HookEngine + hooks.json（加载/拒载/开关语义）。</summary>
public sealed class UiPanelSettingsHooksTests : IDisposable
{
    private readonly UiPanelTempRoot _root = new("uipanel_hooks");
    private readonly SettingsViewModel _vm;
    private readonly HookEngine _engine;

    public UiPanelSettingsHooksTests()
    {
        _engine = new HookEngine(new EventBus());
        _vm = MakeViewModel();
    }

    private SettingsViewModel MakeViewModel()
    {
        var settings = new SettingsService(_root.Paths);
        settings.LoadAsync().GetAwaiter().GetResult();
        return new SettingsViewModel(
            settings,
            new ThemeService(),
            new AeroCode.AI.Providers.ProviderFactory(settings.ToAiOptions(), NullLoggerFactory.Instance),
            PermissionPolicy.CreateDefault(new EventBus()),
            new JsonPermissionStore(_root.Paths.PermissionsFile),
            new ModelProfileCatalog(new JsonFileProfileStore(_root.Paths.MoaProfilesFile)),
            new MoaOptions(),
            new JsonMoaOptionsStore(_root.Paths.MoaOptionsFile),
            NullLogger<SettingsViewModel>.Instance,
            hookEngine: _engine,
            paths: _root.Paths);
    }

    [Fact]
    public void ReloadHooks_ValidConfig_LoadsRows()
    {
        var hooksPath = Path.Combine(_root.Root, "hooks.json");
        File.WriteAllText(hooksPath,
            """[{"id":"h1","event":"SessionStartEvent","command":"echo hi","timeoutSec":10,"enabled":true}]""");
        Assert.True(_vm.HooksEnabled); // settings 默认档

        _vm.ReloadHooksCommand.Execute(null);

        var hook = Assert.Single(_vm.HookItems);
        Assert.Equal("h1", hook.Id);
        Assert.Equal("SessionStartEvent", hook.Event);
        Assert.Contains("1 条钩子", _vm.HooksStatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void ReloadHooks_BadConfig_RejectedHonestly()
    {
        File.WriteAllText(Path.Combine(_root.Root, "hooks.json"), "{ 这不是合法 JSON");

        _vm.ReloadHooksCommand.Execute(null);

        Assert.Empty(_vm.HookItems); // fail-safe 拒载，不半载
        Assert.Contains("拒载", _vm.HooksStatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void ReloadHooks_Disabled_SkipsHonestly()
    {
        _vm.HooksEnabled = false;

        _vm.ReloadHooksCommand.Execute(null);

        Assert.Contains("未启用", _vm.HooksStatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Save_PersistsHooksToggleToSettings()
    {
        _vm.HooksEnabled = false;

        await _vm.SaveCommand.ExecuteAsync(null);

        var fresh = new SettingsService(_root.Paths);
        await fresh.LoadAsync();
        Assert.False(fresh.Current.Hooks.Enabled);
    }

    public void Dispose() => _root.Dispose();
}

/// <summary>G5-8 调度管理段：真实 SchedulerService + jobs.json（增删启停即时落盘）。</summary>
public sealed class UiPanelSettingsSchedulerTests : IDisposable
{
    private readonly UiPanelTempRoot _root = new("uipanel_sched");
    private readonly string _jobsPath;
    private readonly SettingsViewModel _vm;

    public UiPanelSettingsSchedulerTests()
    {
        _jobsPath = Path.Combine(_root.Root, "jobs.json");
        var scheduler = new SchedulerService(_jobsPath);
        _vm = MakeViewModel(scheduler);
    }

    private SettingsViewModel MakeViewModel(SchedulerService scheduler)
    {
        var settings = new SettingsService(_root.Paths);
        settings.LoadAsync().GetAwaiter().GetResult();
        // 测试环境组合根从未启动轮询：把"开机自动轮询"档关掉，VM 的运行态水合才与事实一致
        settings.Current.Scheduler.Enabled = false;
        return new SettingsViewModel(
            settings,
            new ThemeService(),
            new AeroCode.AI.Providers.ProviderFactory(settings.ToAiOptions(), NullLoggerFactory.Instance),
            PermissionPolicy.CreateDefault(new EventBus()),
            new JsonPermissionStore(_root.Paths.PermissionsFile),
            new ModelProfileCatalog(new JsonFileProfileStore(_root.Paths.MoaProfilesFile)),
            new MoaOptions(),
            new JsonMoaOptionsStore(_root.Paths.MoaOptionsFile),
            NullLogger<SettingsViewModel>.Instance,
            scheduler: scheduler,
            paths: _root.Paths);
    }

    [Fact]
    public void AddJob_ValidCron_PersistsToJobsJson()
    {
        _vm.NewJobId = "j1";
        _vm.NewJobCommand = "echo hi";
        _vm.NewJobCron = "*/5 * * * *";

        _vm.AddJobCommand.Execute(null);

        Assert.Contains(_vm.JobItems, j => j.Id == "j1");
        Assert.True(File.Exists(_jobsPath)); // AddOrUpdate 即时落盘
    }

    [Fact]
    public void AddJob_BothTriggers_RejectedHonestly()
    {
        _vm.NewJobId = "j2";
        _vm.NewJobCommand = "echo hi";
        _vm.NewJobCron = "*/5 * * * *";
        _vm.NewJobAtUtcLocal = "2030-01-01 09:30";

        _vm.AddJobCommand.Execute(null);

        Assert.Contains("只能填一个", _vm.SchedulerStatusText, StringComparison.Ordinal);
        Assert.DoesNotContain(_vm.JobItems, j => j.Id == "j2");
    }

    [Fact]
    public void AddJob_NoTrigger_RejectedHonestly()
    {
        _vm.NewJobId = "j3";
        _vm.NewJobCommand = "echo hi";

        _vm.AddJobCommand.Execute(null);

        Assert.Contains("二选一", _vm.SchedulerStatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void ToggleJob_Disables_AndPersistsAcrossInstances()
    {
        _vm.NewJobId = "j4";
        _vm.NewJobCommand = "echo hi";
        _vm.NewJobCron = "0 12 * * *";
        _vm.AddJobCommand.Execute(null);

        _vm.ToggleJobCommand.Execute(_vm.JobItems.Single(j => j.Id == "j4"));

        // 新实例 Load 同一 jobs.json：停用真实持久化
        var fresh = new SchedulerService(_jobsPath);
        fresh.Load();
        Assert.False(fresh.Jobs.Single(j => j.Id == "j4").Enabled);
    }

    [Fact]
    public void RemoveJob_RemovesFromService()
    {
        _vm.NewJobId = "j5";
        _vm.NewJobCommand = "echo hi";
        _vm.NewJobCron = "0 8 * * *";
        _vm.AddJobCommand.Execute(null);

        _vm.RemoveJobCommand.Execute(_vm.JobItems.Single(j => j.Id == "j5"));

        Assert.DoesNotContain(_vm.JobItems, j => j.Id == "j5");
        // 新实例 Load 同一 jobs.json：移除真实持久化
        var fresh = new SchedulerService(_jobsPath);
        fresh.Load();
        Assert.DoesNotContain(fresh.Jobs, j => j.Id == "j5");
    }

    [Fact]
    public void StartStop_ReflectsRunningState()
    {
        Assert.False(_vm.IsSchedulerRunning);

        _vm.StartSchedulerCommand.Execute(null);
        Assert.True(_vm.IsSchedulerRunning);

        _vm.StopSchedulerCommand.Execute(null);
        Assert.False(_vm.IsSchedulerRunning);
    }

    public void Dispose() => _root.Dispose();
}

/// <summary>G5-3 专家团选择器：6 策略含 Experts + 网关提示诚实性（不声称未配置的网关可用）。</summary>
public sealed class UiPanelStrategyChoicesTests
{
    [Fact]
    public void StrategyChoices_ContainExperts_InChatAndSettings()
    {
        var chat = UiPanelChatFactory.Create();
        Assert.Equal(6, chat.Strategies.Count);
        Assert.Contains(chat.Strategies, s => s == OrchestrationStrategy.Experts);
        Assert.False(chat.IsExpertsSelected);

        chat.SelectedStrategy = OrchestrationStrategy.Experts;
        Assert.True(chat.IsExpertsSelected);
        Assert.Contains("moa-gateway-pro", chat.ExpertsGatewayHint, StringComparison.Ordinal);
    }

    [Fact]
    public void ExpertsGatewayHint_ReflectsEnvironmentHonestly()
    {
        var chat = UiPanelChatFactory.Create();
        var url = Environment.GetEnvironmentVariable("MOA_GATEWAY_URL");
        var key = Environment.GetEnvironmentVariable("MOA_GATEWAY_KEY");

        if (string.IsNullOrWhiteSpace(url) && string.IsNullOrWhiteSpace(key))
        {
            // 未配置时必须明说，绝不伪装网关就绪。
            Assert.Contains("未检测到", chat.ExpertsGatewayHint, StringComparison.Ordinal);
        }
        else
        {
            // 配置了 URL/KEY 时提示真实配置状态（含"诚实失败"边界说明）。
            Assert.Contains("诚实失败", chat.ExpertsGatewayHint, StringComparison.Ordinal);
        }
    }
}
