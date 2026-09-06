using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AeroAgent.Conversation.Models;
using AeroAgent.Conversation.Orchestration;
using AeroAgent.Moa.Assignment;
using AeroAgent.Moa.Curation;
using AeroAgent.Moa.LoopGuard;
using AeroAgent.Moa.Profiles;
using AeroAgent.Moa.Strategies;
using AeroAgent.Moa.Tools;
using AeroAgent.Moa.Tools.Workspace;
using AeroCode.AI.Models;
using AeroCode.Harness.Compaction;
using AeroCode.Harness.EventBus;
using AeroCode.Harness.Permission;
using Xunit;
using AiChatMessage = AeroCode.AI.Models.ChatMessage;
using ChatMessage = AeroAgent.Conversation.Models.ChatMessage;

namespace AeroCode.Tests.MoaTests;

/// <summary>A2 目标锚（C-LOOP）纯单元：构造语义与锚定段渲染。</summary>
public sealed class GoalAnchorTests
{
    [Fact]
    public void Create_BlankGoal_ReturnsNull_UnheldSemantics()
    {
        Assert.Null(GoalAnchor.Create(null));
        Assert.Null(GoalAnchor.Create(string.Empty));
        Assert.Null(GoalAnchor.Create("   "));
    }

    [Fact]
    public void Create_TrimsGoal_FiltersBlankCriteria()
    {
        var anchor = GoalAnchor.Create("  把笔记读完  ", new[] { " 笔记已读取 ", "", "   ", "给出结论" });
        Assert.NotNull(anchor);
        Assert.Equal("把笔记读完", anchor!.Goal);
        Assert.Equal(new[] { "笔记已读取", "给出结论" }, anchor.RemainingCriteria);
    }

    [Fact]
    public void Render_ContainsTurnGoalCriteriaCheckpoint_AndClosingInstruction()
    {
        var anchor = GoalAnchor.Create("把笔记读完", new[] { "笔记已读取", "给出结论" })!;

        var text = anchor.Render(2, "seq 3 · edit_file");
        Assert.Contains("[目标锚定 · 第 3 轮]", text); // turn 0 起：2 → 第 3 轮
        Assert.Contains("原始目标：把笔记读完", text);
        Assert.Contains("最近 checkpoint：seq 3 · edit_file", text);
        Assert.Contains("剩余判据：", text);
        Assert.Contains("- 笔记已读取", text);
        Assert.Contains("- 给出结论", text);
        Assert.Contains("偏离", text); // 回轨指令

        // 无 checkpoint 摘要时不渲染该行（不伪造）。
        var bare = anchor.Render(0, null);
        Assert.DoesNotContain("最近 checkpoint", bare);
        Assert.Contains("[目标锚定 · 第 1 轮]", bare);
        Assert.Contains("原始目标：把笔记读完", bare);
    }
}

/// <summary>A2 默认偏离检测器（C-LOOP）纯单元：关键词正反例与词表替换语义。</summary>
public sealed class DefaultKeywordDetectorTests
{
    private static readonly GoalAnchor Anchor = GoalAnchor.Create("把笔记读完并总结")!;

    [Fact]
    public void OffGoal_KeywordsMatch_CaseInsensitive()
    {
        var detector = new DefaultKeywordDetector();

        var chinese = detector.Evaluate(new DeviationInput(
            0, Anchor, "很抱歉，我无法完成这个任务", Array.Empty<string>()));
        Assert.Equal(DeviationVerdict.OffGoal, chinese.Verdict);
        Assert.Contains("无法完成", chinese.Reason);

        var english = detector.Evaluate(new DeviationInput(
            1, Anchor, "CANNOT PROCEED with this approach", Array.Empty<string>()));
        Assert.Equal(DeviationVerdict.OffGoal, english.Verdict);
    }

    [Fact]
    public void OnGoal_NormalProgressText()
    {
        var detector = new DefaultKeywordDetector();
        var report = detector.Evaluate(new DeviationInput(
            0, Anchor, "已读取笔记，正在汇总要点", new[] { "NOTE_BODY" }));
        Assert.Equal(DeviationVerdict.OnGoal, report.Verdict);
    }

    [Fact]
    public void NullAssistantContent_PureToolCallTurn_OnGoal()
    {
        var detector = new DefaultKeywordDetector();
        var report = detector.Evaluate(new DeviationInput(
            0, Anchor, AssistantContent: null, ToolOutputs: new[] { "tool output" }));
        Assert.Equal(DeviationVerdict.OnGoal, report.Verdict);
    }

