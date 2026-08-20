// MissionController full-chain E2E + MoaMissionExecutor tests.
// Test doubles are legitimate hand-written implementations of the real contracts
// (IMissionExecutor / ISessionService / IChatOrchestrationFacade) — no mocking library.
using AeroAgent.Autonomy.Analysis;
using AeroAgent.Autonomy.Clarification;
using AeroAgent.Autonomy.Data;
using AeroAgent.Autonomy.Experience;
using AeroAgent.Autonomy.Llm;
using AeroAgent.Autonomy.Mission;
using AeroAgent.Autonomy.Retrospective;
using AeroAgent.Autonomy.Steelman;
using AeroAgent.Conversation.Models;
using AeroAgent.Conversation.Orchestration;
using AeroAgent.Conversation.Services;
using AeroCode.Core.Common;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AeroCode.Tests.Autonomy;

/// <summary>Test double: scripted mission executor recording what it received.</summary>
internal sealed class FakeMissionExecutor : IMissionExecutor
{
    private readonly Func<MissionExecutionContext, MissionExecutionOutcome> _script;
    public MissionExecutionContext? LastContext { get; private set; }
    public int CallCount { get; private set; }

    public FakeMissionExecutor(Func<MissionExecutionContext, MissionExecutionOutcome> script) => _script = script;

    public Task<MissionExecutionOutcome> ExecuteAsync(MissionExecutionContext context, CancellationToken ct)
    {
        CallCount++;
        LastContext = context;
        return Task.FromResult(_script(context));
    }
}

/// <summary>Test double: session service that really creates sessions (in memory).</summary>
internal sealed class FakeSessionService : ISessionService
{
    public OrchestrationStrategy LastRequestedStrategy { get; private set; }
    public bool FailCreation { get; set; }

    public Task<Result<ChatSession>> CreateSessionAsync(
        OrchestrationStrategy strategy = OrchestrationStrategy.Single,
        string? preferredProviderId = null, string? preferredModel = null, string? title = null)
    {
        LastRequestedStrategy = strategy;
        if (FailCreation) return Task.FromResult(Result<ChatSession>.Fail("session creation disabled"));
        return Task.FromResult(Result<ChatSession>.Ok(new ChatSession { Strategy = strategy, Title = title ?? "mission" }));
    }

    public Task<Result<IReadOnlyList<ChatSessionSummary>>> ListSessionsAsync(bool includeDeleted = false)
        => throw new NotSupportedException();
    public Task<Result<ChatSession>> GetSessionAsync(string id) => throw new NotSupportedException();
    public Task<Result<ChatSession>> RenameSessionAsync(string id, string title) => throw new NotSupportedException();
    public Task<Result<ChatSession>> SetStrategyAsync(string id, OrchestrationStrategy strategy, string? preferredProviderId, string? preferredModel)
        => throw new NotSupportedException();
    public Task<Result<ChatSession>> TogglePinAsync(string id) => throw new NotSupportedException();
    public Task<Result<bool>> DeleteSessionAsync(string id) => throw new NotSupportedException();
    public Task<Result<bool>> RestoreSessionAsync(string id) => throw new NotSupportedException();
    public Task<Result<IReadOnlyList<ChatMessage>>> GetMessagesAsync(string sessionId) => throw new NotSupportedException();
    public Task<Result<ChatMessage>> AppendMessageAsync(ChatMessage message) => throw new NotSupportedException();
    public Task<Result<ChatMessage>> UpdateMessageAsync(ChatMessage message) => throw new NotSupportedException();
}

/// <summary>Test double: scripted orchestration event stream.</summary>
internal sealed class FakeOrchestrationFacade : IChatOrchestrationFacade
{
    private readonly Func<string, string, IReadOnlyList<ChatEvent>> _script;
    public string? LastSessionId { get; private set; }
    public string? LastPayload { get; private set; }

    public FakeOrchestrationFacade(Func<string, string, IReadOnlyList<ChatEvent>> script) => _script = script;

    public async IAsyncEnumerable<ChatEvent> SendAsync(
        string sessionId, string userText,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        LastSessionId = sessionId;
        LastPayload = userText;
        foreach (var ev in _script(sessionId, userText))
        {
            await Task.Yield();
            yield return ev;
        }
    }
}

public sealed class MissionControllerTests : IDisposable
{
    private readonly string _root;
    private readonly AutonomyDataPaths _paths;
    private readonly AutonomyDbContext _db;
    private readonly MissionStore _store;

