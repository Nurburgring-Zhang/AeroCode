using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using AeroAgent.Conversation.Models;
using AeroAgent.Conversation.Orchestration;
using AeroAgent.Moa.Accounting;
using AeroAgent.Moa.Assignment;
using AeroAgent.Moa.Profiles;
using AeroAgent.Moa.Strategies;
using AeroAgent.Moa.Tools;
using AeroCode.AI.Models;
using AeroCode.Harness.EventBus;
using AeroCode.Harness.Permission;
using Xunit;
using AiChatMessage = AeroCode.AI.Models.ChatMessage;
using ChatMessage = AeroAgent.Conversation.Models.ChatMessage;

namespace AeroCode.Tests.MoaTests;

/// <summary>
/// WorkerRunner 工具循环端到端行为（真实 DB 持久化 + 可编程 provider + 真实授权链路）：
/// 多轮工具调用的消息落库形态、上下文回灌、授权拒绝/未知工具的诚实降级、
/// 最大轮数守卫、循环中失败与取消的终态语义。
/// </summary>
public sealed class ToolLoopTests : MoaTestBase
{
    private static readonly IReadOnlyList<AiChatMessage> Prompt = new List<AiChatMessage>
    {
        new() { Role = "user", Content = "帮我读笔记" },
    };

    private static ChatResponse ToolCallResponse(string callId, string toolName, string argsJson, UsageInfo? usage)
        => new()
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

    private static ChatResponse FinalResponse(string content, UsageInfo? usage)
        => new()
        {
            Id = "resp-final",
            Model = string.Empty,
            Content = content,
            FinishReason = "stop",
            Usage = usage,
        };

    private async Task<(OrchestrationContext Ctx, ModelAssignment Assignment, ModelProfile Profile)> SetupAsync(
        string providerId, ConversationTests.ScriptedProvider provider)
    {
        // provider 已由 AddProvider 注册进 Registry，这里只负责画像/会话/上下文装配
        _ = provider;
        var profile = SetProfile(providerId, new[] { ModelStrength.General },
            costPerMIn: 1.0, costPerMOut: 2.0);
        var session = await NewSessionAsync(OrchestrationStrategy.Single);
        var ctx = new OrchestrationContext
        {
            Session = session,
            History = Array.Empty<ChatMessage>(),
            UserMessageId = "msg-user",
            Providers = Registry,
        };
        return (ctx, new ModelAssignment(providerId, string.Empty, profile), profile);
    }

    private static (Channel<ChatEvent> Sink, ChannelWriter<ChatEvent> Writer) NewSink()
    {
        var ch = Channel.CreateUnbounded<ChatEvent>();
        return (ch, ch.Writer);
    }

    private static async Task<List<ChatEvent>> DrainAsync(Channel<ChatEvent> ch)
    {
        ch.Writer.TryComplete();
        var list = new List<ChatEvent>();
        await foreach (var e in ch.Reader.ReadAllAsync())
        {
            list.Add(e);
        }

        return list;
    }

    private (ToolRouter Router, ScriptedToolbox Box, ScriptedBroker Broker) NewRouter(
        PermissionDecision brokerDecision = PermissionDecision.Allow,
        params ToolDefinition[] definitions)
    {
        var defs = definitions.Length > 0
            ? definitions
            : new[] { new ToolDefinition { Name = "get_note", Description = "读取笔记" } };
        var box = new ScriptedToolbox("notes", defs);
        var registry = new ToolboxRegistry();
        registry.Register(box);
        var broker = new ScriptedBroker(brokerDecision);
        return (new ToolRouter(registry, PermissionPolicy.CreateDefault(new EventBus()), broker), box, broker);
    }

