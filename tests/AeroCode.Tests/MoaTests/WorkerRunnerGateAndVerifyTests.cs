// Copyright (c) AeroCode
// R2 波次 γ 集成行为验证：
// F-M1 —— 主循环（WorkerRunner 工具循环）每轮真实 usage 实报 mission 级 token 闸门（BudgetState 翻转）；
// C2 —— WorkerRunner / SubAgentRunner 完成判定链：拒绝 → 有界重试 → 按策略收敛，
//       重试轮的真实用量/成本照常核算（TurnBudget + 闸门），预算耗尽不再重试（不绕过预算）。
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AeroAgent.Conversation.Models;
using AeroAgent.Conversation.Orchestration;
using AeroAgent.Moa.Accounting;
using AeroAgent.Moa.Assignment;
using AeroAgent.Moa.Budget;
using AeroAgent.Moa.Profiles;
using AeroAgent.Moa.Strategies;
using AeroAgent.Moa.Subagent;
using AeroAgent.Moa.Tools;
using AeroAgent.Moa.Verify;
using AeroCode.AI.Configuration;
using AeroCode.AI.Models;
using AeroCode.AI.Providers;
using AeroCode.Harness.EventBus;
using AeroCode.Harness.Permission;
using AeroCode.Tests.ConversationTests;
using Xunit;
using AiChatMessage = AeroCode.AI.Models.ChatMessage;
using ChatMessage = AeroAgent.Conversation.Models.ChatMessage;

namespace AeroCode.Tests.MoaTests;

/// <summary>
/// 计数包装 provider：委托给脚本化 provider 的同时记录 ChatAsync 调用次数与完整请求
/// （断言 critique 重试轮的消息形态与「重试没有发生」用）。
/// </summary>
internal sealed class CountingProvider : IAiProvider
{
    private readonly ScriptedProvider _inner;

    public CountingProvider(ScriptedProvider inner)
    {
        _inner = inner;
        ProviderId = inner.ProviderId;
    }

    public string ProviderId { get; }
    public string DisplayName => _inner.DisplayName;
    public ProviderKind Kind => _inner.Kind;
    public bool SupportsStreaming => _inner.SupportsStreaming;
    public bool SupportsToolCalling => _inner.SupportsToolCalling;
    public bool SupportsThinking => _inner.SupportsThinking;

    public int ChatCalls { get; private set; }

    /// <summary>历次 ChatAsync 收到的完整请求（按调用次序）。</summary>
    public List<ChatRequest> Requests { get; } = new();

    public async Task<ChatResponse> ChatAsync(ChatRequest request, CancellationToken ct = default)
    {
        ChatCalls++;
        Requests.Add(request);
        return await _inner.ChatAsync(request, ct);
    }

    public IAsyncEnumerable<ChatChunk> StreamChatAsync(ChatRequest request, CancellationToken ct = default)
        => _inner.StreamChatAsync(request, ct);

    public Task<bool> HealthCheckAsync(CancellationToken ct = default) => _inner.HealthCheckAsync(ct);
}