    [Fact]
    public void CustomKeywords_ReplacesDefaultTable_AndBlankEntriesFiltered()
    {
        // 空白项被过滤：若未过滤，空串包含匹配会让所有文本都 OffGoal。
        var detector = new DefaultKeywordDetector(new[] { "走偏了", "  ", string.Empty });

        var off = detector.Evaluate(new DeviationInput(0, Anchor, "这条路线走偏了", Array.Empty<string>()));
        Assert.Equal(DeviationVerdict.OffGoal, off.Verdict);
        Assert.Contains("走偏了", off.Reason);

        // 词表是替换而非叠加：默认词表关键词不再生效。
        var on = detector.Evaluate(new DeviationInput(1, Anchor, "进展顺利，无法完成的风险已消除", Array.Empty<string>()));
        Assert.Equal(DeviationVerdict.OnGoal, on.Verdict);
    }
}

/// <summary>
/// A2 升级策略与审批凭据（C-LOOP 安全硬门）：默认暂停待人策略的事件外抛、
/// 审批一次性消费（同一审批二次消费被拒、并发下恰好一个受理者）。
/// </summary>
public sealed class EscalationPolicyTests
{
    [Fact]
    public void TryConsume_FirstWins_SubsequentRejected()
    {
        var request = new EscalationRequest("esc-1", turn: 1, strikes: 2, reason: "r", raisedUtc: DateTime.UtcNow);
        Assert.False(request.Consumed);

        Assert.True(request.TryConsume());   // 首次受理成功
        Assert.True(request.Consumed);
        Assert.False(request.TryConsume());  // 二次消费被拒（不可重放）
        Assert.False(request.TryConsume());
    }

    [Fact]
    public void TryConsume_Concurrent_ExactlyOneApproval()
    {
        var request = new EscalationRequest("esc-2", 1, 2, "r", DateTime.UtcNow);
        var results = new bool[16];
        Parallel.For(0, results.Length, i => results[i] = request.TryConsume());
        Assert.Equal(1, results.Count(r => r)); // 审批恰好被消费一次
    }

    [Fact]
    public void HumanPause_Handle_RaisesOneTimeRequest_AndPublishesBusEvent()
    {
        var bus = new EventBus();
        var published = new List<DeviationEscalationEvent>();
        bus.Subscribe<DeviationEscalationEvent>(published.Add);

        var policy = new HumanPauseEscalationPolicy(bus);
        var raised = new List<EscalationRequest>();
        policy.EscalationRaised += raised.Add;

        var decision = policy.Handle(new EscalationContext(
            Turn: 3, Strikes: 2, Anchor: GoalAnchor.Create("目标")!, Reason: "偏离原因",
            RestoredCheckpointSeq: 7, RestoredFiles: 2));

        Assert.Equal(EscalationDecision.PauseForHuman, decision);

        var request = Assert.Single(raised);
        Assert.StartsWith("esc-", request.Id);
        Assert.Equal(3, request.Turn);
        Assert.Equal(2, request.Strikes);
        Assert.Equal("偏离原因", request.Reason);
        Assert.Equal(DateTimeKind.Utc, request.RaisedUtc.Kind);

        var evt = Assert.Single(published); // 观测面广播（EventBus）
        Assert.Equal(request.Id, evt.EscalationId);
        Assert.Equal(request.Turn, evt.Turn);
        Assert.Equal(request.Strikes, evt.Strikes);
    }

    [Fact]
    public void HumanPause_SubscriberThrowing_DoesNotBlockEscalationChain()
    {
        var bus = new EventBus();
        var published = new List<DeviationEscalationEvent>();
        bus.Subscribe<DeviationEscalationEvent>(published.Add);

        var policy = new HumanPauseEscalationPolicy(bus);
        policy.EscalationRaised += _ => throw new InvalidOperationException("subscriber blew up");

        var decision = policy.Handle(new EscalationContext(
            0, 2, GoalAnchor.Create("g")!, "r", null, 0));

        // 升级链路不被订阅方异常阻断：决定照常返回、凭据照常生成并广播。
        Assert.Equal(EscalationDecision.PauseForHuman, decision);
        Assert.Single(published);
    }
}

/// <summary>A2 LoopGuard 配置默认值与关闭预设。</summary>
public sealed class LoopGuardOptionsTests
{
    [Fact]
    public void Defaults_AnchoringOn_TwoStrikes()
    {
        var options = new LoopGuardOptions();
        Assert.True(options.GoalAnchoringEnabled);
        Assert.Equal(2, options.MaxStrikes);
        Assert.Equal(200, options.CheckpointSummaryChars);
    }

    [Fact]
    public void Disabled_Preset_TurnsAnchoringOffOnly()
    {
        Assert.False(LoopGuardOptions.Disabled.GoalAnchoringEnabled);
        Assert.Equal(2, LoopGuardOptions.Disabled.MaxStrikes); // 偏离检测配置不受影响
    }
}