    [Fact]
    public async Task TwoTurnLoop_PersistsAllTurns_AndRefeedsToolResults()
    {
        var provider = AddProvider("tooler");
        provider.ResponseQueue.Enqueue(ToolCallResponse("call-1", "get_note", "{\"id\":\"n1\"}",
            new UsageInfo { PromptTokens = 100, CompletionTokens = 10 }));
        provider.ResponseQueue.Enqueue(FinalResponse("笔记内容如下",
            new UsageInfo { PromptTokens = 200, CompletionTokens = 20 }));

        var (router, box, _) = NewRouter();
        box.SetResult("get_note", ToolInvokeResult.Ok("NOTE_BODY"));
        // get_note 未知于默认策略 → Ask → broker Allow
        var loopRunner = new WorkerRunner(Sessions, Catalog, tools: router);

        var (ctx, assignment, profile) = await SetupAsync("tooler", provider);
        var (sink, writer) = NewSink();

        var outcome = await loopRunner.RunAsync(
            ctx, assignment, StrategyRole.Worker,
            parentMessageId: null, label: "读取任务",
            Prompt, stream: false, isFinal: true,
            sink: writer, budget: null, CancellationToken.None);

        // ---- 结果：最终答复 + 跨轮累计用量与成本 ----
        Assert.True(outcome.Succeeded);
        Assert.Equal("笔记内容如下", outcome.Content);
        Assert.Equal(300, outcome.TokensIn);
        Assert.Equal(30, outcome.TokensOut);
        var expectedCost = (CostTracker.Estimate(profile, 100, 10)
            + CostTracker.Estimate(profile, 200, 20)) ?? 0.0;
        Assert.Equal(expectedCost, outcome.CostUsd, 10);

        // ---- 落库形态：占位最终答复 + 助手 tool_calls 轮 + tool 结果 ----
        var messages = (await Sessions.GetMessagesAsync(ctx.Session.Id)).Value!;
        Assert.Equal(3, messages.Count);

        var final = Assert.Single(messages, m => m.IsFinal == true);
        Assert.Equal("笔记内容如下", final.Content);
        Assert.Equal(MessageStatus.Completed, final.Status);
        Assert.Equal(StrategyRole.Worker, final.OrchestrationRole);

        var turn = Assert.Single(messages, m => m.Role == ChatRole.Assistant && m.IsFinal == false);
        Assert.NotNull(turn.ToolCallsJson);
        Assert.Contains("get_note", turn.ToolCallsJson);
        Assert.Contains("call-1", turn.ToolCallsJson);
        Assert.Equal(final.Id, turn.ParentMessageId);
        Assert.Equal(MessageStatus.Completed, turn.Status);
        Assert.Equal(100, turn.TokensIn);

        var tool = Assert.Single(messages, m => m.Role == ChatRole.Tool);
        Assert.Equal("get_note", tool.Name);
        Assert.Equal("call-1", tool.ToolCallId);
        Assert.Equal("NOTE_BODY", tool.Content);
        Assert.Equal(MessageStatus.Completed, tool.Status);
        Assert.Equal(turn.Id, tool.ParentMessageId);

        // ---- 第二轮回灌：user + assistant(tool_calls) + tool 结果 ----
        var reFed = provider.LastRequestMessages!;
        Assert.Equal(3, reFed.Count);
        Assert.Equal("user", reFed[0].Role);
        Assert.Equal("assistant", reFed[1].Role);
        Assert.NotNull(reFed[1].ToolCalls);
        Assert.Equal("get_note", reFed[1].ToolCalls![0].FunctionName);
        Assert.Equal("tool", reFed[2].Role);
        Assert.Equal("NOTE_BODY", reFed[2].Content);
        Assert.Equal("call-1", reFed[2].ToolCallId);
        Assert.Equal("get_note", reFed[2].Name);

        // 请求携带了工具定义
        Assert.NotNull(provider.LastRequest!.Tools);
        Assert.Equal("get_note", Assert.Single(provider.LastRequest.Tools!).Name);

        // ---- 事件流：工具开始/结束事件齐备 ----
        var events = await DrainAsync(sink);
        var started = Assert.Single(events.OfType<ToolCallStartedEvent>());
        Assert.Equal("get_note", started.ToolName);
        Assert.Equal("call-1", started.ToolCallId);
        Assert.Equal(tool.Id, started.MessageId);
        Assert.Equal("{\"id\":\"n1\"}", started.ArgumentsJson);
        var completed = Assert.Single(events.OfType<ToolCallCompletedEvent>());
        Assert.True(completed.Success);
        Assert.False(completed.Denied);
        Assert.Equal(tool.Id, completed.MessageId);
        Assert.Empty(events.OfType<MessageFailedEvent>());
    }

