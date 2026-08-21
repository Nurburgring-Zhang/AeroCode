// ExpertExecutors tests.
//  - AgentExpertExecutor.ComposePrompt: memory / task / node information injection
//    (internal static, visible to this assembly via InternalsVisibleTo).
//  - FacadeExpertExecutor: content aggregation, failure, cancellation, empty stream,
//    session resolution/reuse — driven by hand-written ClusterFakeSessionService and
//    ClusterFakeFacade doubles (pattern from MissionControllerTests).
using AeroAgent.Autonomy.Cluster;
using AeroAgent.Conversation.Models;
using AeroAgent.Conversation.Orchestration;
using Xunit;

namespace AeroCode.Tests.Autonomy.Cluster;

public sealed class ClusterAgentExpertExecutorTests
{
    private static ExpertExecutionContext Context(
        string memory = "", ExpertAttemptKind kind = ExpertAttemptKind.Primary, int fanOutIndex = 0) => new(
            ExpertId: "expert-123",
            ExpertSessionId: "expert-session-456",
            Role: "后端工程师",
            NodeId: "n1",
            NodeName: "登录接口",
            TaskText: "实现登录接口并输出测试要点",
            MemorySnapshot: memory,
            AttemptKind: kind,
            FanOutIndex: fanOutIndex);

    [Fact]
    public void ComposePrompt_ContainsNodeTaskAndDeliverableRequirement()
    {
        var prompt = AgentExpertExecutor.ComposePrompt(Context());

        Assert.Contains("[Primary attempt #0] 节点 n1（登录接口）", prompt);
        Assert.Contains("## 本次任务", prompt);
        Assert.Contains("实现登录接口并输出测试要点", prompt);
        Assert.Contains("产出与任务直接对应的可检验成果", prompt);
        Assert.Contains("禁止编造", prompt);
    }

    [Fact]
    public void ComposePrompt_WithMemory_IncludesPersistedMemorySection()
    {
        var memory = "- [2026-01-01T00:00:00Z] (cluster) 上次任务结论：接口必须幂等";
        var prompt = AgentExpertExecutor.ComposePrompt(Context(memory: memory));

        Assert.Contains("## 你的持久记忆（以往任务沉淀）", prompt);
        Assert.Contains("上次任务结论：接口必须幂等", prompt);
        // Memory section comes before the task section.
        Assert.True(prompt.IndexOf("你的持久记忆", StringComparison.Ordinal)
            < prompt.IndexOf("本次任务", StringComparison.Ordinal));
    }

    [Fact]
    public void ComposePrompt_WithoutMemory_OmitsMemorySection()
    {
        var prompt = AgentExpertExecutor.ComposePrompt(Context(memory: ""));
        Assert.DoesNotContain("你的持久记忆", prompt);
    }

    [Fact]
    public void ComposePrompt_FanOutAttempt_ShowsKindAndCandidateIndex()
    {
        var prompt = AgentExpertExecutor.ComposePrompt(Context(kind: ExpertAttemptKind.FanOut, fanOutIndex: 2));
        Assert.Contains("[FanOut attempt #2] 节点 n1（登录接口）", prompt);
    }
}

public sealed class ClusterFacadeExpertExecutorTests
{
    private static ExpertExecutionContext Context(
        string expertId = "expert-1", string sessionId = "sess-1") => new(
            ExpertId: expertId,
            ExpertSessionId: sessionId,
            Role: "测试工程师",
            NodeId: "n9",
            NodeName: "回归测试",
            TaskText: "为订单服务编写回归测试清单",
            MemorySnapshot: string.Empty,
            AttemptKind: ExpertAttemptKind.Primary,
            FanOutIndex: 0);