/// <summary>
/// 测试用偏离检测器：按轮出脚本判定（默认耗尽后诚实 OnGoal）。
/// </summary>
internal sealed class ScriptedDeviationDetector : IDeviationDetector
{
    private readonly Queue<DeviationReport> _reports;

    public ScriptedDeviationDetector(params DeviationReport[] reports) => _reports = new Queue<DeviationReport>(reports);

    public DeviationReport Evaluate(DeviationInput input)
        => _reports.Count > 0 ? _reports.Dequeue() : new DeviationReport(DeviationVerdict.OnGoal, "script exhausted");
}

/// <summary>
/// 测试用升级策略：记录升级上下文、外抛一次性凭据、返回可编程决定。
/// </summary>
internal sealed class RecordingEscalationPolicy : IEscalationPolicy
{
    public List<EscalationContext> Contexts { get; } = new();
    public List<EscalationRequest> Requests { get; } = new();
    public EscalationDecision Decision { get; set; } = EscalationDecision.PauseForHuman;

    public event Action<EscalationRequest>? EscalationRaised;

    public EscalationDecision Handle(EscalationContext context)
    {
        Contexts.Add(context);
        var request = new EscalationRequest(
            $"esc-test-{Contexts.Count}", context.Turn, context.Strikes, context.Reason, DateTime.UtcNow);
        Requests.Add(request);
        EscalationRaised?.Invoke(request);
        return Decision;
    }
}