    [Fact]
    public async Task TwoTurnLoop_NoFailedEvents()
    {
        var provider = AddProvider("tooler2");
        provider.ResponseQueue.Enqueue(ToolCallResponse("call-1", "get_note", "{}", null));
        provider.ResponseQueue.Enqueue(FinalResponse("完成", null));
        var (router, box, _) = NewRouter();
        box.SetResult("get_note", ToolInvokeResult.Ok("ok"));
        var loopRunner = new WorkerRunner(Sessions, Catalog, tools: router);
        var (ctx, assignment, _) = await SetupAsync("tooler2", provider);
        var (sink, writer) = NewSink();

        var outcome = await loopRunner.RunAsync(
            ctx, assignment, StrategyRole.Worker, null, null,
            Prompt, stream: false, isFinal: true, writer, null, CancellationToken.None);

        Assert.True(outcome.Succeeded);
        var events = await DrainAsync(sink);
        Assert.Empty(events.OfType<MessageFailedEvent>());
        Assert.Equal(2, events.OfType<AssistantMessageStarted>().Count()); // 占位最终答复 + 工具轮
        Assert.Equal(2, events.OfType<MessageCompletedEvent>().Count());   // 工具轮 + 最终答复
    }

    [Fact]
    public async Task DeniedTool_DegradedRow_ModelSeesHonestDenial()
    {
        var provider = AddProvider("denier");
        provider.ResponseQueue.Enqueue(ToolCallResponse("call-d", "get_note", "{}", null));
        provider.ResponseQueue.Enqueue(FinalResponse("抱歉，无法读取", null));
        var (router, box, _) = NewRouter();
        box.SetResult("get_note", ToolInvokeResult.Ok("SECRET"));

        // 用户显式拒绝该工具
        var policy = PermissionPolicy.CreateDefault(new EventBus());
        policy.SetDefaultDecision("get_note", PermissionDecision.Deny);
        var registry = new ToolboxRegistry();
        registry.Register(box);
        var deniedRouter = new ToolRouter(registry, policy, new ScriptedBroker(PermissionDecision.Allow));
        var loopRunner = new WorkerRunner(Sessions, Catalog, tools: deniedRouter);

        var (ctx, assignment, _) = await SetupAsync("denier", provider);
        var outcome = await loopRunner.RunAsync(
            ctx, assignment, StrategyRole.Worker, null, null,
            Prompt, stream: false, isFinal: true, null, null, CancellationToken.None);

        Assert.True(outcome.Succeeded); // 拒绝不是失败：模型看到原因后正常收尾
        Assert.Equal("抱歉，无法读取", outcome.Content);
        Assert.Empty(box.Invocations); // 工具从未真正执行

        var messages = (await Sessions.GetMessagesAsync(ctx.Session.Id)).Value!;
        var tool = Assert.Single(messages, m => m.Role == ChatRole.Tool);
        Assert.Equal(MessageStatus.Degraded, tool.Status);
        Assert.Contains("Permission denied", tool.Content);
        Assert.NotNull(tool.Error);

        // 模型在第二轮确实看到了拒绝原因
        var reFed = Assert.Single(provider.LastRequestMessages!, m => m.Role == "tool");
        Assert.Contains("Permission denied", reFed.Content);
        Assert.DoesNotContain("SECRET", reFed.Content);
    }