    public MissionControllerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "aerocode-mission-" + Guid.NewGuid().ToString("N"));
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
        var llm = new AutonomyLlmClient(registry: null); // deterministic paths, honest [DEGRADED]
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

    private static FakeMissionExecutor SucceedingExecutor() => new(ctx =>
        new MissionExecutionOutcome(true, false, "真实执行产出内容，长度足够，可以直接进入校验与复盘阶段。", null, "sess-fake", 2, 0.003));

    [Fact]
    public async Task FullRun_Success_EndsAtExperienceWritten_WithAllArtifacts()
    {
        var executor = SucceedingExecutor();
        var controller = Controller(executor);

        var record = await controller.RunAsync("调研主流向量数据库并输出结论");

        Assert.Equal(MissionState.ExperienceWritten, record.State);
        Assert.Equal(MissionOutcome.Succeeded, record.Outcome);
        Assert.NotNull(record.AnalysisJson);
        Assert.NotNull(record.ClarificationJson);
        Assert.NotNull(record.SteelmanJson);
        Assert.NotNull(record.PlanJson);
        Assert.NotNull(record.ExecutionJson);
        Assert.NotNull(record.VerificationJson);
        Assert.NotNull(record.RetrospectiveJson);
        Assert.NotNull(record.TransitionsJson);
        Assert.Equal("sess-fake", record.SessionId);
        Assert.Equal(1, executor.CallCount);
    }

    [Fact]
    public async Task FullRun_TransitionsCoverTheWholeStateMachine()
    {
        var controller = Controller(SucceedingExecutor());
        var record = await controller.RunAsync("写一份技术调研报告");

        var transitions = System.Text.Json.JsonSerializer.Deserialize<List<MissionTransition>>(record.TransitionsJson!)!;
        var reached = transitions.Select(t => t.To).ToHashSet();
        foreach (var state in new[]
        {
            MissionState.Received, MissionState.Analyzed, MissionState.Clarification,
            MissionState.Steelman, MissionState.Planning, MissionState.Executing,
            MissionState.Verifying, MissionState.Retrospective, MissionState.ExperienceWritten,
        })
        {
            Assert.Contains(state, reached);
        }
    }

    [Fact]
    public async Task ExecutionFailure_StillRetrospects_WritesLessons_EndsFailed()
    {
        var executor = new FakeMissionExecutor(_ =>
            new MissionExecutionOutcome(false, false, "", "provider 连接超时", null, 0, 0));
        var controller = Controller(executor);

        var record = await controller.RunAsync("部署服务到测试环境");

        Assert.Equal(MissionOutcome.Failed, record.Outcome);
        Assert.NotNull(record.RetrospectiveJson);
        Assert.Equal(MissionState.ExperienceWritten, record.State); // 失败也必达复盘+经验
        var lessons = await _store.GetRecentLessonsAsync(10);
        Assert.NotEmpty(lessons);
        Assert.Contains(lessons, l => l.Gap.Contains("provider 连接超时"));
    }

    [Fact]
    public async Task ExecutionSucceeded_ButVerificationFails_OutcomeIsFailed_Honestly()
    {
        // 产出内容过短（<20 字符）→ 最小规模校验不通过 → 即使执行成功也如实记 Failed。
        var executor = new FakeMissionExecutor(_ =>
            new MissionExecutionOutcome(true, false, "短", null, "sess-v", 1, 0.001));
        var controller = Controller(executor);

        var record = await controller.RunAsync("实现一个数据导出功能");

        Assert.Equal(MissionOutcome.Failed, record.Outcome);
        Assert.NotNull(record.Error);
        Assert.Contains("校验未通过", record.Error);
    }

    [Fact]
    public async Task StrategyOverride_IsRespected()
    {
        var executor = SucceedingExecutor();
        var controller = Controller(executor);

        var record = await controller.RunAsync("随便一个简单的任务",
            new MissionRunOptions { StrategyOverride = OrchestrationStrategy.Pipeline });

        Assert.Equal("Pipeline", record.Strategy);
        Assert.Contains("显式策略覆盖", record.StrategyRationale);
        Assert.Equal(OrchestrationStrategy.Pipeline, executor.LastContext!.Strategy);
    }

    [Fact]
    public async Task StrategyDrivenByAnalysis_ResearchTaskGetsRouter()
    {
        var executor = SucceedingExecutor();
        var controller = Controller(executor);

        var record = await controller.RunAsync("调研主流向量数据库的使用现状，查文献并在全网搜集资料");

        Assert.Equal("Router", record.Strategy);
        Assert.Equal(OrchestrationStrategy.Router, executor.LastContext!.Strategy);
    }

    [Fact]
    public async Task PriorLessons_AreInjectedIntoExecutorSystemPrompt()
    {
        await _store.AddLessonsAsync(new[]
        {
            new LessonRecord { MissionId = "prev", Phase = "Executing", Gap = "上次因未设置超时导致挂起", Suggestion = "所有网络调用加超时", Severity = "critical" },
        });
        var executor = SucceedingExecutor();
        var controller = Controller(executor);

        await controller.RunAsync("实现一个数据抓取脚本");

        Assert.Contains("上次因未设置超时导致挂起", executor.LastContext!.SystemPrompt);
        Assert.Contains("所有网络调用加超时", executor.LastContext.SystemPrompt);
    }

    [Fact]
    public async Task ClarificationAnswers_AreAppendedToEffectiveTaskText()
    {
        var executor = SucceedingExecutor();
        var controller = Controller(executor);
        var responder = new ScriptedClarificationResponder(new[] { "对象是订单服务", "标准是接口联调通过", "本周五前" });

        await controller.RunAsync("处理一下那个东西", new MissionRunOptions { ClarificationResponder = responder });

        Assert.Contains("补充澄清", executor.LastContext!.EffectiveTaskText);
        Assert.Contains("对象是订单服务", executor.LastContext.EffectiveTaskText);
    }

    [Fact]
    public async Task SteelmanInteractive_AnswerIsRecordedInMission()
    {
        var executor = SucceedingExecutor();
        var controller = Controller(executor);
        var responder = new ScriptedSteelmanResponder("以接口联调通过为完成标准");

        var record = await controller.RunAsync("给订单模块增加退款功能", new MissionRunOptions
        {
            SteelmanMode = SteelmanMode.Interactive,
            SteelmanResponder = responder,
        });

        Assert.Contains("以接口联调通过为完成标准", record.SteelmanJson);
    }

    [Fact]
    public async Task PreCancelled_OutcomeCancelled()
    {
        var controller = Controller(SucceedingExecutor());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var record = await controller.RunAsync("任意任务", ct: cts.Token);

        Assert.Equal(MissionOutcome.Cancelled, record.Outcome);
    }

    [Fact]
    public async Task EmptyTaskText_Throws()
    {
        var controller = Controller(SucceedingExecutor());
        await Assert.ThrowsAsync<ArgumentException>(() => controller.RunAsync("   "));
    }

    [Fact]
    public async Task Mission_IsPersistedToRealSqlite_ReadableByFreshStore()
    {
        var controller = Controller(SucceedingExecutor());
        var record = await controller.RunAsync("验证持久化的任务");

        // A brand-new store over the same DB file must see the mission.
        using var db2 = new AutonomyDbContext(
            new DbContextOptionsBuilder<AutonomyDbContext>().UseSqlite($"Data Source={_paths.DatabaseFile}").Options);
        using var store2 = new MissionStore(db2);
        var loaded = await store2.GetMissionAsync(record.Id);

        Assert.NotNull(loaded);
        Assert.Equal("验证持久化的任务", loaded!.TaskText);
        Assert.Equal(MissionState.ExperienceWritten, loaded.State);
    }

    private sealed class ScriptedClarificationResponder : IClarificationResponder
    {
        private readonly IReadOnlyList<string> _answers;
        public ScriptedClarificationResponder(IReadOnlyList<string> answers) => _answers = answers;
        public Task<IReadOnlyList<string>> AnswerAsync(IReadOnlyList<ClarificationQuestion> questions, CancellationToken ct)
            => Task.FromResult(_answers.Take(questions.Count).ToList() as IReadOnlyList<string>);
    }

    private sealed class ScriptedSteelmanResponder : ISteelmanResponder
    {
        private readonly string _answer;
        public ScriptedSteelmanResponder(string answer) => _answer = answer;
        public Task<string?> AnswerAsync(string taskText, string keyQuestion, CancellationToken ct)
            => Task.FromResult<string?>(_answer);
    }
}