    [Fact]
    public async Task Success_AggregatesDeltas_AndSendsComposedPrompt()
    {
        var sessions = new ClusterFakeSessionService();
        var facade = new ClusterFakeFacade((sid, _) => new ChatEvent[]
        {
            ClusterFakeFacade.Started(sid, "msg-1"),
            new TextDeltaEvent { SessionId = sid, MessageId = "msg-1", Delta = "回归测试清单：" },
            new TextDeltaEvent { SessionId = sid, MessageId = "msg-1", Delta = "下单/支付/退款。" },
            new MessageCompletedEvent { SessionId = sid, MessageId = "msg-1" },
        });
        var executor = new FacadeExpertExecutor(sessions, facade);

        var outcome = await executor.ExecuteAsync(Context(sessionId: string.Empty), CancellationToken.None);

        Assert.True(outcome.Succeeded);
        Assert.Equal("回归测试清单：下单/支付/退款。", outcome.Output);
        Assert.Null(outcome.Error);
        // The facade received the composed prompt (node + task information).
        Assert.NotNull(facade.LastPayload);
        Assert.Contains("为订单服务编写回归测试清单", facade.LastPayload);
        Assert.Contains("节点 n9（回归测试）", facade.LastPayload);
    }

    [Fact]
    public async Task MultipleAssistantMessages_LastNonEmptyWins()
    {
        var sessions = new ClusterFakeSessionService();
        var facade = new ClusterFakeFacade((sid, _) => new ChatEvent[]
        {
            ClusterFakeFacade.Started(sid, "msg-1"),
            new TextDeltaEvent { SessionId = sid, MessageId = "msg-1", Delta = "第一稿" },
            ClusterFakeFacade.Started(sid, "msg-2"),
            new TextDeltaEvent { SessionId = sid, MessageId = "msg-2", Delta = "最终稿" },
        });
        var executor = new FacadeExpertExecutor(sessions, facade);

        var outcome = await executor.ExecuteAsync(Context(sessionId: string.Empty), CancellationToken.None);

        Assert.True(outcome.Succeeded);
        Assert.Equal("最终稿", outcome.Output);
    }