    [Fact]
    public async Task UnknownToolName_HonestFailureRow_LoopContinues()
    {
        var provider = AddProvider("hallucinator");
        provider.ResponseQueue.Enqueue(ToolCallResponse("call-g", "ghost_tool", "{}", null));
        provider.ResponseQueue.Enqueue(FinalResponse("换个方式完成", null));
        var (router, box, broker) = NewRouter(); // 注册表里只有 get_note
        var loopRunner = new WorkerRunner(Sessions, Catalog, tools: router);

        var (ctx, assignment, _) = await SetupAsync("hallucinator", provider);
        var outcome = await loopRunner.RunAsync(
            ctx, assignment, StrategyRole.Worker, null, null,
            Prompt, stream: false, isFinal: true, null, null, CancellationToken.None);

        Assert.True(outcome.Succeeded);
        var messages = (await Sessions.GetMessagesAsync(ctx.Session.Id)).Value!;
        var tool = Assert.Single(messages, m => m.Role == ChatRole.Tool);
        Assert.Equal(MessageStatus.Degraded, tool.Status);
        Assert.Contains("ghost_tool", tool.Content);
        Assert.Contains("not found", tool.Content);
        Assert.Empty(box.Invocations);
        Assert.Single(broker.Consultations); // Ask 裁决照样走了授权链
    }