public sealed class MoaMissionExecutorTests
{
    private static MissionExecutionContext Ctx(string text = "执行这个任务", OrchestrationStrategy strategy = OrchestrationStrategy.Single) =>
        new("mid-12345678", text, "SYSTEM-PREAMBLE", strategy,
            new TaskAnalysis { Type = TaskType.Code, Complexity = 2 });

    private static ChatEvent Started(string sessionId, string messageId) => new AssistantMessageStarted
    {
        SessionId = sessionId,
        MessageId = messageId,
        ProviderId = "p",
        ModelId = "m",
        OrchestrationRole = StrategyRole.None,
    };

    [Fact]
    public async Task Success_AggregatesContent_CostAndMessageCount()
    {
        var sessions = new FakeSessionService();
        var facade = new FakeOrchestrationFacade((sid, _) => new ChatEvent[]
        {
            Started(sid, "msg-1"),
            new TextDeltaEvent { SessionId = sid, MessageId = "msg-1", Delta = "第一部分。" },
            new TextDeltaEvent { SessionId = sid, MessageId = "msg-1", Delta = "第二部分。" },
            new MessageCompletedEvent { SessionId = sid, MessageId = "msg-1", CostUsd = 0.002 },
            new TurnCompletedEvent { SessionId = sid, MessageId = "user-1", Strategy = OrchestrationStrategy.Single, TotalMessages = 1, TotalCostUsd = 0.002 },
        });
        var executor = new MoaMissionExecutor(sessions, facade);

        var outcome = await executor.ExecuteAsync(Ctx(), CancellationToken.None);

        Assert.True(outcome.Succeeded);
        Assert.Equal("第一部分。第二部分。", outcome.FinalContent);
        Assert.Equal(1, outcome.AssistantMessages);
        Assert.Equal(0.002, outcome.TotalCostUsd, 5);
        Assert.NotNull(outcome.SessionId);
    }

