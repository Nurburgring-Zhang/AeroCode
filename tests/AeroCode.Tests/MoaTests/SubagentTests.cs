using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AeroAgent.Conversation.Models;
using AeroAgent.Moa.Budget;
using AeroAgent.Moa.Profiles;
using AeroAgent.Moa.Subagent;
using AeroAgent.Moa.Tools;
using AeroCode.AI.Configuration;
using AeroCode.AI.Models;
using AeroCode.AI.Providers;
using AeroCode.Harness.EventBus;
using AeroCode.Harness.Permission;
using Xunit;
using ConvChatMessage = AeroAgent.Conversation.Models.ChatMessage;

namespace AeroCode.Tests.MoaTests;

/// <summary>
/// 测试用受闸 provider：ChatAsync 悬挂到 Gate 放行（制造确定性的“运行中”窗口），
/// 放行后返回预设非流式答复。与 ScriptedProvider 同口径的真实 IAiProvider 双。
/// </summary>
internal sealed class GatedProvider : IAiProvider
{
    public string ProviderId { get; init; } = "gated";
    public string DisplayName => "Gated";
    public ProviderKind Kind => ProviderKind.OpenAICompatible;
    public bool SupportsStreaming => false;
    public bool SupportsToolCalling => false;
    public bool SupportsThinking => false;

    /// <summary>ChatAsync 等待的闸门。</summary>
    public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string Content { get; set; } = "gated done";
    public UsageInfo? Usage { get; set; }

    public async Task<ChatResponse> ChatAsync(ChatRequest request, CancellationToken ct = default)
    {
        await Gate.Task.WaitAsync(ct);
        return new ChatResponse
        {
            Id = "resp-gated",
            Model = request.Model,
            Content = Content,
            FinishReason = "stop",
            Usage = Usage,
        };
    }

    public IAsyncEnumerable<ChatChunk> StreamChatAsync(ChatRequest request, CancellationToken ct = default)
        => throw new NotSupportedException("gated provider is non-streaming by design");

    public Task<bool> HealthCheckAsync(CancellationToken ct = default) => Task.FromResult(false);
}

/// <summary>
/// 测试用可编程 mission 闸门：可指定首轮实报后转 Exhausted 且**不触发事件**——
/// 确定性建模「BudgetExhausted 转换沿落在 LaunchAsync 拒绝判定与 _inFlight 登记之间」
/// 的竞态窗口（run 错过一次性事件 = 收不到取消通知，只能靠工具循环逐轮状态复查自纠）。
/// </summary>
internal sealed class ProgrammableBudgetGate : ITokenBudgetGate
{
    public BudgetState State { get; set; } = BudgetState.Running;
    public long LimitTokens { get; set; } = 100;
    public long SpentTokens { get; set; }
    public bool DegradedToSingleAgent { get; set; }
    public bool ExhaustAfterFirstReport { get; set; }

    public event Action<BudgetSnapshot>? BudgetExhausted;

    public BudgetState ReportUsage(long tokens)
    {
        SpentTokens += tokens;
        if (ExhaustAfterFirstReport && State == BudgetState.Running)
        {
            State = BudgetState.Exhausted;
            DegradedToSingleAgent = true;
            // 有意不触发 BudgetExhausted：见类注释（错过事件沿的竞态窗口建模）。
        }

        return State;
    }

    public BudgetSnapshot Snapshot() => new(State, SpentTokens, LimitTokens, DegradedToSingleAgent);
}

/// <summary>
/// SubAgentRunner 真实行为（真实 SQLite 会话库 + 可编程 provider + 真实授权链路）：
/// 深度硬上限、并行上限排队、完成事件真实成本、独立会话上下文、权限显式继承、
/// MaxTurns/预算/取消/缺 provider 的诚实失败终态。
/// </summary>
public sealed class SubagentTests : MoaTestBase
{
    private static SubAgentSpec Spec(
        string providerId,
        int depth = 1,
        int maxTurns = 8,
        double maxCostUsd = 0,
        string prompt = "子任务：读取笔记并汇总") => new(
        Description: "probe-subagent",
        Prompt: prompt,
        ProviderId: providerId,
        Model: string.Empty,
        Depth: depth,
        MaxTurns: maxTurns,
        MaxCostUsd: maxCostUsd,
        ParallelSafe: true);