/// <summary>
/// A2 LoopGuard 与 WorkerRunner 工具循环的接线行为（C-LOOP，真实 DB + 可编程 provider + 真实文件系统 checkpoint）：
/// 目标锚定每轮首注入一次（同位置替换、不重复堆积）、与最近 checkpoint 摘要联动、
/// two-strike 停止条件（Restore 真实发生 + escalation 事件外抛 + 审批一次性消费）。
/// </summary>
public sealed class LoopGuardWorkerLoopTests : MoaTestBase
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

    private async Task<(OrchestrationContext Ctx, ModelAssignment Assignment)> SetupAsync(
        string providerId, ConversationTests.ScriptedProvider provider)
    {
        var profile = SetProfile(providerId, new[] { ModelStrength.General });
        var session = await NewSessionAsync(OrchestrationStrategy.Single);
        var ctx = new OrchestrationContext
        {
            Session = session,
            History = Array.Empty<ChatMessage>(),
            UserMessageId = "msg-user",
            Providers = Registry,
        };
        return (ctx, new ModelAssignment(providerId, string.Empty, profile));
    }

    /// <summary>
    /// F-H1 测试用：编排上下文持有真实任务目标（History 末条 user 消息 = 生产门面构造上下文的契约）。
    /// goalText 为 null 时不放 user 消息（未持有目标）。
    /// </summary>
    private async Task<(OrchestrationContext Ctx, ModelAssignment Assignment)> SetupWithHistoryAsync(
        string providerId, ConversationTests.ScriptedProvider provider, string? goalText)
    {
        var profile = SetProfile(providerId, new[] { ModelStrength.General });
        var session = await NewSessionAsync(OrchestrationStrategy.Single);
        var history = goalText is null
            ? Array.Empty<ChatMessage>()
            : new ChatMessage[] { new() { Role = ChatRole.User, Content = goalText } };
        var ctx = new OrchestrationContext
        {
            Session = session,
            History = history,
            UserMessageId = "msg-user",
            Providers = Registry,
        };
        return (ctx, new ModelAssignment(providerId, string.Empty, profile));
    }

    private static (ToolRouter Router, ScriptedToolbox Box) NewRouter()
    {
        var box = new ScriptedToolbox("notes", new ToolDefinition { Name = "get_note", Description = "读取笔记" });
        box.SetResult("get_note", ToolInvokeResult.Ok("NOTE_BODY"));
        var registry = new ToolboxRegistry();
        registry.Register(box);
        // 工具在策略层显式放行：多轮工具调用不依赖 broker 决策队列（ScriptedBroker 队列耗尽 = 诚实 Deny）。
        var permission = PermissionPolicy.CreateDefault(new EventBus());
        permission.SetDefaultDecision("get_note", PermissionDecision.Allow);
        var router = new ToolRouter(registry, permission, new ScriptedBroker(PermissionDecision.Allow));
        return (router, box);
    }

    /// <summary>在临时目录准备真实文件 + CheckpointStore，并 Track 一次检查点；返回（文件路径, store, 根目录）。</summary>
    private static (string File, CheckpointStore Store, string Root) NewCheckpointedFile(string captured, string modified)
    {
        var root = Path.Combine(Path.GetTempPath(), $"loopguard_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "a.txt");
        File.WriteAllText(file, captured);

        var store = new CheckpointStore(Path.Combine(root, "checkpoints"));
        store.Track("edit_file", new[] { file });
        File.WriteAllText(file, modified); // checkpoint 之后文件被改写——恢复应回到 captured
        return (file, store, root);
    }

    private WorkerRunner NewLoopRunner(
        ToolRouter router,
        IDeviationDetector? detector,
        IEscalationPolicy? policy,
        CheckpointStore? checkpoints = null,
        LoopGuardOptions? loopGuard = null)
        => new(Sessions, Catalog,
            tools: router,
            loopGuard: loopGuard ?? new LoopGuardOptions(),
            deviationDetector: detector,
            escalationPolicy: policy,
            checkpoints: checkpoints);

    // ---- 目标锚定：每轮首注入、同位置替换、checkpoint 摘要联动 ----

    [Fact]
    public async Task GoalAnchor_InjectedEachTurn_SamePositionReplacement_NoAccumulation()
    {
        var provider = AddProvider("lg-anchor");
        provider.ResponseQueue.Enqueue(ToolCallResponse("c1", "get_note", "{}", null));
        provider.ResponseQueue.Enqueue(FinalResponse("锚定完成", null));
        var (router, _) = NewRouter();
        var runner = NewLoopRunner(router, detector: null, policy: null);

        var (ctx, assignment) = await SetupAsync("lg-anchor", provider);
        var anchor = GoalAnchor.Create("把笔记读完", new[] { "笔记已读取" });

        var outcome = await runner.RunAsync(
            ctx, assignment, StrategyRole.Worker, null, null,
            Prompt, stream: false, isFinal: true, null, null, CancellationToken.None, anchor);

        Assert.True(outcome.Succeeded);

        // 第二轮请求（最后一轮）：user prompt + 锚定段 + assistant tool_calls + tool。
        var reFed = provider.LastRequestMessages!;
        Assert.Equal(4, reFed.Count);
        Assert.Equal("user", reFed[0].Role);
        Assert.Equal("帮我读笔记", reFed[0].Content);

        var anchors = reFed.Where(m => m.Content.Contains("[目标锚定", StringComparison.Ordinal)).ToList();
        Assert.Single(anchors); // 连续两轮只有一条锚定消息：同位置替换，不逐轮堆积
        Assert.Contains("第 2 轮", anchors[0].Content);  // 每轮更新到当前轮次
        Assert.DoesNotContain("第 1 轮", anchors[0].Content); // 旧轮次文本不残留
        Assert.Contains("原始目标：把笔记读完", anchors[0].Content);
        Assert.Contains("- 笔记已读取", anchors[0].Content);

        // 同位置语义：锚定段保持在首轮注入处（index 1），未漂移到队尾重复追加。
        Assert.Equal(1, reFed.IndexOf(anchors[0]));
    }

    [Fact]
    public async Task GoalAnchor_LinkedToLatestCheckpointSummary()
    {
        var (file, store, root) = NewCheckpointedFile("original", "modified");
        try
        {
            var provider = AddProvider("lg-ckpt");
            provider.ResponseQueue.Enqueue(ToolCallResponse("c1", "get_note", "{}", null));
            provider.ResponseQueue.Enqueue(FinalResponse("done", null));
            var (router, _) = NewRouter();
            var runner = NewLoopRunner(router, detector: null, policy: null, checkpoints: store);

            var (ctx, assignment) = await SetupAsync("lg-ckpt", provider);
            var anchor = GoalAnchor.Create("把笔记读完");

            var outcome = await runner.RunAsync(
                ctx, assignment, StrategyRole.Worker, null, null,
                Prompt, stream: false, isFinal: true, null, null, CancellationToken.None, anchor);

            Assert.True(outcome.Succeeded);
            var anchorMessage = Assert.Single(
                provider.LastRequestMessages!,
                m => m.Content.Contains("[目标锚定", StringComparison.Ordinal));
            Assert.Contains("最近 checkpoint：", anchorMessage.Content);
            Assert.Contains("seq 1", anchorMessage.Content);
            Assert.Contains("edit_file", anchorMessage.Content);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task NoGoalAnchor_LoopBehaviorUnchanged()
    {
        var provider = AddProvider("lg-nogoal");
        provider.ResponseQueue.Enqueue(ToolCallResponse("c1", "get_note", "{}", null));
        provider.ResponseQueue.Enqueue(FinalResponse("普通完成", null));
        var (router, box) = NewRouter();
        var runner = NewLoopRunner(router, detector: null, policy: null);

        var (ctx, assignment) = await SetupAsync("lg-nogoal", provider);

        var outcome = await runner.RunAsync(
            ctx, assignment, StrategyRole.Worker, null, null,
            Prompt, stream: false, isFinal: true, null, null, CancellationToken.None, goalAnchor: null);

        Assert.True(outcome.Succeeded);
        Assert.Equal("普通完成", outcome.Content);
        Assert.DoesNotContain(
            provider.LastRequestMessages!,
            m => m.Content.Contains("[目标锚定", StringComparison.Ordinal)); // 未持有目标：不注入
        Assert.Single(box.Invocations);
    }

    [Fact]
    public async Task AnchoringDisabled_NoAnchorInjected_DeviationDetectionStillActive()
    {
        var provider = AddProvider("lg-disabled");
        provider.ResponseQueue.Enqueue(ToolCallResponse("c1", "get_note", "{}", null));
        provider.ResponseQueue.Enqueue(ToolCallResponse("c2", "get_note", "{}", null));
        var (router, _) = NewRouter();
        var policy = new RecordingEscalationPolicy();
        var detector = new ScriptedDeviationDetector(
            new DeviationReport(DeviationVerdict.OffGoal, "wandering"),
            new DeviationReport(DeviationVerdict.OffGoal, "wandering again"));
        var runner = NewLoopRunner(router, detector, policy, loopGuard: LoopGuardOptions.Disabled);

        var (ctx, assignment) = await SetupAsync("lg-disabled", provider);
        var anchor = GoalAnchor.Create("把笔记读完");

        var outcome = await runner.RunAsync(
            ctx, assignment, StrategyRole.Worker, null, null,
            Prompt, stream: false, isFinal: true, null, null, CancellationToken.None, anchor);

        // 锚定关闭：不注入锚定段；偏离检测不受影响（检测由注入的 detector 决定）。
        Assert.False(outcome.Succeeded);
        Assert.Contains("paused for human review", outcome.Error);
        Assert.DoesNotContain(
            provider.LastRequestMessages!,
            m => m.Content.Contains("[目标锚定", StringComparison.Ordinal));
        Assert.Single(policy.Contexts);
    }

    // ---- two-strike 停止条件 ----

    [Fact]
    public async Task TwoConsecutiveOffGoalStrikes_RestoreLatestCheckpoint_RaiseEscalation_OneTimeApproval()
    {
        var (file, store, root) = NewCheckpointedFile("original", "modified");
        try
        {
            var provider = AddProvider("lg-strike");
            provider.ResponseQueue.Enqueue(ToolCallResponse("c1", "get_note", "{}", null));
            provider.ResponseQueue.Enqueue(ToolCallResponse("c2", "get_note", "{}", null));
            var (router, box) = NewRouter();
            var policy = new RecordingEscalationPolicy { Decision = EscalationDecision.PauseForHuman };
            var detector = new ScriptedDeviationDetector(
                new DeviationReport(DeviationVerdict.OffGoal, "wandering"),
                new DeviationReport(DeviationVerdict.OffGoal, "wandering again"));
            var runner = NewLoopRunner(router, detector, policy, checkpoints: store);

            var (ctx, assignment) = await SetupAsync("lg-strike", provider);
            var anchor = GoalAnchor.Create("把笔记读完并总结", new[] { "笔记已读取", "给出结论" });

            var outcome = await runner.RunAsync(
                ctx, assignment, StrategyRole.Worker, null, null,
                Prompt, stream: false, isFinal: true, null, null, CancellationToken.None, anchor);

            // 诚实暂停：两 strike → 恢复最近 checkpoint + 升级（默认暂停待人）。
            Assert.False(outcome.Succeeded);
            Assert.Contains("paused for human review (deviation escalation)", outcome.Error);

            // escalation 事件外抛 + 上下文如实（含真实恢复结果，不伪造）。
            var context = Assert.Single(policy.Contexts);
            Assert.Equal(2, context.Strikes);
            Assert.Equal(1, context.Turn); // 第二个工具轮（0 起）
            Assert.Equal(1L, context.RestoredCheckpointSeq);
            Assert.Equal(1, context.RestoredFiles);
            Assert.Contains("restored 1 file(s)", context.Reason);
            Assert.Equal("把笔记读完并总结", context.Anchor.Goal);

            // Restore 真实发生：文件内容回到 checkpoint 捕获时状态。
            Assert.Equal("original", await File.ReadAllTextAsync(file));

            // 审批一次性消费：首次受理成功，二次消费被拒（不可重放）。
            var request = Assert.Single(policy.Requests);
            Assert.True(request.TryConsume());
            Assert.False(request.TryConsume());

            // 终态落库：占位最终答复 Failed（诚实暂停态）。
            var messages = (await Sessions.GetMessagesAsync(ctx.Session.Id)).Value!;
            Assert.Equal(MessageStatus.Failed, Assert.Single(messages, m => m.IsFinal == true).Status);

            // 两次 OffGoal 恰好两次工具轮：第三次 provider 调用未发生。
            Assert.Equal(2, box.Invocations.Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SingleOffGoalStrike_ThenOnGoal_DoesNotEscalate_LoopCompletes()
    {
        var (file, store, root) = NewCheckpointedFile("original", "modified");
        try
        {
            var provider = AddProvider("lg-single");
            provider.ResponseQueue.Enqueue(ToolCallResponse("c1", "get_note", "{}", null));
            provider.ResponseQueue.Enqueue(ToolCallResponse("c2", "get_note", "{}", null));
            provider.ResponseQueue.Enqueue(FinalResponse("已回到目标：读取完成", null));
            var (router, box) = NewRouter();
            var policy = new RecordingEscalationPolicy();
            var detector = new ScriptedDeviationDetector(
                new DeviationReport(DeviationVerdict.OffGoal, "one-off"),
                new DeviationReport(DeviationVerdict.OnGoal, "back on track"));
            var runner = NewLoopRunner(router, detector, policy, checkpoints: store);

            var (ctx, assignment) = await SetupAsync("lg-single", provider);
            var anchor = GoalAnchor.Create("把笔记读完");

            var outcome = await runner.RunAsync(
                ctx, assignment, StrategyRole.Worker, null, null,
                Prompt, stream: false, isFinal: true, null, null, CancellationToken.None, anchor);

            // 一次 OffGoal 不触发升级；随后 OnGoal 清零 strike，循环正常完成。
            Assert.True(outcome.Succeeded);
            Assert.Equal("已回到目标：读取完成", outcome.Content);
            Assert.Empty(policy.Contexts);
            Assert.Empty(policy.Requests);

            // 未发生 checkpoint 恢复：文件保持被改写后的内容。
            Assert.Equal("modified", await File.ReadAllTextAsync(file));
            Assert.Equal(2, box.Invocations.Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task TwoStrikes_ContinueWithWarning_ResetsStrikesAndContinuesLoop()
    {
        var (file, store, root) = NewCheckpointedFile("original", "modified");
        try
        {
            var provider = AddProvider("lg-continue");
            provider.ResponseQueue.Enqueue(ToolCallResponse("c1", "get_note", "{}", null));
            provider.ResponseQueue.Enqueue(ToolCallResponse("c2", "get_note", "{}", null));
            provider.ResponseQueue.Enqueue(FinalResponse("策略接管后完成", null));
            var (router, box) = NewRouter();
            var policy = new RecordingEscalationPolicy { Decision = EscalationDecision.ContinueWithWarning };
            var detector = new ScriptedDeviationDetector(
                new DeviationReport(DeviationVerdict.OffGoal, "wandering"),
                new DeviationReport(DeviationVerdict.OffGoal, "wandering again"));
            var runner = NewLoopRunner(router, detector, policy, checkpoints: store);

            var (ctx, assignment) = await SetupAsync("lg-continue", provider);

            var outcome = await runner.RunAsync(
                ctx, assignment, StrategyRole.Worker, null, null,
                Prompt, stream: false, isFinal: true, null, null, CancellationToken.None,
                GoalAnchor.Create("把笔记读完"));

            // 策略接管责任：恢复先行发生，strike 清零后循环继续到最终答复。
            Assert.True(outcome.Succeeded);
            Assert.Equal("策略接管后完成", outcome.Content);
            var context = Assert.Single(policy.Contexts);
            Assert.Equal(2, context.Strikes);
            Assert.Equal("original", await File.ReadAllTextAsync(file)); // 恢复在决策前完成
            Assert.Equal(2, box.Invocations.Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task TwoStrikes_AbortDecision_FailsRunHonest()
    {
        var provider = AddProvider("lg-abort");
        provider.ResponseQueue.Enqueue(ToolCallResponse("c1", "get_note", "{}", null));
        provider.ResponseQueue.Enqueue(ToolCallResponse("c2", "get_note", "{}", null));
        var (router, _) = NewRouter();
        var policy = new RecordingEscalationPolicy { Decision = EscalationDecision.Abort };
        var detector = new ScriptedDeviationDetector(
            new DeviationReport(DeviationVerdict.OffGoal, "wandering"),
            new DeviationReport(DeviationVerdict.OffGoal, "wandering again"));
        var runner = NewLoopRunner(router, detector, policy);

        var (ctx, assignment) = await SetupAsync("lg-abort", provider);

        var outcome = await runner.RunAsync(
            ctx, assignment, StrategyRole.Worker, null, null,
            Prompt, stream: false, isFinal: true, null, null, CancellationToken.None,
            GoalAnchor.Create("把笔记读完"));

        Assert.False(outcome.Succeeded);
        Assert.Contains("aborted by deviation escalation", outcome.Error);
        var messages = (await Sessions.GetMessagesAsync(ctx.Session.Id)).Value!;
        Assert.Equal(MessageStatus.Failed, Assert.Single(messages, m => m.IsFinal == true).Status);
    }

    [Fact]
    public async Task MaxStrikesOne_SingleOffGoal_TriggersEscalationImmediately()
    {
        var provider = AddProvider("lg-max1");
        provider.ResponseQueue.Enqueue(ToolCallResponse("c1", "get_note", "{}", null));
        var (router, box) = NewRouter();
        var policy = new RecordingEscalationPolicy();
        var detector = new ScriptedDeviationDetector(new DeviationReport(DeviationVerdict.OffGoal, "wandering"));
        var runner = NewLoopRunner(
            router, detector, policy, loopGuard: new LoopGuardOptions { MaxStrikes = 1 });

        var (ctx, assignment) = await SetupAsync("lg-max1", provider);

        var outcome = await runner.RunAsync(
            ctx, assignment, StrategyRole.Worker, null, null,
            Prompt, stream: false, isFinal: true, null, null, CancellationToken.None,
            GoalAnchor.Create("把笔记读完"));

        Assert.False(outcome.Succeeded);
        Assert.Contains("paused for human review", outcome.Error);
        var context = Assert.Single(policy.Contexts);
        Assert.Equal(1, context.Strikes);
        Assert.Equal(0, context.Turn);
        Assert.Single(box.Invocations);
    }

    // ---- F-H1（R1 审查修复）：LoopGuard 生产链路可达——公共入口从编排上下文派生任务锚 ----

    [Fact]
    public async Task LoopGuardEnabled_DerivesGoalAnchorFromContextHistory_InjectsAnchor()
    {
        var provider = AddProvider("lg-derive");
        provider.ResponseQueue.Enqueue(ToolCallResponse("c1", "get_note", "{}", null));
        provider.ResponseQueue.Enqueue(FinalResponse("锚定完成", null));
        var (router, _) = NewRouter();
        var runner = NewLoopRunner(router, detector: null, policy: null);

        // 上下文持有任务目标（生产门面契约：History 末条 = 本轮用户消息），调用点不再显式传锚。
        var (ctx, assignment) = await SetupWithHistoryAsync("lg-derive", provider, "把笔记读完并总结");

        var outcome = await runner.RunAsync(
            ctx, assignment, StrategyRole.Worker, null, null,
            Prompt, stream: false, isFinal: true, null, null, CancellationToken.None, goalAnchor: null);

        Assert.True(outcome.Succeeded);
        var anchorMessage = Assert.Single(
            provider.LastRequestMessages!,
            m => m.Content.Contains("[目标锚定", StringComparison.Ordinal));
        Assert.Contains("原始目标：把笔记读完并总结", anchorMessage.Content);
    }

    [Fact]
    public async Task LoopGuardEnabled_BlankGoal_DerivationKeepsNullSemantics()
    {
        var provider = AddProvider("lg-blank");
        provider.ResponseQueue.Enqueue(ToolCallResponse("c1", "get_note", "{}", null));
        provider.ResponseQueue.Enqueue(FinalResponse("普通完成", null));
        var (router, box) = NewRouter();
        var runner = NewLoopRunner(router, detector: null, policy: null);

        // 空白目标 → GoalAnchor.Create 返回 null（未持有语义经公共入口保持）。
        var (ctx, assignment) = await SetupWithHistoryAsync("lg-blank", provider, "   ");

        var outcome = await runner.RunAsync(
            ctx, assignment, StrategyRole.Worker, null, null,
            Prompt, stream: false, isFinal: true, null, null, CancellationToken.None, goalAnchor: null);

        Assert.True(outcome.Succeeded);
        Assert.Equal("普通完成", outcome.Content);
        Assert.DoesNotContain(
            provider.LastRequestMessages!,
            m => m.Content.Contains("[目标锚定", StringComparison.Ordinal));
        Assert.Single(box.Invocations);
    }

    [Fact]
    public async Task LoopGuardDisabled_NoAnchorDerived_BehaviorUnchanged()
    {
        var provider = AddProvider("lg-noguard");
        provider.ResponseQueue.Enqueue(ToolCallResponse("c1", "get_note", "{}", null));
        provider.ResponseQueue.Enqueue(FinalResponse("普通完成", null));
        var (router, box) = NewRouter();

        // 硬门：组合根未启用 LoopGuard（不注入 LoopGuardOptions）→ 即使上下文持有目标，
        // 也不派生锚、行为与基线完全一致。
        var runner = new WorkerRunner(Sessions, Catalog, tools: router);

        var (ctx, assignment) = await SetupWithHistoryAsync("lg-noguard", provider, "把笔记读完并总结");

        var outcome = await runner.RunAsync(
            ctx, assignment, StrategyRole.Worker, null, null,
            Prompt, stream: false, isFinal: true, null, null, CancellationToken.None, goalAnchor: null);

        Assert.True(outcome.Succeeded);
        Assert.Equal("普通完成", outcome.Content);
        Assert.DoesNotContain(
            provider.LastRequestMessages!,
            m => m.Content.Contains("[目标锚定", StringComparison.Ordinal));
        Assert.Single(box.Invocations);
    }

    // ---- F-M6（R1 审查修复）：压缩/策展移除锚定段后的自动重注入（只加测试不改实现）----

    [Fact]
    public async Task CompactionRemovesAnchorSegment_NextTurnReinjectsExactlyOne()
    {
        var provider = AddProvider("lg-reinject");
        provider.ResponseQueue.Enqueue(ToolCallResponse("c1", "get_note", "{}", null));
        provider.ResponseQueue.Enqueue(FinalResponse("压缩后重注入完成", null));
        var (router, _) = NewRouter();
        var runner = new WorkerRunner(
            Sessions, Catalog,
            tools: router,
            loopGuard: new LoopGuardOptions(),
            compactor: new Compactor(new EventBus(), CompactionStrategy.SlidingWindow, keepRecentMessages: 2),
            compaction: new CompactionGateOptions { ThresholdTokens = 1000 });

        var (ctx, assignment) = await SetupWithHistoryAsync("lg-reinject", provider, "把笔记读完");

        // 大 prompt：第 2 轮首的溢出压缩把锚定段挤出滑动窗口。
        var prompt = new List<AiChatMessage>
        {
            new() { Role = "user", Content = new string('x', 20_000) },
        };
        var outcome = await runner.RunAsync(
            ctx, assignment, StrategyRole.Worker, null, null,
            prompt, stream: false, isFinal: true, null, null, CancellationToken.None,
            GoalAnchor.Create("把笔记读完"));

        Assert.True(outcome.Succeeded);

        // 第二轮请求 = 压缩后的对话区 + 自动重注入的锚定段（恰一条，轮次已推进）。
        var reFed = provider.LastRequestMessages!;
        Assert.Equal(3, reFed.Count);
        Assert.Equal("assistant", reFed[0].Role);
        Assert.Equal("tool", reFed[1].Role);
        var anchors = reFed.Where(m => m.Content.Contains("[目标锚定", StringComparison.Ordinal)).ToList();
        Assert.Single(anchors);
        Assert.Contains("第 2 轮", anchors[0].Content);
        Assert.DoesNotContain("第 1 轮", anchors[0].Content);
        Assert.Equal(2, reFed.IndexOf(anchors[0]));
    }

    [Fact]
    public async Task CurationRemovesAnchorSegment_NextTurnReinjectsExactlyOne()
    {
        var provider = AddProvider("lg-curate-reinject");
        provider.ResponseQueue.Enqueue(ToolCallResponse("c1", "get_note", "{}", null));
        provider.ResponseQueue.Enqueue(FinalResponse("策展后重注入完成", null));
        var (router, _) = NewRouter();
        var runner = new WorkerRunner(Sessions, Catalog, tools: router, loopGuard: new LoopGuardOptions());
        runner.Curator = new ContextCurator(new CurationOptions
        {
            WatermarkThresholdTokens = 1000,
            KeepRecentMessages = 2,
            Strategy = CompactionStrategy.TruncateOldest,
        });

        var (ctx, assignment) = await SetupWithHistoryAsync("lg-curate-reinject", provider, "把笔记读完");

        var prompt = new List<AiChatMessage>
        {
            new() { Role = "user", Content = new string('x', 20_000) },
        };
        var outcome = await runner.RunAsync(
            ctx, assignment, StrategyRole.Worker, null, null,
            prompt, stream: false, isFinal: true, null, null, CancellationToken.None,
            GoalAnchor.Create("把笔记读完"));

        Assert.True(outcome.Succeeded);

        // 第二轮请求 = 状态块（新冻结前缀）+ 压缩后对话区 + 自动重注入的锚定段（恰一条）。
        var reFed = provider.LastRequestMessages!;
        Assert.Equal(4, reFed.Count);
        Assert.Equal("system", reFed[0].Role);
        Assert.Equal("assistant", reFed[1].Role);
        Assert.Equal("tool", reFed[2].Role);
        // 锚定段是 user 消息（ApplyGoalAnchor 语义）；状态块是 system 消息，
        // 其 [最近动作] digest 会引用被移除锚定的原文，须按 Role 排除避免误计。
        var anchors = reFed
            .Where(m => string.Equals(m.Role, "user", StringComparison.Ordinal)
                        && m.Content.Contains("[目标锚定", StringComparison.Ordinal))
            .ToList();
        Assert.Single(anchors);
        Assert.Contains("第 2 轮", anchors[0].Content);
        Assert.Equal(3, reFed.IndexOf(anchors[0]));
    }
}