    [Fact]
    public async Task SystemPrompt_IsPrependedToRealPayload()
    {
        var sessions = new FakeSessionService();
        var facade = new FakeOrchestrationFacade((sid, _) => new ChatEvent[]
        {
            Started(sid, "m"), new MessageCompletedEvent { SessionId = sid, MessageId = "m" },
        });
        var executor = new MoaMissionExecutor(sessions, facade);

        await executor.ExecuteAsync(Ctx("任务正文"), CancellationToken.None);

        Assert.NotNull(facade.LastPayload);
        Assert.Contains("SYSTEM-PREAMBLE", facade.LastPayload);
        Assert.Contains("任务正文", facade.LastPayload);
    }

    [Fact]
    public async Task Strategy_IsPassedToSessionCreation()
    {
        var sessions = new FakeSessionService();
        var facade = new FakeOrchestrationFacade((sid, _) => new ChatEvent[]
        {
            Started(sid, "m"), new MessageCompletedEvent { SessionId = sid, MessageId = "m" },
        });
        var executor = new MoaMissionExecutor(sessions, facade);

        await executor.ExecuteAsync(Ctx(strategy: OrchestrationStrategy.Decompose), CancellationToken.None);

        Assert.Equal(OrchestrationStrategy.Decompose, sessions.LastRequestedStrategy);
    }

    [Fact]
    public async Task MessageFailedEvent_OutcomeFailedWithError()
    {
        var sessions = new FakeSessionService();
        var facade = new FakeOrchestrationFacade((sid, _) => new ChatEvent[]
        {
            Started(sid, "m"),
            new MessageFailedEvent { SessionId = sid, MessageId = "m", Error = "rate limited" },
        });
        var executor = new MoaMissionExecutor(sessions, facade);

        var outcome = await executor.ExecuteAsync(Ctx(), CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Equal("rate limited", outcome.Error);
    }

    [Fact]
    public async Task EmptyEventStream_NoAssistantMessages_HonestFailure()
    {
        var sessions = new FakeSessionService();
        var facade = new FakeOrchestrationFacade((_, _) => Array.Empty<ChatEvent>());
        var executor = new MoaMissionExecutor(sessions, facade);

        var outcome = await executor.ExecuteAsync(Ctx(), CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Contains("未产出任何助手消息", outcome.Error);
    }

    [Fact]
    public async Task SessionCreationFailure_ReturnsFailure_NoFacadeCall()
    {
        var sessions = new FakeSessionService { FailCreation = true };
        var facade = new FakeOrchestrationFacade((_, _) =>
            throw new InvalidOperationException("facade must not be called"));
        var executor = new MoaMissionExecutor(sessions, facade);

        var outcome = await executor.ExecuteAsync(Ctx(), CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Contains("创建执行会话失败", outcome.Error);
    }

    [Fact]
    public async Task Cancellation_OutcomeCancelled()
    {
        var sessions = new FakeSessionService();
        var facade = new FakeOrchestrationFacade((sid, _) => new ChatEvent[]
        {
            Started(sid, "m"),
            new MessageCancelledEvent { SessionId = sid, MessageId = "m" },
        });
        var executor = new MoaMissionExecutor(sessions, facade);

        var outcome = await executor.ExecuteAsync(Ctx(), CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.True(outcome.Cancelled);
    }

    [Fact]
    public async Task EmptyTaskText_FailsWithoutSession()
    {
        var sessions = new FakeSessionService();
        var facade = new FakeOrchestrationFacade((_, _) => Array.Empty<ChatEvent>());
        var executor = new MoaMissionExecutor(sessions, facade);

        var outcome = await executor.ExecuteAsync(Ctx("   "), CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Contains("任务文本为空", outcome.Error);
    }
}