    [Fact]
    public async Task MessageFailedEvent_FailedOutcomeWithError()
    {
        var sessions = new ClusterFakeSessionService();
        var facade = new ClusterFakeFacade((sid, _) => new ChatEvent[]
        {
            ClusterFakeFacade.Started(sid, "msg-1"),
            new TextDeltaEvent { SessionId = sid, MessageId = "msg-1", Delta = "部分内容" },
            new MessageFailedEvent { SessionId = sid, MessageId = "msg-1", Error = "provider rate limited" },
        });
        var executor = new FacadeExpertExecutor(sessions, facade);

        var outcome = await executor.ExecuteAsync(Context(sessionId: string.Empty), CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.False(outcome.Cancelled);
        Assert.Equal("provider rate limited", outcome.Error);
        Assert.Equal("部分内容", outcome.Output); // partial content is preserved
    }

    [Fact]
    public async Task EmptyEventStream_HonestFailure()
    {
        var sessions = new ClusterFakeSessionService();
        var facade = new ClusterFakeFacade((_, _) => Array.Empty<ChatEvent>());
        var executor = new FacadeExpertExecutor(sessions, facade);

        var outcome = await executor.ExecuteAsync(Context(sessionId: string.Empty), CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Equal("orchestration produced no assistant content", outcome.Error);
        Assert.Equal(string.Empty, outcome.Output);
    }

    [Fact]
    public async Task MessageCancelledEvent_CancelledOutcome()
    {
        var sessions = new ClusterFakeSessionService();
        var facade = new ClusterFakeFacade((sid, _) => new ChatEvent[]
        {
            ClusterFakeFacade.Started(sid, "msg-1"),
            new MessageCancelledEvent { SessionId = sid, MessageId = "msg-1" },
        });
        var executor = new FacadeExpertExecutor(sessions, facade);

        var outcome = await executor.ExecuteAsync(Context(sessionId: string.Empty), CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.True(outcome.Cancelled);
    }

    [Fact]
    public async Task ExistingSession_IsReused_WithoutCreatingANewOne()
    {
        var sessions = new ClusterFakeSessionService();
        sessions.ExistingSessionIds.Add("sess-existing");
        var facade = new ClusterFakeFacade((sid, _) => new ChatEvent[]
        {
            ClusterFakeFacade.Started(sid, "m"),
            new TextDeltaEvent { SessionId = sid, MessageId = "m", Delta = "ok" },
        });
        var executor = new FacadeExpertExecutor(sessions, facade);

        var outcome = await executor.ExecuteAsync(Context(sessionId: "sess-existing"), CancellationToken.None);

        Assert.True(outcome.Succeeded);
        Assert.Equal(1, sessions.GetCallCount);
        Assert.Equal(0, sessions.CreateCallCount);
        Assert.Equal("sess-existing", facade.LastSessionId);
    }

    [Fact]
    public async Task MissingSession_IsCreated_WithStrategyAndExpertTitle()
    {
        var sessions = new ClusterFakeSessionService(); // GetSessionAsync fails for unknown ids
        var facade = new ClusterFakeFacade((sid, _) => new ChatEvent[]
        {
            ClusterFakeFacade.Started(sid, "m"),
            new TextDeltaEvent { SessionId = sid, MessageId = "m", Delta = "ok" },
        });
        var executor = new FacadeExpertExecutor(sessions, facade, OrchestrationStrategy.Decompose);

        var outcome = await executor.ExecuteAsync(Context(expertId: "expert-77", sessionId: "sess-missing"),
            CancellationToken.None);

        Assert.True(outcome.Succeeded);
        Assert.Equal(1, sessions.GetCallCount);
        Assert.Equal(1, sessions.CreateCallCount);
        Assert.Equal(OrchestrationStrategy.Decompose, sessions.LastCreateStrategy);
        Assert.Contains("expert-77", sessions.LastCreateTitle);
        Assert.Contains("测试工程师", sessions.LastCreateTitle);
        Assert.Equal(sessions.CreatedSessions.Single().Id, facade.LastSessionId);
    }

    [Fact]
    public async Task SessionCreationFailure_CapturedAsFailedOutcome()
    {
        var sessions = new ClusterFakeSessionService { FailCreation = true };
        var facade = new ClusterFakeFacade((_, _) =>
            throw new InvalidOperationException("facade must not be called"));
        var executor = new FacadeExpertExecutor(sessions, facade);

        var outcome = await executor.ExecuteAsync(Context(sessionId: string.Empty), CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Contains("failed to create expert session", outcome.Error);
        Assert.Equal(0, facade.SendCallCount);
    }

    [Fact]
    public async Task SameExpert_SecondAttempt_ReusesCachedSession()
    {
        var sessions = new ClusterFakeSessionService();
        var facade = new ClusterFakeFacade((sid, _) => new ChatEvent[]
        {
            ClusterFakeFacade.Started(sid, "m"),
            new TextDeltaEvent { SessionId = sid, MessageId = "m", Delta = "ok" },
        });
        var executor = new FacadeExpertExecutor(sessions, facade);
        var context = Context(expertId: "expert-42", sessionId: string.Empty);

        await executor.ExecuteAsync(context, CancellationToken.None);
        var second = await executor.ExecuteAsync(context, CancellationToken.None);

        Assert.True(second.Succeeded);
        Assert.Equal(1, sessions.CreateCallCount); // only the first attempt created a session
        Assert.Equal(0, sessions.GetCallCount);    // empty ExpertSessionId → no lookup; second attempt hit the cache
        Assert.Equal(2, facade.SendCallCount);
    }

    [Fact]
    public async Task FacadeThrows_CapturedAsFailedOutcome_NotLeaked()
    {
        var sessions = new ClusterFakeSessionService();
        var facade = new ClusterFakeFacade((_, _) => throw new InvalidOperationException("facade boom"));
        var executor = new FacadeExpertExecutor(sessions, facade);

        var outcome = await executor.ExecuteAsync(Context(sessionId: string.Empty), CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Contains("InvalidOperationException", outcome.Error);
        Assert.Contains("facade boom", outcome.Error);
    }
}