    private static ChatResponse FinalResponse(string content, UsageInfo? usage) => new()
    {
        Id = "resp-final",
        Model = string.Empty,
        Content = content,
        FinishReason = "stop",
        Usage = usage,
    };

    private static ChatResponse ToolCallResponse(string callId, string toolName, string argsJson, UsageInfo? usage) => new()
    {
        Id = "resp-tc",
        Model = string.Empty,
        Content = string.Empty,
        ToolCalls = new List<ToolCall>
        {
            new() { Id = callId, Type = "function", FunctionName = toolName, ArgumentsJson = argsJson },
        },
        FinishReason = "tool_calls",
        Usage = usage,
    };

    private SubAgentRunner NewRunner(SubagentOptions? options = null, ToolRouter? tools = null, EventBus? events = null)
        => new(Sessions, Registry, Catalog, events ?? new EventBus(), options, tools);

    private static ToolRouter NewToolRouter(PermissionPolicy? policy = null, ScriptedBroker? broker = null)
    {
        var registry = new ToolboxRegistry();
        var box = new ScriptedToolbox("notes", new ToolDefinition { Name = "get_note", Description = "读取笔记" });
        box.SetResult("get_note", ToolInvokeResult.Ok("NOTE_BODY"));
        registry.Register(box);
        return new ToolRouter(
            registry,
            policy ?? PermissionPolicy.CreateDefault(new EventBus()),
            broker ?? new ScriptedBroker(PermissionDecision.Allow));
    }

    private async Task<ConvChatMessage[]> SubagentMessagesAsync(string titlePrefix)
    {
        var listed = await Sessions.ListSessionsAsync();
        Assert.True(listed.IsSuccess);
        var subSession = listed.Value!.Single(s => s.Title.StartsWith(titlePrefix, StringComparison.Ordinal));
        var messages = await Sessions.GetMessagesAsync(subSession.Id);
        Assert.True(messages.IsSuccess);
        return messages.Value!.ToArray();
    }