public sealed class WorkerRunnerGateAndVerifyTests : MoaTestBase
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

    private (ToolRouter Router, ScriptedToolbox Box) NewRouter()
    {
        var box = new ScriptedToolbox("notes", new ToolDefinition { Name = "get_note", Description = "读取笔记" });
        var registry = new ToolboxRegistry();
        registry.Register(box);
        var router = new ToolRouter(registry, PermissionPolicy.CreateDefault(new EventBus()), new ScriptedBroker(PermissionDecision.Allow));
        return (router, box);
    }

    private async Task<(OrchestrationContext Ctx, ModelAssignment Assignment, ModelProfile Profile)> SetupAsync(string providerId)
    {
        var profile = SetProfile(providerId, new[] { ModelStrength.General }, costPerMIn: 1.0, costPerMOut: 2.0);
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

    private static SubAgentSpec Spec(string providerId, double maxCostUsd = 0)
        => new(
            Description: "probe-subagent",
            Prompt: "子任务：读取笔记并汇总",
            ProviderId: providerId,
            Model: string.Empty,
            Depth: 1,
            MaxTurns: 8,
            MaxCostUsd: maxCostUsd,
            ParallelSafe: true);

    // ---------- F-M1：主循环 token 入 mission 闸门 ----------

    [Fact]
    public async Task MainLoop_EveryTurnReportsRealUsage_GateFlipsToExhausted()
    {
        var scripted = new ScriptedProvider { ProviderId = "gate-loop" };
        Registry.Add(scripted);
        scripted.ResponseQueue.Enqueue(ToolCallResponse("call-1", "get_note", "{}",
            new UsageInfo { PromptTokens = 100, CompletionTokens = 10 }));
        scripted.ResponseQueue.Enqueue(FinalResponse("done",
            new UsageInfo { PromptTokens = 200, CompletionTokens = 20 }));

        var (router, box) = NewRouter();
        box.SetResult("get_note", ToolInvokeResult.Ok("NOTE_BODY"));
        var gate = new TokenBudgetGate(50); // 110 + 220 = 330 ≥ 50 → Exhausted
        var runner = new WorkerRunner(Sessions, Catalog, tools: router) { BudgetGate = gate };

        var (ctx, assignment, _) = await SetupAsync("gate-loop");
        var outcome = await runner.RunAsync(
            ctx, assignment, StrategyRole.Worker, null, null,
            Prompt, stream: false, isFinal: true, null, null, CancellationToken.None);

        Assert.True(outcome.Succeeded);
        // R1 遗留修复：主循环（此前只有子代理侧实报）每轮真实 usage 进入闸门
        Assert.Equal(330, gate.SpentTokens);
        Assert.Equal(BudgetState.Exhausted, gate.State);
        Assert.True(gate.DegradedToSingleAgent);
    }

    [Fact]
    public async Task MainLoop_WarningZoneReported_BeforeExhaustion()
    {
        var scripted = new ScriptedProvider { ProviderId = "gate-warn" };
        Registry.Add(scripted);
        scripted.ResponseQueue.Enqueue(ToolCallResponse("call-1", "get_note", "{}",
            new UsageInfo { PromptTokens = 100, CompletionTokens = 10 }));
        scripted.ResponseQueue.Enqueue(ToolCallResponse("call-2", "get_note", "{}",
            new UsageInfo { PromptTokens = 100, CompletionTokens = 10 }));
        scripted.ResponseQueue.Enqueue(FinalResponse("done", usage: null));

        var (router, box) = NewRouter();
        box.SetResult("get_note", ToolInvokeResult.Ok("ok"));
        var gate = new TokenBudgetGate(260); // 110 → Running；220 ≥ 208(80%) → Warning
        var runner = new WorkerRunner(Sessions, Catalog, tools: router) { BudgetGate = gate };

        var (ctx, assignment, _) = await SetupAsync("gate-warn");
        var outcome = await runner.RunAsync(
            ctx, assignment, StrategyRole.Worker, null, null,
            Prompt, stream: false, isFinal: true, null, null, CancellationToken.None);

        Assert.True(outcome.Succeeded);
        Assert.Equal(220, gate.SpentTokens);
        Assert.Equal(BudgetState.Warning, gate.State);
        Assert.False(gate.DegradedToSingleAgent);
    }

    // ---------- C2：WorkerRunner 完成判定链 ----------

    [Fact]
    public async Task WorkerLoop_VerifierRejectThenAccept_RetryAccountingHonest()
    {
        var scripted = new ScriptedProvider { ProviderId = "verify-loop" };
        var counting = new CountingProvider(scripted);
        Registry.Add(counting);
        scripted.ResponseQueue.Enqueue(ToolCallResponse("call-1", "get_note", "{}",
            new UsageInfo { PromptTokens = 100, CompletionTokens = 10 }));
        scripted.ResponseQueue.Enqueue(FinalResponse("draft v1",
            new UsageInfo { PromptTokens = 200, CompletionTokens = 20 }));
        scripted.ResponseQueue.Enqueue(FinalResponse("fixed v2",
            new UsageInfo { PromptTokens = 300, CompletionTokens = 30 }));

        var (router, box) = NewRouter();
        box.SetResult("get_note", ToolInvokeResult.Ok("NOTE_BODY"));
        var verifier = new StubVerifier(
            CompletionVerdict.Reject("missing detail", "补充细节 X"),
            CompletionVerdict.Accept("ok now"));
        var budget = new TurnBudget(10.0);
        var runner = new WorkerRunner(Sessions, Catalog, tools: router)
        {
            CompletionVerifier = verifier,
        };

        var (ctx, assignment, profile) = await SetupAsync("verify-loop");
        var outcome = await runner.RunAsync(
            ctx, assignment, StrategyRole.Worker, null, null,
            Prompt, stream: false, isFinal: true, null, budget, CancellationToken.None);

        Assert.True(outcome.Succeeded);
        Assert.Equal("fixed v2", outcome.Content);

        // 重试产出真实发生：第 3 次 provider 调用 = 修正轮（纯文本，不带工具）
        Assert.Equal(3, counting.ChatCalls);
        var retryRequest = counting.Requests[^1];
        Assert.Null(retryRequest.Tools);
        Assert.Contains(retryRequest.Messages, m => m.Role == "assistant" && m.Content == "draft v1");
        Assert.Contains(retryRequest.Messages, m => m.Role == "user"
            && (m.Content ?? string.Empty).Contains("[completion-critique]")
            && (m.Content ?? string.Empty).Contains("补充细节 X"));

        // 判定不信自报：佐证 = 工具输出（产出路径之外的独立观测），目标 = 任务文本
        Assert.Equal(2, verifier.Requests.Count);
        Assert.Equal(0, verifier.Requests[0].Round);
        Assert.Equal("draft v1", verifier.Requests[0].Output);
        Assert.Equal("帮我读笔记", verifier.Requests[0].TaskGoal);
        Assert.Equal("NOTE_BODY", Assert.Single(verifier.Requests[0].Evidence));
        Assert.Equal(1, verifier.Requests[1].Round);
        Assert.Equal("fixed v2", verifier.Requests[1].Output);

        // 消耗计入既有预算语义不绕过：跨轮 + 重试轮全部真实核算
        Assert.Equal(600, outcome.TokensIn);
        Assert.Equal(60, outcome.TokensOut);
        var expectedCost = (CostTracker.Estimate(profile, 100, 10)
            + CostTracker.Estimate(profile, 200, 20)
            + CostTracker.Estimate(profile, 300, 30)) ?? 0.0;
        Assert.Equal(expectedCost, outcome.CostUsd, 10);
        Assert.Equal(expectedCost, budget.SpentUsd, 10);
    }

    [Fact]
    public async Task WorkerLoop_VerifierFailPolicy_HonestFailureTerminal()
    {
        var scripted = new ScriptedProvider { ProviderId = "verify-fail" };
        var counting = new CountingProvider(scripted);
        Registry.Add(counting);
        scripted.ResponseQueue.Enqueue(ToolCallResponse("call-1", "get_note", "{}", null));
        scripted.ResponseQueue.Enqueue(FinalResponse("draft v1", null));
        scripted.ResponseQueue.Enqueue(FinalResponse("fixed v2", null));

        var (router, box) = NewRouter();
        box.SetResult("get_note", ToolInvokeResult.Ok("ok"));
        var verifier = new StubVerifier(
            CompletionVerdict.Reject("bad r0", "fix a"),
            CompletionVerdict.Reject("bad r1", "fix b"));
        var runner = new WorkerRunner(Sessions, Catalog, tools: router)
        {
            CompletionVerifier = verifier,
            CritiqueOptions = new CritiqueLoopOptions { ConvergencePolicy = CritiqueConvergencePolicy.Fail },
        };

        var (ctx, assignment, _) = await SetupAsync("verify-fail");
        var outcome = await runner.RunAsync(
            ctx, assignment, StrategyRole.Worker, null, null,
            Prompt, stream: false, isFinal: true, null, null, CancellationToken.None);

        // 2 轮 critique 耗尽仍有拒绝 → Fail 策略诚实失败（不冒充完成）
        Assert.False(outcome.Succeeded);
        Assert.Contains("completion verification failed after 2 critique round(s)", outcome.Error);

        // 末轮拒绝后不再重试：turn1 + 最终答复 + 1 次修正 = 3 次调用
        Assert.Equal(3, counting.ChatCalls);
        Assert.Equal(2, verifier.Requests.Count);

        var messages = (await Sessions.GetMessagesAsync(ctx.Session.Id)).Value!;
        var final = Assert.Single(messages, m => m.IsFinal == true);
        Assert.Equal(MessageStatus.Failed, final.Status);
        Assert.Contains("completion verification failed", final.Error);
    }

    [Fact]
    public async Task WorkerLoop_VerifierDefaultPolicy_ConvergedAccept_StillSucceeds()
    {
        var scripted = new ScriptedProvider { ProviderId = "verify-conv" };
        var counting = new CountingProvider(scripted);
        Registry.Add(counting);
        scripted.ResponseQueue.Enqueue(ToolCallResponse("call-1", "get_note", "{}", null));
        scripted.ResponseQueue.Enqueue(FinalResponse("draft v1", null));
        scripted.ResponseQueue.Enqueue(FinalResponse("fixed v2", null));

        var (router, box) = NewRouter();
        box.SetResult("get_note", ToolInvokeResult.Ok("ok"));
        var verifier = new StubVerifier(
            CompletionVerdict.Reject("bad r0", "fix a"),
            CompletionVerdict.Reject("bad r1", "fix b")); // 默认 AcceptWithFindings
        var runner = new WorkerRunner(Sessions, Catalog, tools: router)
        {
            CompletionVerifier = verifier,
        };

        var (ctx, assignment, _) = await SetupAsync("verify-conv");
        var outcome = await runner.RunAsync(
            ctx, assignment, StrategyRole.Worker, null, null,
            Prompt, stream: false, isFinal: true, null, null, CancellationToken.None);

        // 保活语义：轮耗尽按默认策略收敛（任务不失败），产出为最后被 critique 的候选
        Assert.True(outcome.Succeeded);
        Assert.Equal("fixed v2", outcome.Content);
        Assert.Equal(3, counting.ChatCalls); // turn1 + 最终答复 + 1 次修正
        Assert.Equal(2, verifier.Requests.Count);
    }

    [Fact]
    public async Task WorkerLoop_VerifierNotWired_CurrentBehaviorUnchanged()
    {
        var scripted = new ScriptedProvider { ProviderId = "verify-null" };
        var counting = new CountingProvider(scripted);
        Registry.Add(counting);
        scripted.ResponseQueue.Enqueue(ToolCallResponse("call-1", "get_note", "{}", null));
        scripted.ResponseQueue.Enqueue(FinalResponse("done", null));

        var (router, box) = NewRouter();
        box.SetResult("get_note", ToolInvokeResult.Ok("ok"));
        var runner = new WorkerRunner(Sessions, Catalog, tools: router); // CompletionVerifier = null

        var (ctx, assignment, _) = await SetupAsync("verify-null");
        var outcome = await runner.RunAsync(
            ctx, assignment, StrategyRole.Worker, null, null,
            Prompt, stream: false, isFinal: true, null, null, CancellationToken.None);

        // null = 现行为：无 critique、无重试
        Assert.True(outcome.Succeeded);
        Assert.Equal("done", outcome.Content);
        Assert.Equal(2, counting.ChatCalls);
    }

    // ---------- C2：SubAgentRunner 完成判定链 ----------

    [Fact]
    public async Task SubAgent_VerifierRejectThenAccept_RetryAccountedIntoGate()
    {
        var scripted = new ScriptedProvider { ProviderId = "sa-verify" };
        var counting = new CountingProvider(scripted);
        Registry.Add(counting);
        SetProfile("sa-verify", new[] { ModelStrength.General }, costPerMIn: 1.0, costPerMOut: 2.0);
        scripted.ResponseQueue.Enqueue(ToolCallResponse("c1", "get_note", "{}",
            new UsageInfo { PromptTokens = 10, CompletionTokens = 1 }));
        scripted.ResponseQueue.Enqueue(FinalResponse("draft v1",
            new UsageInfo { PromptTokens = 20, CompletionTokens = 2 }));
        scripted.ResponseQueue.Enqueue(FinalResponse("fixed v2",
            new UsageInfo { PromptTokens = 30, CompletionTokens = 3 }));

        var verifier = new StubVerifier(
            CompletionVerdict.Reject("missing detail", "补充细节 X"),
            CompletionVerdict.Accept("ok now"));
        var gate = new TokenBudgetGate(10_000);
        var runner = new SubAgentRunner(
            Sessions, Registry, Catalog, new EventBus(),
            tools: NewRouter().Router,
            budgetGate: gate,
            completionVerifier: verifier);

        var handle = await runner.LaunchAsync(Spec("sa-verify"), CancellationToken.None);
        var summary = await handle.WaitAsync(CancellationToken.None);
        await handle.DisposeAsync();

        Assert.Equal("fixed v2", summary); // 通过验证：无 [verification] 标注

        // 重试轮真实发生且全套核算：任务预算（隐含）+ mission 闸门逐轮实报（11 + 22 + 33）
        Assert.Equal(3, counting.ChatCalls);
        Assert.Equal(66, gate.SpentTokens);
        var retryRequest = counting.Requests[^1];
        Assert.Null(retryRequest.Tools);
        Assert.Contains(retryRequest.Messages, m => m.Role == "user"
            && (m.Content ?? string.Empty).Contains("[completion-critique]"));
    }

    [Fact]
    public async Task SubAgent_VerifierFailPolicy_HonestFailure()
    {
        var scripted = new ScriptedProvider { ProviderId = "sa-verify-fail" };
        var counting = new CountingProvider(scripted);
        Registry.Add(counting);
        SetProfile("sa-verify-fail", new[] { ModelStrength.General });
        scripted.ResponseQueue.Enqueue(ToolCallResponse("c1", "get_note", "{}", null));
        scripted.ResponseQueue.Enqueue(FinalResponse("draft v1", null));
        scripted.ResponseQueue.Enqueue(FinalResponse("fixed v2", null));

        var verifier = new StubVerifier(
            CompletionVerdict.Reject("bad r0", "fix a"),
            CompletionVerdict.Reject("bad r1", "fix b"));
        var runner = new SubAgentRunner(
            Sessions, Registry, Catalog, new EventBus(),
            tools: NewRouter().Router,
            completionVerifier: verifier,
            critiqueOptions: new CritiqueLoopOptions { ConvergencePolicy = CritiqueConvergencePolicy.Fail });

        var handle = await runner.LaunchAsync(Spec("sa-verify-fail"), CancellationToken.None);
        var summary = await handle.WaitAsync(CancellationToken.None);
        await handle.DisposeAsync();

        Assert.Contains("completion verification failed after 2 critique round(s)", summary);
        Assert.Equal(3, counting.ChatCalls); // 末轮拒绝后不再重试
        Assert.Equal(2, verifier.Requests.Count);
    }

    [Fact]
    public async Task SubAgent_BudgetExhaustedAtCritique_NoRetry_HonestConverge()
    {
        var scripted = new ScriptedProvider { ProviderId = "sa-verify-budget" };
        var counting = new CountingProvider(scripted);
        Registry.Add(counting);
        SetProfile("sa-verify-budget", new[] { ModelStrength.General }, costPerMIn: 1.0, costPerMOut: 0.0);
        // 每轮真实成本 $1.0；spec 上限 $1.5：turn1 后 1.0（有余额），最终答复轮后 2.0（耗尽）。
        scripted.ResponseQueue.Enqueue(ToolCallResponse("c1", "get_note", "{}",
            new UsageInfo { PromptTokens = 1_000_000, CompletionTokens = 0 }));
        scripted.ResponseQueue.Enqueue(FinalResponse("draft v1",
            new UsageInfo { PromptTokens = 1_000_000, CompletionTokens = 0 }));

        var verifier = new StubVerifier(CompletionVerdict.Reject("bad", "fix"));
        var runner = new SubAgentRunner(
            Sessions, Registry, Catalog, new EventBus(),
            tools: NewRouter().Router,
            completionVerifier: verifier);

        var handle = await runner.LaunchAsync(Spec("sa-verify-budget", maxCostUsd: 1.5), CancellationToken.None);
        var summary = await handle.WaitAsync(CancellationToken.None);
        await handle.DisposeAsync();

        // 预算耗尽 → reproduce 返回 null → 不再重试，按默认策略收敛并如实标注
        Assert.Equal(2, counting.ChatCalls);
        Assert.Single(verifier.Requests);
        Assert.StartsWith("draft v1", summary);
        Assert.Contains("[verification]", summary);
        Assert.Contains("completion not verified after 1 critique round(s)", summary);
    }

    [Fact]
    public async Task WorkerLoop_InLoopSpendFillsBudget_RetryRefused()
    {
        // R2 修复 MED-2：critique 重试前的预算裁决必须计入本 turn 在途消耗（totalCost）——
        // 修复前只读 HasBudget（SpentUsd 快照，AddActual 要等 turn 终点），在途消耗漏记会放行超限重试。
        // 编排：每轮真实成本 $0.6（prompt 60 万 token × $1/M，输出 0），TurnBudget=$1.0；
        // 最终答复轮后 spent(0,未落账) + 在途 1.2 ≥ 1.0 → reproduce 返回 null → 不再重试。
        var scripted = new ScriptedProvider { ProviderId = "verify-med2" };
        var counting = new CountingProvider(scripted);
        Registry.Add(counting);
        SetProfile("verify-med2", new[] { ModelStrength.General }, costPerMIn: 1.0, costPerMOut: 0.0);
        scripted.ResponseQueue.Enqueue(ToolCallResponse("call-1", "get_note", "{}",
            new UsageInfo { PromptTokens = 600_000, CompletionTokens = 0 }));
        scripted.ResponseQueue.Enqueue(FinalResponse("draft v1",
            new UsageInfo { PromptTokens = 600_000, CompletionTokens = 0 }));

        var (router, box) = NewRouter();
        box.SetResult("get_note", ToolInvokeResult.Ok("ok"));
        var verifier = new StubVerifier(CompletionVerdict.Reject("bad", "fix"));
        var budget = new TurnBudget(1.0);
        var runner = new WorkerRunner(Sessions, Catalog, tools: router)
        {
            CompletionVerifier = verifier,
        };

        var (ctx, assignment, _) = await SetupAsync("verify-med2");
        var outcome = await runner.RunAsync(
            ctx, assignment, StrategyRole.Worker, null, null,
            Prompt, stream: false, isFinal: true, null, budget, CancellationToken.None);

        // 在途消耗入裁决：拒绝重试（修复前会放行第 3 次调用，预算被击穿到 $1.8）
        Assert.Equal(2, counting.ChatCalls);
        Assert.Single(verifier.Requests);
        Assert.True(outcome.Succeeded); // 默认 AcceptWithFindings：不再重试按策略诚实收敛
        Assert.Equal("draft v1", outcome.Content);
        Assert.Equal(1.2, budget.SpentUsd, 10); // 终点 AddActual 如实落账（超额不谎报）
    }
}