    [Fact]
    public async Task MaxTurnsExceeded_HonestAbort()
    {
        var provider = AddProvider("looper");
        for (var i = 0; i < WorkerRunner.MaxToolTurns + 1; i++)
        {
            provider.ResponseQueue.Enqueue(ToolCallResponse($"call-{i}", "get_note", "{}", null));
        }

        var (router, box, _) = NewRouter();
        box.SetResult("get_note", ToolInvokeResult.Ok("ok"));
        var loopRunner = new WorkerRunner(Sessions, Catalog, tools: router);

        var (ctx, assignment, _) = await SetupAsync("looper", provider);
        var (sink, writer) = NewSink();

        var outcome = await loopRunner.RunAsync(
            ctx, assignment, StrategyRole.Worker, null, null,
            Prompt, stream: false, isFinal: true, writer, null, CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.False(outcome.Cancelled);
        Assert.Contains("exceeded the limit", outcome.Error);

        var messages = (await Sessions.GetMessagesAsync(ctx.Session.Id)).Value!;
        var final = Assert.Single(messages, m => m.IsFinal == true);
        Assert.Equal(MessageStatus.Failed, final.Status);
        Assert.Equal(WorkerRunner.MaxToolTurns, messages.Count(m => m.Role == ChatRole.Tool));

        var events = await DrainAsync(sink);
        var failed = Assert.Single(events.OfType<MessageFailedEvent>());
        Assert.Contains("exceeded the limit", failed.Error);

        // 循环失控属于模型质量问题：如实计入失败统计
        var profile = Catalog.Find("looper", string.Empty);
        Assert.Equal(1, profile!.Stats.Failures);
    }

    [Fact]
    public async Task ProviderFailureMidLoop_FailsRun_KeepsCompletedTurns()
    {
        var provider = AddProvider("flaky-loop");
        provider.ResponseQueue.Enqueue(ToolCallResponse("call-1", "get_note", "{}", null));
        provider.ThrowWhenResponseQueueEmpty = new InvalidOperationException("turn2 网络中断");

        var (router, box, _) = NewRouter();
        box.SetResult("get_note", ToolInvokeResult.Ok("ok"));
        var loopRunner = new WorkerRunner(Sessions, Catalog, tools: router);

        var (ctx, assignment, _) = await SetupAsync("flaky-loop", provider);
        var outcome = await loopRunner.RunAsync(
            ctx, assignment, StrategyRole.Worker, null, null,
            Prompt, stream: false, isFinal: true, null, null, CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Contains("turn2 网络中断", outcome.Error);

        // 已完成的中间轮保持 Completed（事实源不被后续失败涂改）
        var messages = (await Sessions.GetMessagesAsync(ctx.Session.Id)).Value!;
        var final = Assert.Single(messages, m => m.IsFinal == true);
        Assert.Equal(MessageStatus.Failed, final.Status);
        Assert.Equal(MessageStatus.Completed,
            Assert.Single(messages, m => m.Role == ChatRole.Assistant && m.IsFinal == false).Status);
        Assert.Equal(MessageStatus.Completed,
            Assert.Single(messages, m => m.Role == ChatRole.Tool).Status);

        var profile = Catalog.Find("flaky-loop", string.Empty);
        Assert.Equal(1, profile!.Stats.Failures);
    }

    [Fact]
    public async Task ProviderFailureMidLoop_SpentCostStillRecordedInBudget()
    {
        // 预算纪律回归（Reviewer-A P1-2）：turn1 已真实计价，turn2 provider 中断——
        // 花掉的钱必须记入 TurnBudget，否则同轮后续 worker/judge 会再次花满预算，
        // 静默突破用户单轮上限；outcome.CostUsd 同样如实返回而非 0。
        var provider = AddProvider("budget-loop");
        provider.ResponseQueue.Enqueue(ToolCallResponse("call-1", "get_note", "{}",
            new UsageInfo { PromptTokens = 1000, CompletionTokens = 500 }));
        provider.ThrowWhenResponseQueueEmpty = new InvalidOperationException("turn2 网络中断");

        var (router, box, _) = NewRouter();
        box.SetResult("get_note", ToolInvokeResult.Ok("ok"));
        var loopRunner = new WorkerRunner(Sessions, Catalog, tools: router);

        var (ctx, assignment, profile) = await SetupAsync("budget-loop", provider);
        var budget = new TurnBudget(1.0);

        var outcome = await loopRunner.RunAsync(
            ctx, assignment, StrategyRole.Worker, null, null,
            Prompt, stream: false, isFinal: true, null, budget, CancellationToken.None);

        Assert.False(outcome.Succeeded);
        var turn1Cost = CostTracker.Estimate(profile, 1000, 500);
        Assert.NotNull(turn1Cost);
        Assert.True(turn1Cost > 0);
        Assert.Equal(turn1Cost!.Value, budget.SpentUsd, 10);   // 失败路径如实记账
        Assert.Equal(turn1Cost.Value, outcome.CostUsd, 10);    // 汇总不再谎报 0
        Assert.True(budget.HasBudget);                          // 1.0 上限内仍有余额
    }

    [Fact]
    public async Task MaxTurnsExceeded_AllTurnCostsRecordedInBudget()
    {
        // 超轮数中止同样不得丢弃已发生成本：MaxToolTurns 轮全部计价入账。
        var provider = AddProvider("runaway");
        for (var i = 0; i < WorkerRunner.MaxToolTurns + 1; i++)
        {
            provider.ResponseQueue.Enqueue(ToolCallResponse($"call-{i}", "get_note", "{}",
                new UsageInfo { PromptTokens = 100, CompletionTokens = 10 }));
        }

        var (router, box, _) = NewRouter();
        box.SetResult("get_note", ToolInvokeResult.Ok("ok"));
        var loopRunner = new WorkerRunner(Sessions, Catalog, tools: router);

        var (ctx, assignment, profile) = await SetupAsync("runaway", provider);
        var budget = new TurnBudget(10.0);

        var outcome = await loopRunner.RunAsync(
            ctx, assignment, StrategyRole.Worker, null, null,
            Prompt, stream: false, isFinal: true, null, budget, CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Contains("exceeded the limit", outcome.Error);
        var perTurn = CostTracker.Estimate(profile, 100, 10);
        Assert.NotNull(perTurn);
        var expected = perTurn!.Value * WorkerRunner.MaxToolTurns;
        Assert.Equal(expected, budget.SpentUsd, 10);
        Assert.Equal(expected, outcome.CostUsd, 10);
    }

    [Fact]
    public async Task CancelDuringToolInvoke_CancelledRows_NoStatsPollution()
    {
        var provider = AddProvider("slow-tool");
        provider.ResponseQueue.Enqueue(ToolCallResponse("call-c", "get_note", "{}", null));

        var (router, box, _) = NewRouter();
        box.DelayMs = 5000; // 工具执行远超取消时点
        var loopRunner = new WorkerRunner(Sessions, Catalog, tools: router);

        var (ctx, assignment, _) = await SetupAsync("slow-tool", provider);
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(50);

        var outcome = await loopRunner.RunAsync(
            ctx, assignment, StrategyRole.Worker, null, null,
            Prompt, stream: false, isFinal: true, null, null, cts.Token);

        Assert.True(outcome.Cancelled);
        Assert.Equal("cancelled by user", outcome.Error);
        Assert.Equal(0.0, outcome.CostUsd);

        var messages = (await Sessions.GetMessagesAsync(ctx.Session.Id)).Value!;
        Assert.Equal(MessageStatus.Cancelled,
            Assert.Single(messages, m => m.IsFinal == true).Status);
        Assert.Equal(MessageStatus.Cancelled,
            Assert.Single(messages, m => m.Role == ChatRole.Tool).Status); // 不留 Pending 僵尸

        var profile = Catalog.Find("slow-tool", string.Empty);
        Assert.Equal(0, profile!.Stats.Calls);
        Assert.Equal(0, profile.Stats.Failures);
    }

    [Fact]
    public async Task ToolsDisabled_ViaMoaOptions_SkipsToolLoop()
    {
        var provider = AddProvider("no-tools");
        provider.NonStreamContent = "普通答复";
        var (router, box, _) = NewRouter();
        var loopRunner = new WorkerRunner(
            Sessions, Catalog, tools: router, options: new MoaOptions { ToolsEnabled = false });

        var (ctx, assignment, _) = await SetupAsync("no-tools", provider);
        var outcome = await loopRunner.RunAsync(
            ctx, assignment, StrategyRole.Worker, null, null,
            Prompt, stream: false, isFinal: true, null, null, CancellationToken.None);

        Assert.True(outcome.Succeeded);
        Assert.Equal("普通答复", outcome.Content);
        Assert.Null(provider.LastRequest!.Tools); // 请求不携带 tools
        Assert.Empty(box.Invocations);            // 工具箱从未被触碰

        var messages = (await Sessions.GetMessagesAsync(ctx.Session.Id)).Value!;
        Assert.Single(messages); // 只有最终答复，无工具轮次
        Assert.Equal(MessageStatus.Completed, messages[0].Status);
    }

    [Fact]
    public async Task ToolsEnabledExplicitlyTrue_LoopStillRuns()
    {
        var provider = AddProvider("tools-on");
        provider.ResponseQueue.Enqueue(ToolCallResponse("call-1", "get_note", "{}", null));
        provider.ResponseQueue.Enqueue(FinalResponse("工具答复", null));
        var (router, box, _) = NewRouter();
        box.SetResult("get_note", ToolInvokeResult.Ok("ok"));
        var loopRunner = new WorkerRunner(
            Sessions, Catalog, tools: router, options: new MoaOptions { ToolsEnabled = true });

        var (ctx, assignment, _) = await SetupAsync("tools-on", provider);
        var outcome = await loopRunner.RunAsync(
            ctx, assignment, StrategyRole.Worker, null, null,
            Prompt, stream: false, isFinal: true, null, null, CancellationToken.None);

        Assert.True(outcome.Succeeded);
        Assert.Equal("工具答复", outcome.Content);
        Assert.Single(box.Invocations); // 工具循环真实执行
    }
}