    private static readonly string SessionTitlePrefix = "[subagent] probe-subagent";

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.True(condition(), "condition not met within timeout");
    }

    /// <summary>异步条件轮询（条件本身需要 await 时使用，与同步重载语义一致）。</summary>
    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition().ConfigureAwait(false))
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.True(await condition().ConfigureAwait(false), "condition not met within timeout");
    }

    [Fact]
    public async Task Depth_BeyondHardLimit_IsRejectedHonest()
    {
        // 默认 MaxDepth=4：深度 4 允许（层数含自身），深度 5 / 0 拒绝（诚实失败）。
        var runner = NewRunner();
        var provider = AddProvider("sa");
        provider.ResponseQueue.Enqueue(FinalResponse("done", null));

        await Assert.ThrowsAsync<ArgumentException>(() => runner.LaunchAsync(Spec("sa", depth: 5), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => runner.LaunchAsync(Spec("sa", depth: 0), CancellationToken.None));

        var handle = await runner.LaunchAsync(Spec("sa", depth: 4), CancellationToken.None);
        var summary = await handle.WaitAsync(CancellationToken.None);
        Assert.Equal("done", summary);
        Assert.Equal(4, handle.Spec.Depth);
        await handle.DisposeAsync();
    }

    [Fact]
    public async Task Depth1_InnerRedispatch_Rejected()
    {
        // MaxDepth=1 配置：深度 1（第一层派发）允许；内层再派发（深度 2）被诚实拒绝。
        var runner = NewRunner(new SubagentOptions { MaxDepth = 1 });
        var provider = AddProvider("sa1");
        provider.ResponseQueue.Enqueue(FinalResponse("leaf done", null));

        await Assert.ThrowsAsync<ArgumentException>(() => runner.LaunchAsync(Spec("sa1", depth: 2), CancellationToken.None));

        var handle = await runner.LaunchAsync(Spec("sa1", depth: 1), CancellationToken.None);
        Assert.Equal("leaf done", await handle.WaitAsync(CancellationToken.None));
        await handle.DisposeAsync();
    }

    [Fact]
    public async Task Disabled_ThrowsInvalidOperationException()
    {
        var runner = NewRunner(new SubagentOptions { Enabled = false });
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.LaunchAsync(Spec("x"), CancellationToken.None));
        Assert.Contains("disabled", ex.Message);
    }

    [Fact]
    public async Task InvalidTurnsOrBudget_ThrowsArgumentException()
    {
        var runner = NewRunner();
        // 负数/NaN/Infinity 的预算都非法；0 语义为“无计价上限”故允许；MaxTurns < 1 非法。
        await Assert.ThrowsAsync<ArgumentException>(() => runner.LaunchAsync(Spec("sa", maxCostUsd: -1), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => runner.LaunchAsync(Spec("sa", maxCostUsd: double.NaN), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => runner.LaunchAsync(Spec("sa", maxCostUsd: double.PositiveInfinity), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => runner.LaunchAsync(Spec("sa", maxTurns: 0), CancellationToken.None));
    }

    [Fact]
    public async Task Completion_PublishesEventWithRealCost_AndPersistsToIndependentSession()
    {
        var events = new EventBus();
        var received = new List<SubAgentCompletedEvent>();
        events.Subscribe<SubAgentCompletedEvent>(received.Add);

        SetProfile("sa", new[] { ModelStrength.General }, costPerMIn: 1.0, costPerMOut: 2.0);
        var provider = AddProvider("sa");
        provider.ResponseQueue.Enqueue(FinalResponse("汇总完成", new UsageInfo { PromptTokens = 100, CompletionTokens = 10 }));

        var runner = NewRunner(events: events);
        var handle = await runner.LaunchAsync(Spec("sa"), CancellationToken.None);
        var summary = await handle.WaitAsync(CancellationToken.None);
        await handle.DisposeAsync();

        // 汇总与事件：真实 usage 计价 = 100*1.0/1M + 10*2.0/1M = 0.00012
        Assert.Equal("汇总完成", summary);
        var evt = Assert.Single(received);
        Assert.Equal(handle.Id, evt.SubAgentId);
        Assert.True(evt.Success);
        Assert.Equal("汇总完成", evt.Summary);
        Assert.Equal(0.00012, evt.CostUsd, precision: 9);

        // 独立会话上下文：子代理会话真实存在、只含自身消息（无父历史回灌）。
        var persisted = await SubagentMessagesAsync(SessionTitlePrefix);
        var finalMessage = Assert.Single(persisted);
        Assert.Equal(ChatRole.Assistant, finalMessage.Role);
        Assert.Equal(StrategyRole.Worker, finalMessage.OrchestrationRole);
        Assert.Equal("汇总完成", finalMessage.Content);
        Assert.Equal(100, finalMessage.TokensIn);
        Assert.Equal(0.00012, finalMessage.CostUsd, precision: 9);
    }

    [Fact]
    public async Task MaxTurnsExceeded_AbortsHonest()
    {
        SetProfile("sa", new[] { ModelStrength.General }, costPerMIn: 1.0, costPerMOut: 2.0);
        var provider = AddProvider("sa");
        provider.ResponseQueue.Enqueue(ToolCallResponse("c1", "get_note", "{\"id\":\"n1\"}", new UsageInfo { PromptTokens = 10, CompletionTokens = 1 }));
        provider.ResponseQueue.Enqueue(ToolCallResponse("c2", "get_note", "{\"id\":\"n2\"}", new UsageInfo { PromptTokens = 10, CompletionTokens = 1 }));
        provider.ResponseQueue.Enqueue(ToolCallResponse("c3", "get_note", "{\"id\":\"n3\"}", new UsageInfo { PromptTokens = 10, CompletionTokens = 1 }));

        var events = new EventBus();
        var received = new List<SubAgentCompletedEvent>();
        events.Subscribe<SubAgentCompletedEvent>(received.Add);

        var runner = NewRunner(tools: NewToolRouter(), events: events);
        var handle = await runner.LaunchAsync(Spec("sa", maxTurns: 2), CancellationToken.None);
        var summary = await handle.WaitAsync(CancellationToken.None);
        await handle.DisposeAsync();

        Assert.Contains("exceeded the limit (2 turns)", summary);
        var evt = Assert.Single(received);
        Assert.False(evt.Success);

        // 两轮 tool_call 真实执行并落库；占位消息落 Failed 终态（第 3 轮未发生）。
        var persisted = await SubagentMessagesAsync(SessionTitlePrefix);
        Assert.Equal(2, persisted.Count(m => m.Role == ChatRole.Tool));
        var finalMessage = persisted.Single(m => m.Role == ChatRole.Assistant && m.ParentMessageId is null);
        Assert.Equal(MessageStatus.Failed, finalMessage.Status);
        Assert.Contains("exceeded the limit", finalMessage.Error);
    }

    [Fact]
    public async Task BudgetExceeded_MidLoop_AbortsHonestWithRealCost()
    {
        SetProfile("sa", new[] { ModelStrength.General }, costPerMIn: 1.0, costPerMOut: 2.0);
        var provider = AddProvider("sa");
        // 每轮真实 usage = 1,000,000 in → 成本 $1.0；预算 $1.5 → 第 3 轮前超限。
        provider.ResponseQueue.Enqueue(ToolCallResponse("c1", "get_note", "{\"id\":\"n1\"}", new UsageInfo { PromptTokens = 1_000_000, CompletionTokens = 0 }));
        provider.ResponseQueue.Enqueue(ToolCallResponse("c2", "get_note", "{\"id\":\"n2\"}", new UsageInfo { PromptTokens = 1_000_000, CompletionTokens = 0 }));

        var events = new EventBus();
        var received = new List<SubAgentCompletedEvent>();
        events.Subscribe<SubAgentCompletedEvent>(received.Add);

        var runner = NewRunner(tools: NewToolRouter(), events: events);
        var handle = await runner.LaunchAsync(Spec("sa", maxCostUsd: 1.5), CancellationToken.None);
        var summary = await handle.WaitAsync(CancellationToken.None);
        await handle.DisposeAsync();

        Assert.Contains("budget exceeded", summary);
        var evt = Assert.Single(received);
        Assert.False(evt.Success);
        Assert.Equal(2.0, evt.CostUsd, precision: 6); // 已真实发生的成本如实回注，不归零
    }

    [Fact]
    public async Task ToolCalls_RouteThroughInheritedRouter_PersistResults()
    {
        SetProfile("sa", new[] { ModelStrength.General });
        var provider = AddProvider("sa");
        provider.ResponseQueue.Enqueue(ToolCallResponse("c1", "get_note", "{\"id\":\"n1\"}", new UsageInfo { PromptTokens = 10, CompletionTokens = 2 }));
        provider.ResponseQueue.Enqueue(FinalResponse("笔记读取完毕：NOTE_BODY", new UsageInfo { PromptTokens = 20, CompletionTokens = 4 }));

        var runner = NewRunner(tools: NewToolRouter());
        var handle = await runner.LaunchAsync(Spec("sa"), CancellationToken.None);
        var summary = await handle.WaitAsync(CancellationToken.None);
        await handle.DisposeAsync();

        Assert.Equal("笔记读取完毕：NOTE_BODY", summary);

        var persisted = await SubagentMessagesAsync(SessionTitlePrefix);
        var toolMessage = persisted.Single(m => m.Role == ChatRole.Tool);
        Assert.Equal("get_note", toolMessage.Name);
        Assert.Equal("c1", toolMessage.ToolCallId);
        Assert.Equal(MessageStatus.Completed, toolMessage.Status);
        // tool 结果真实回灌进后续 provider 上下文。
        var lastRequest = provider.LastRequest!;
        Assert.Contains(lastRequest.Messages, m => m.Role == "tool" && m.Content!.Contains("NOTE_BODY"));
    }

    [Fact]
    public async Task PermissionDeny_IsInheritedExplicitly_FedBackHonest()
    {
        SetProfile("sa", new[] { ModelStrength.General });
        var provider = AddProvider("sa");
        provider.ResponseQueue.Enqueue(ToolCallResponse("c1", "get_note", "{\"id\":\"n1\"}", null));
        provider.ResponseQueue.Enqueue(FinalResponse("工具被拒，如实转述", null));

        // 显式继承验证：父策略实例把 get_note 设为 Deny，子代理经同一 router 执行时被拒。
        var policy = PermissionPolicy.CreateDefault(new EventBus());
        policy.SetDefaultDecision("get_note", PermissionDecision.Deny);
        var runner = NewRunner(tools: NewToolRouter(policy: policy));
        var handle = await runner.LaunchAsync(Spec("sa"), CancellationToken.None);
        var summary = await handle.WaitAsync(CancellationToken.None);
        await handle.DisposeAsync();

        Assert.Equal("工具被拒，如实转述", summary);

        var persisted = await SubagentMessagesAsync(SessionTitlePrefix);
        var toolMessage = persisted.Single(m => m.Role == ChatRole.Tool);
        Assert.Equal(MessageStatus.Degraded, toolMessage.Status);
        Assert.Contains("Permission denied", toolMessage.Content);
    }

    [Fact]
    public async Task MissingProvider_FailsHonest()
    {
        var events = new EventBus();
        var received = new List<SubAgentCompletedEvent>();
        events.Subscribe<SubAgentCompletedEvent>(received.Add);

        var runner = NewRunner(events: events);
        var handle = await runner.LaunchAsync(Spec("no-such-provider"), CancellationToken.None);
        var summary = await handle.WaitAsync(CancellationToken.None);
        await handle.DisposeAsync();

        Assert.Contains("provider 'no-such-provider' unavailable", summary);
        Assert.False(received.Single().Success);
    }

    [Fact]
    public async Task Cancel_RunningRun_HonestCancelledTerminal()
    {
        SetProfile("sa", new[] { ModelStrength.General });
        var gated = new GatedProvider { ProviderId = "sa-gated" };
        Registry.Add(gated);

        var events = new EventBus();
        var received = new List<SubAgentCompletedEvent>();
        events.Subscribe<SubAgentCompletedEvent>(received.Add);

        var runner = NewRunner(events: events);
        var handle = await runner.LaunchAsync(Spec("sa-gated"), CancellationToken.None);
        await WaitUntilAsync(() => runner.ActiveCount == 1, TimeSpan.FromSeconds(5));
        handle.Cancel();
        var summary = await handle.WaitAsync(CancellationToken.None);
        await handle.DisposeAsync();

        Assert.Equal("cancelled by user", summary);
        var evt = Assert.Single(received);
        Assert.False(evt.Success);
        Assert.Equal(0, evt.CostUsd);

        var persisted = await SubagentMessagesAsync(SessionTitlePrefix);
        Assert.All(persisted, m => Assert.NotEqual(MessageStatus.Pending, m.Status));
        Assert.Equal(MessageStatus.Cancelled, persisted.Single(m => m.ParentMessageId is null).Status);
        gated.Gate.TrySetResult(); // 收尾：释放悬挂任务
    }

    [Fact]
    public async Task ParallelLimit_QueuesThirdLaunch_UntilSlotFrees()
    {
        SetProfile("sa", new[] { ModelStrength.General });
        var g1 = new GatedProvider { ProviderId = "g1", Content = "p1" };
        var g2 = new GatedProvider { ProviderId = "g2", Content = "p2" };
        Registry.Add(g1);
        Registry.Add(g2);
        var free = AddProvider("g3");
        free.NonStreamContent = "third done";

        var runner = NewRunner(new SubagentOptions { MaxParallel = 2 });
        var h1 = await runner.LaunchAsync(new SubAgentSpec("a", "p1", "g1", string.Empty, 1, 8, 0, true), CancellationToken.None);
        var h2 = await runner.LaunchAsync(new SubAgentSpec("b", "p2", "g2", string.Empty, 1, 8, 0, true), CancellationToken.None);
        var h3 = await runner.LaunchAsync(new SubAgentSpec("c", "p3", "g3", string.Empty, 1, 8, 0, true), CancellationToken.None);

        // 上限生效：前两个运行中，第三个排队（尚未发起任何调用）。
        await WaitUntilAsync(() => runner.ActiveCount == 2, TimeSpan.FromSeconds(5));
        Assert.Equal(0, free.LastRequestMessages?.Count ?? 0);

        // 释放一个槽位 → 第三个获得调度并完成。
        g1.Gate.TrySetResult();
        Assert.Equal("third done", await h3.WaitAsync(CancellationToken.None));
        Assert.Equal("p1", await h1.WaitAsync(CancellationToken.None));
        await WaitUntilAsync(() => runner.ActiveCount == 1, TimeSpan.FromSeconds(5)); // g2 仍悬挂

        g2.Gate.TrySetResult();
        Assert.Equal("p2", await h2.WaitAsync(CancellationToken.None));
        await h1.DisposeAsync();
        await h2.DisposeAsync();
        await h3.DisposeAsync();
    }

    // ---- S-M1（R1 审查修复）：错过 BudgetExhausted 事件沿的竞态 → 工具循环逐轮复查自纠 ----

    [Fact]
    public async Task BudgetGateExhausted_MissedEventRace_SecondTurnHonestCancelled()
    {
        SetProfile("sa-gate", new[] { ModelStrength.General });
        var provider = AddProvider("sa-gate");
        // 首轮真实 usage 触发闸门转 Exhausted（无事件沿）；第二轮 provider 调用必须不发生。
        provider.ResponseQueue.Enqueue(ToolCallResponse(
            "c1", "get_note", "{}", new UsageInfo { PromptTokens = 10, CompletionTokens = 1 }));
        provider.ThrowWhenResponseQueueEmpty =
            new InvalidOperationException("second provider call must not happen after gate exhausted");

        var events = new EventBus();
        var received = new List<SubAgentCompletedEvent>();
        events.Subscribe<SubAgentCompletedEvent>(received.Add);

        var gate = new ProgrammableBudgetGate { ExhaustAfterFirstReport = true };
        var runner = new SubAgentRunner(
            Sessions, Registry, Catalog, events, tools: NewToolRouter(), budgetGate: gate);
        var handle = await runner.LaunchAsync(Spec("sa-gate"), CancellationToken.None);
        var summary = await handle.WaitAsync(CancellationToken.None);
        await handle.DisposeAsync();

        // 错过事件沿的第二轮被逐轮闸门复查诚实取消：与事件取消一致的 Cancelled 终态路径。
        Assert.Equal(BudgetState.Exhausted, gate.State);
        Assert.Equal("cancelled by user", summary);
        var evt = Assert.Single(received);
        Assert.False(evt.Success);
        Assert.True(runner.DegradedToSingleAgent);

        var persisted = await SubagentMessagesAsync(SessionTitlePrefix);
        Assert.Equal(MessageStatus.Cancelled, persisted.Single(m => m.ParentMessageId is null).Status);
        Assert.All(persisted, m => Assert.NotEqual(MessageStatus.Pending, m.Status));
    }

    // ---- S-M2（R1 审查修复）：取消落在工具执行中 → Pending 的 tool 消息落诚实终态 ----

    [Fact]
    public async Task CancelDuringToolExecution_PendingToolMessageFinalized_NoZombie()
    {
        SetProfile("sa-slow", new[] { ModelStrength.General });
        var provider = AddProvider("sa-slow");
        provider.ResponseQueue.Enqueue(ToolCallResponse("c1", "get_note", "{}", null));

        // 悬挂工具：取消落在工具执行中（tool 消息已以 Pending 落库且尚未回写）。
        var box = new ScriptedToolbox("notes", new ToolDefinition { Name = "get_note", Description = "d" })
        {
            DelayMs = 30_000,
        };
        box.SetResult("get_note", ToolInvokeResult.Ok("NOTE_BODY"));
        var registry = new ToolboxRegistry();
        registry.Register(box);
        var router = new ToolRouter(
            registry,
            PermissionPolicy.CreateDefault(new EventBus()),
            new ScriptedBroker(PermissionDecision.Allow));

        var events = new EventBus();
        var received = new List<SubAgentCompletedEvent>();
        events.Subscribe<SubAgentCompletedEvent>(received.Add);
        var runner = new SubAgentRunner(Sessions, Registry, Catalog, events, tools: router);
        var handle = await runner.LaunchAsync(Spec("sa-slow"), CancellationToken.None);

        // 等 tool 消息以 Pending 状态真实落库（僵尸窗口已存在），再取消。
        await WaitUntilAsync(async () =>
        {
            var messages = await SubagentMessagesAsync(SessionTitlePrefix).ConfigureAwait(false);
            return messages.Any(m => m.Role == ChatRole.Tool && m.Status == MessageStatus.Pending);
        }, TimeSpan.FromSeconds(5));
        handle.Cancel();

        var summary = await handle.WaitAsync(CancellationToken.None);
        await handle.DisposeAsync();

        Assert.Equal("cancelled by user", summary);
        Assert.False(received.Single().Success);

        var persisted = await SubagentMessagesAsync(SessionTitlePrefix);
        Assert.All(persisted, m => Assert.True(
            m.Status is not (MessageStatus.Pending or MessageStatus.Streaming),
            $"message {m.Id} ({m.Role}) left in {m.Status}"));
        Assert.Equal(MessageStatus.Cancelled, persisted.Single(m => m.Role == ChatRole.Tool).Status);
        Assert.Equal(MessageStatus.Cancelled, persisted.Single(m => m.ParentMessageId is null).Status);
    }
}
