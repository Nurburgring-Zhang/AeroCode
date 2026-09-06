using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using AeroAgent.Conversation.Models;
using AeroAgent.Conversation.Orchestration;
using AeroAgent.Conversation.Services;
using AeroAgent.Moa.Accounting;
using AeroAgent.Moa.Assignment;
using AeroAgent.Moa.Budget;
using AeroAgent.Moa.Curation;
using AeroAgent.Moa.Guard;
using AeroAgent.Moa.LoopGuard;
using AeroAgent.Moa.Profiles;
using AeroAgent.Moa.Tools;
using AeroAgent.Moa.Tools.Workspace;
using AeroAgent.Moa.Verify;
using AeroCode.AI.Providers;
using AeroCode.Harness.Compaction;
using AeroCode.Harness.Curation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using AiChatMessage = AeroCode.AI.Models.ChatMessage;
using AiChatRequest = AeroCode.AI.Models.ChatRequest;

namespace AeroAgent.Moa.Strategies;

/// <summary>一次 worker 调用的结果。RunAsync 不抛 provider 异常——成败都在结果里。</summary>
public sealed record WorkerOutcome(
    string MessageId,
    string ProviderId,
    string ModelId,
    string Content,
    bool Succeeded,
    bool Cancelled,
    string? Error,
    int TokensIn,
    int TokensOut,
    double CostUsd,
    int LatencyMs);

/// <summary>
/// 工具循环溢出检测配置（组合根从 Settings.Compaction 映射，DI 单例注入）。
/// ThresholdTokens ≤ 0 = 关闭溢出检测（不压缩，行为与批次 A 完全一致）。
/// </summary>
public sealed record CompactionGateOptions
{
    public int ThresholdTokens { get; init; }

    public static CompactionGateOptions Disabled { get; } = new() { ThresholdTokens = 0 };
}

/// <summary>MOA 各策略共用的单模型调用引擎：持久化占位消息 → 真实调用（流式/非流式）→
/// 事件发射 → 真实用量与成本落库 → 自学习统计回填。
/// 异常边界：provider 异常/取消都在此收容为 Failed/Cancelled 终态（DB 是事实源），
/// 不向策略抛出——策略按 <see cref="WorkerOutcome"/> 决定降级或中止。
/// 批次 B G2-4：工具循环带溢出检测——估算 token 超过阈值时调 Harness <see cref="Compactor"/>
/// 压缩在途上下文（Compactor 自身发布 CompactionTriggeredEvent），工具配对完整性由本类保证。
/// </summary>
public sealed class WorkerRunner
{
    /// <summary>工具循环最大轮数：防止模型无限套娃调用工具（超限诚实中止并落失败态）。</summary>
    public const int MaxToolTurns = 8;

    private readonly ISessionService _sessions;
    private readonly IModelProfileCatalog _catalog;
    private readonly ILogger<WorkerRunner> _logger;
    private readonly ToolRouter? _tools;
    private readonly MoaOptions? _options;
    private readonly Compactor? _compactor;
    private readonly CompactionGateOptions? _compaction;
    private readonly LoopGuardOptions? _loopGuard;
    private readonly IDeviationDetector? _deviationDetector;
    private readonly IEscalationPolicy? _escalationPolicy;
    private readonly CheckpointStore? _checkpoints;

    public WorkerRunner(
        ISessionService sessions,
        IModelProfileCatalog catalog,
        ILogger<WorkerRunner>? logger = null,
        ToolRouter? tools = null,
        MoaOptions? options = null,
        Compactor? compactor = null,
        CompactionGateOptions? compaction = null,
        LoopGuardOptions? loopGuard = null,
        IDeviationDetector? deviationDetector = null,
        IEscalationPolicy? escalationPolicy = null,
        CheckpointStore? checkpoints = null)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _logger = logger ?? NullLogger<WorkerRunner>.Instance;
        _tools = tools;
        _options = options;
        _compactor = compactor;
        _compaction = compaction;
        _loopGuard = loopGuard;
        _deviationDetector = deviationDetector;
        _escalationPolicy = escalationPolicy;
        _checkpoints = checkpoints;
    }

    /// <summary>
    /// C-CURATE 注入点（β 定契约，α 唯一接线点）：非 null 时每轮末以当前对话区 +
    /// 冻结前缀边界 + 水位阈值调用 <see cref="IContextCurator.Curate"/>；
    /// 触发压缩的结果经工具配对修复后替换在途上下文。null = 不策展（行为不变）。
    /// </summary>
    public IContextCurator? Curator { get; set; }

    /// <summary>
    /// 策展水位阈值（token 估算，4 字符 ≈ 1 token 既有口径），随
    /// <see cref="CurationInput.WatermarkThresholdTokens"/> 透传给策展器；
    /// null = 由策展器采用其默认水位。策展器自身对水位做判定（未超阈值原样返回）。
    /// </summary>
    public int? CuratorThresholdTokens { get; set; }

    /// <summary>
    /// F-M1（R2 波次 γ）：mission 级 token 预算闸门（可选注入，null = 现行为）。
    /// 工具循环每轮真实 usage（prompt+completion，含 critique 重试轮）实报汇入闸门——
    /// 修复 R1 遗留「ReportUsage 唯一调用点在子代理侧，主循环 token 不入 mission 级闸门」。
    /// Exhausted 转换沿行为由闸门订阅方定义（本类不中断循环，逐轮成本照常诚实核算）。
    /// </summary>
    public ITokenBudgetGate? BudgetGate { get; set; }

    /// <summary>
    /// C2（R2 波次 γ）：完成判定验证器（可选注入，null = 现行为）。非 null 时工具循环的最终答复
    /// 须经独立 critique（判定不信自报，佐证=工具输出），拒绝时有界重试（critique ≤2 轮），
    /// 轮耗尽按策略收敛（默认 AcceptWithFindings，Fail=诚实失败）。
    /// 重试产出的真实用量/成本照常逐轮核算进 TurnBudget 与 <see cref="BudgetGate"/>，不绕过预算。
    /// </summary>
    public ICompletionVerifier? CompletionVerifier { get; set; }

    /// <summary>critique 循环配置（可选；null = 默认 ≤2 轮 + AcceptWithFindings）。</summary>
    public CritiqueLoopOptions? CritiqueOptions { get; set; }

    /// <summary>
    /// ⑤ effort「决策→请求」闭合（R2 缝合，可选注入，null = 现行为）：非 null 且
    /// <see cref="PeakEffortRequested"/> 时，每次 worker 调用前经
    /// <see cref="EffortProfile.DecideAsync"/> 做峰值档裁决（探测 fail-closed），Peak 命中
    /// 才把厂商 token 写入 <c>ChatRequest.ThinkingEffort</c>（O=xhigh / A=extended-thinking /
    /// G=deep-think）。未注入 / 未请求 / Standard 档：不改动 effort 形态，请求与基线逐字节一致。
    /// 组合根独占装配（settings.effort.Enabled 且探测可用时注入）。
    /// </summary>
    public EffortProfile? PeakEffortProfile { get; set; }

    /// <summary>是否申请峰值推理档（组合根按 settings.effort 决定；默认 false = 现行为）。</summary>
    public bool PeakEffortRequested { get; set; }

    /// <summary>
    /// ⑥ 冻结前缀→CacheBreakpoints 边界值传递（R2 缝合，默认 false = 现行为）。true 时工具循环
    /// 把冻结前缀边界（起始连续 system 稳定段；断点 = 最后一条冻结消息的 0-based 索引）写入
    /// <c>ChatRequest.CacheBreakpoints</c>——C-CURATE 冻结前缀与厂商缓存断点的真实交汇点
    /// 在工具循环（直连路径无策展、无冻结边界维护，不接线）。
    /// </summary>
    public bool CacheBreakpointsEnabled { get; set; }

    /// <summary>
    /// B5 guardrail 输入/输出段生产接点（R2 修复 MED-4，可选注入，null = 现行为、零成本跳过）：
    /// 非 null 时在用户输入入口（<see cref="RunAsync"/> 进入处）与最终答复产出点调用
    /// <see cref="GuardrailPipeline.ValidateInput"/>/<see cref="GuardrailPipeline.ValidateOutput"/>，
    /// 发现仅记录 + WARN 日志（MarkOnly 语义：不阻断、不改变任何流程走向，与
    /// <see cref="GuardrailOptions.Mode"/>=MarkOnly 默认一致）。工具调用段经
    /// GuardrailPermissionAdvisor → Harness PermissionPolicy 挂点生效（既有接线）；
    /// 工具段验证器缺口（Advisor 只消费 ToolCall 段）为 R3 边界。组合根独占装配。
    /// </summary>
    public GuardrailPipeline? Guardrail { get; set; }

    /// <summary>执行一次模型调用。事件写入 <paramref name="sink"/>（可为 null = 静默执行）。</summary>
    /// <param name="ctx">编排上下文（会话/历史/provider 注册表）。</param>
    /// <param name="assignment">模型分配（provider + 模型 + 画像）。</param>
    /// <param name="role">本消息在编排中的角色（归属标注）。</param>
    /// <param name="parentMessageId">父消息 Id（归属树），可为 null。</param>
    /// <param name="label">可读标签（如子任务名），可为 null。</param>
    /// <param name="messages">发给 provider 的消息序列。</param>
    /// <param name="stream">true = 流式打字机（面向用户的最终答复）；false = 一次性（内部子任务，可拿到真实 usage）。</param>
    /// <param name="isFinal">true = 面向用户的最终答复（进入后续轮次上下文）；false = 编排中间产物（仅当轮编排与审计，不回灌历史）。</param>
    /// <param name="sink">事件写入通道，null = 静默执行。</param>
    /// <param name="budget">单轮预算；发起前检查，超限直接失败返回。</param>
    /// <param name="ct">取消令牌。</param>
    /// <param name="goalAnchor">任务目标锚（可选；持有目标时 LoopGuard 每轮首注入锚定段、每轮末做偏离检测）。</param>
    public async Task<WorkerOutcome> RunAsync(
        OrchestrationContext ctx,
        ModelAssignment assignment,
        StrategyRole role,
        string? parentMessageId,
        string? label,
        IReadOnlyList<AiChatMessage> messages,
        bool stream,
        bool isFinal,
        ChannelWriter<ChatEvent>? sink,
        TurnBudget? budget,
        CancellationToken ct,
        GoalAnchor? goalAnchor = null)
    {
        var sessionId = ctx.Session.Id;

        // ---- 预算检查（诚实中止，不静默继续）----
        if (budget is { HasBudget: false })
        {
            var budgetError = $"budget exceeded: spent ${budget.SpentUsd:F6}, limit ${budget.MaxUsd:F6}";
            await EmitAsync(sink, new MessageFailedEvent
            {
                SessionId = sessionId,
                MessageId = string.Empty,
                Error = budgetError,
            });
            return new WorkerOutcome(string.Empty, assignment.ProviderId, assignment.ModelId,
                string.Empty, Succeeded: false, Cancelled: false, budgetError, 0, 0, 0, 0);
        }

        var provider = ctx.Providers.Get(assignment.ProviderId);

        // ---- R2 修复 MED-4（B5 输入段生产接点）：Guardrail 注入时对送入模型的用户输入做输入段验证，
        // 发现仅记录 + WARN（MarkOnly 语义：不阻断、不改变流程走向）；null = 零成本跳过（现行为）。----
        if (Guardrail is not null)
        {
            var userInput = messages.LastOrDefault(m =>
                string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase))?.Content;
            var pipeline = Guardrail;
            LogGuardrailFindings("input", () => pipeline.ValidateInput(userInput, cancellationToken: ct));
        }

        // ---- 持久化占位消息 ----
        var message = new ChatMessage
        {
            SessionId = sessionId,
            Role = ChatRole.Assistant,
            ProviderId = assignment.ProviderId,
            ModelId = assignment.ModelId,
            OrchestrationRole = role,
            ParentMessageId = parentMessageId,
            Label = label,
            IsFinal = isFinal,
            Status = MessageStatus.Streaming,
        };
        var appended = await _sessions.AppendMessageAsync(message);
        if (!appended.IsSuccess)
        {
            var error = appended.Error ?? "persist failed";
            await EmitAsync(sink, new MessageFailedEvent
            {
                SessionId = sessionId,
                MessageId = message.Id,
                Error = error,
            });
            return new WorkerOutcome(message.Id, assignment.ProviderId, assignment.ModelId,
                string.Empty, Succeeded: false, Cancelled: false, error, 0, 0, 0, 0);
        }

        await EmitAsync(sink, new AssistantMessageStarted
        {
            SessionId = sessionId,
            MessageId = message.Id,
            ProviderId = assignment.ProviderId,
            ModelId = assignment.ModelId,
            OrchestrationRole = role,
            ParentMessageId = parentMessageId,
            Label = label,
        });

        // ---- F-H1 接线（R1 审查修复）：LoopGuard 生产链路可达化。组合根只在
        // LoopGuard.Enabled=true 时注入 LoopGuardOptions——以此作为派生任务锚的唯一开关：
        // 持有编排上下文（History 末条 user 消息 = 任务目标）即在此公共入口统一构造锚，
        // 全部策略调用点一处覆盖；目标空白 → GoalAnchor.Create 返回 null（未持有语义保持）。
        // 双重保险：_loopGuard 为 null（未启用）时锚保持 null，ApplyGoalAnchor 与偏离检测
        // 各自还要求「持有 anchor 才生效」——默认路径行为与基线完全一致。----
        if (goalAnchor is null && _loopGuard is not null)
        {
            goalAnchor = GoalAnchor.Create(
                ctx.History.LastOrDefault(m => m.Role == ChatRole.User)?.Content);
        }

        // ---- 工具循环：注册中心有工具且未禁用时走非流式多轮（需要完整 tool_calls 才能配对执行）。
        // 占位消息承载最终答复；中间轮次（助手 tool_calls + tool 结果）逐条真实落库。
        // stream 参数在此路径被忽略——工具轮没有打字机，这是 Phase 3 的既定取舍。----
        if (_options?.ToolsEnabled is not false && _tools is { HasTools: true })
        {
            return await RunToolLoopAsync(ctx, assignment, role, message, provider, messages, sink, budget, ct, goalAnchor);
        }

        // ⑤ effort（R2 缝合）：PeakEffortProfile 注入且 Peak 命中时写厂商 token；
        // 否则镜像 ChatRequest.ThinkingEffort 基线默认值 "high"——请求形态与基线逐字节一致。
        var peakEffortToken = await ResolvePeakEffortTokenAsync(assignment);
        var request = new AiChatRequest
        {
            Model = assignment.ModelId,
            Messages = messages,
            Stream = stream && provider.SupportsStreaming,
            ThinkingEffort = peakEffortToken ?? "high",
        };

        var sb = new StringBuilder();
        var sw = Stopwatch.StartNew();
        var tokensIn = 0;
        var tokensOut = 0;

        try
        {
            if (request.Stream)
            {
                await foreach (var chunk in provider.StreamChatAsync(request, ct))
                {
                    if (!string.IsNullOrEmpty(chunk.DeltaContent))
                    {
                        sb.Append(chunk.DeltaContent);
                        // 增量直接写（带 ct）：取消时立即停止泵送。
                        if (sink is not null)
                        {
                            await sink.WriteAsync(new TextDeltaEvent
                            {
                                SessionId = sessionId,
                                MessageId = message.Id,
                                Delta = chunk.DeltaContent,
                            }, ct);
                        }
                    }

                    if (!string.IsNullOrEmpty(chunk.DeltaReasoning) && sink is not null)
                    {
                        await sink.WriteAsync(new ReasoningDeltaEvent
                        {
                            SessionId = sessionId,
                            MessageId = message.Id,
                            Delta = chunk.DeltaReasoning,
                        }, ct);
                    }
                }
            }
            else
            {
                var response = await provider.ChatAsync(request, ct);
                sb.Append(response.Content);
                if (sink is not null && response.Content.Length > 0)
                {
                    await sink.WriteAsync(new TextDeltaEvent
                    {
                        SessionId = sessionId,
                        MessageId = message.Id,
                        Delta = response.Content,
                    }, ct);
                }

                if (response.Usage is not null)
                {
                    tokensIn = response.Usage.PromptTokens;
                    tokensOut = response.Usage.CompletionTokens;
                }
            }
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            var latency = (int)sw.ElapsedMilliseconds;
            message.Content = sb.ToString();
            message.Status = MessageStatus.Cancelled;
            message.LatencyMs = latency;
            await _sessions.UpdateMessageAsync(message);

            // 用户取消不是模型质量问题：不计入画像统计（否则污染失败率打分），
            // 也不产生成本（未计价不猜）。

            // 终态事件不带 ct：取消场景下也要送达。
            await EmitAsync(sink, new MessageCancelledEvent
            {
                SessionId = sessionId,
                MessageId = message.Id,
            });

            return new WorkerOutcome(message.Id, assignment.ProviderId, assignment.ModelId,
                message.Content, Succeeded: false, Cancelled: true, "cancelled by user",
                tokensIn, tokensOut, 0, latency);
        }
        catch (Exception ex)
        {
            sw.Stop();
            var latency = (int)sw.ElapsedMilliseconds;
            var error = ErrorText.Truncate(ex.Message) ?? ex.Message;
            message.Content = sb.ToString(); // 已产出的部分如实保留
            message.Status = MessageStatus.Failed;
            message.Error = error;
            message.LatencyMs = latency;
            await _sessions.UpdateMessageAsync(message);
            _catalog.RecordUsage(assignment.ProviderId, assignment.ModelId, latency, failed: true);
            await SaveCatalogQuietlyAsync();
            _logger.LogWarning(
                "worker call failed: {Provider}/{Model} after {LatencyMs}ms: {Error}",
                assignment.ProviderId, assignment.ModelId, latency, error);

            await EmitAsync(sink, new MessageFailedEvent
            {
                SessionId = sessionId,
                MessageId = message.Id,
                Error = error,
            });

            return new WorkerOutcome(message.Id, assignment.ProviderId, assignment.ModelId,
                message.Content, Succeeded: false, Cancelled: false, error,
                tokensIn, tokensOut, 0, latency);
        }

        sw.Stop();

        // ---- 正常收尾：真实用量 + 画像计价 ----
        var cost = CostTracker.Estimate(assignment.Profile, tokensIn, tokensOut) ?? 0.0;
        message.Content = sb.ToString();
        message.Status = MessageStatus.Completed;
        message.TokensIn = tokensIn;
        message.TokensOut = tokensOut;
        message.CostUsd = cost;
        message.LatencyMs = (int)sw.ElapsedMilliseconds;
        await _sessions.UpdateMessageAsync(message);

        _catalog.RecordUsage(assignment.ProviderId, assignment.ModelId, message.LatencyMs, failed: false);
        await SaveCatalogQuietlyAsync();
        budget?.AddActual(cost);

        // ---- R2 修复 MED-4（B5 输出段生产接点）：直连路径最终答复产出点做输出段验证，
        // 发现仅记录 + WARN（MarkOnly 语义）；null = 零成本跳过（现行为）。----
        if (Guardrail is not null)
        {
            var finalOutput = message.Content;
            var pipeline = Guardrail;
            LogGuardrailFindings("output", () => pipeline.ValidateOutput(finalOutput, cancellationToken: ct));
        }

        await EmitAsync(sink, new MessageCompletedEvent
        {
            SessionId = sessionId,
            MessageId = message.Id,
            TokensIn = tokensIn,
            TokensOut = tokensOut,
            CostUsd = cost,
            LatencyMs = message.LatencyMs,
        });

        return new WorkerOutcome(message.Id, assignment.ProviderId, assignment.ModelId,
            message.Content, Succeeded: true, Cancelled: false, null,
            tokensIn, tokensOut, cost, message.LatencyMs);
    }

    /// <summary>
    /// 工具循环：非流式多轮。每轮把上一轮的助手 tool_calls + tool 结果追加进上下文再问模型，
    /// 直到模型不再请求工具（得到最终答复）或达到 <see cref="MaxToolTurns"/>（诚实中止）。
    /// 占位消息承载最终答复；中间轮次逐条落库（IsFinal == false，HistoryMapper 按配对规则回灌）。
    /// 成本核算：每轮 API 调用各记各的 usage/cost（消息行为事实源），outcome 汇总。
    /// A2（C-LOOP）：持有任务目标时，每轮首注入/滚动更新目标锚定段；每轮末
    /// <see cref="IDeviationDetector.Evaluate"/> 计 strike，连续两 strike → 恢复最近 checkpoint +
    /// <see cref="IEscalationPolicy"/>（默认=暂停待人，事件外抛，MissionController 接线归编排者）。
    /// </summary>
    private async Task<WorkerOutcome> RunToolLoopAsync(
        OrchestrationContext ctx,
        ModelAssignment assignment,
        StrategyRole role,
        ChatMessage finalMessage,
        IAiProvider provider,
        IReadOnlyList<AiChatMessage> messages,
        ChannelWriter<ChatEvent>? sink,
        TurnBudget? budget,
        CancellationToken ct,
        GoalAnchor? goalAnchor)
    {
        var sessionId = ctx.Session.Id;
        var definitions = _tools!.Definitions;
        var conversation = new List<AiChatMessage>(messages);
        var runSw = Stopwatch.StartNew();
        var tokensIn = 0;
        var tokensOut = 0;
        var totalCost = 0.0;
        ChatMessage? inFlightToolMessage = null;
        var maxStrikes = Math.Max(1, _loopGuard?.MaxStrikes ?? 2);
        var strikes = 0;
        AiChatMessage? anchorMessage = null;
        var frozenPrefixCount = CountLeadingSystemMessages(messages);
        var evidence = new List<string>(); // C2：工具输出作为独立佐证（判定不信自报）

        // ⑤ effort（R2 缝合）：本 worker 调用内一次性做峰值档裁决（同一 provider/模型，
        // 探测结论轮内不变）；未注入 profile / Standard 档 → null，全部请求保持基线形态。
        var peakEffortToken = await ResolvePeakEffortTokenAsync(assignment);

        try
        {
            for (var turn = 0; ; turn++)
            {
                if (turn >= MaxToolTurns)
                {
                    var error = $"tool-call loop exceeded the limit ({MaxToolTurns} turns), aborted";
                    _logger.LogWarning(
                        "tool loop aborted: {Provider}/{Model} hit {MaxTurns} turns",
                        assignment.ProviderId, assignment.ModelId, MaxToolTurns);
                    return await FailRunAsync(sink, finalMessage, assignment, error,
                        tokensIn, tokensOut, (int)runSw.ElapsedMilliseconds, countInStats: true,
                        budget, totalCost);
                }

                // ---- F-M3/F-M4 修复（C-CURATE 启用分支）：策展器已接线时每轮重算冻结边界——
                // 上一轮的压缩/策展可能移动对话区起点，沿用首轮边界会把过期值传给策展器，
                // 导致本应冻结的稳定段被截。未接线（默认）时 frozenPrefixCount 无人消费，行为不变。
                // R2 缝合（⑥）：CacheBreakpointsEnabled 时边界值同样进入请求面（缓存断点），
                // 消费方多了一个，沿用同一「每轮重算」纪律。----
                if (Curator is not null || CacheBreakpointsEnabled)
                {
                    frozenPrefixCount = CountLeadingSystemMessages(conversation);
                }

                // ---- 溢出检测（G2-4）：估算 token 超阈值 → Harness Compactor 压缩在途上下文 ----
                conversation = CompactIfOverflowing(conversation, frozenPrefixCount);

                // ---- 目标锚定（A2，C-LOOP）：每轮首持有任务目标 → 注入/滚动更新锚定段 ----
                if (anchorMessage is not null && !conversation.Contains(anchorMessage))
                {
                    anchorMessage = null; // 压缩/策展移除了锚定段：本轮重新注入
                }

                anchorMessage = ApplyGoalAnchor(conversation, goalAnchor, turn, anchorMessage);

                var turnSw = Stopwatch.StartNew();
                // ⑥ 冻结前缀→CacheBreakpoints（R2 缝合，默认 null=请求形态与基线逐字节一致）：
                // 断点 = 最后一条冻结 system 消息的 0-based 索引（该索引及之前构成可缓存前缀）；
                // ClaudeProvider 显式 cache_control，其余厂商按各自 cache 政策忽略。
                var request = new AiChatRequest
                {
                    Model = assignment.ModelId,
                    Messages = conversation,
                    Tools = definitions,
                    Stream = false, // 工具轮必须非流式：拿到完整 tool_calls 才能配对执行
                    ThinkingEffort = peakEffortToken ?? "high",
                    CacheBreakpoints = CacheBreakpointsEnabled && frozenPrefixCount > 0
                        ? new[] { frozenPrefixCount - 1 }
                        : null,
                };
                var response = await provider.ChatAsync(request, ct);
                var turnLatency = (int)turnSw.ElapsedMilliseconds;

                var turnTokensIn = 0;
                var turnTokensOut = 0;
                if (response.Usage is not null)
                {
                    turnTokensIn = response.Usage.PromptTokens;
                    turnTokensOut = response.Usage.CompletionTokens;
                }

                tokensIn += turnTokensIn;
                tokensOut += turnTokensOut;
                var turnCost = CostTracker.Estimate(assignment.Profile, turnTokensIn, turnTokensOut) ?? 0.0;
                totalCost += turnCost;

                // ---- F-M1（R2）：主循环每轮真实 usage 实报 mission 级 token 闸门（null = 现行为）。
                // Exhausted 转换沿行为由闸门订阅方定义；主循环自身不中断，逐轮成本照常诚实核算。----
                BudgetGate?.ReportUsage(turnTokensIn + turnTokensOut);

                if (response.ToolCalls.Count == 0)
                {
                    // ---- C2 完成判定（可选注入，null = 现行为）：最终答复经独立 critique
                    // （判定不信自报，佐证=工具输出），拒绝时有界重试（critique ≤2 轮），
                    // 轮耗尽按策略收敛。重试产出经 reproduce 委托产生：真实 usage/成本照常逐轮
                    // 核算（tokensIn/tokensOut/totalCost/BudgetGate），消耗计入既有任务预算语义不绕过；
                    // 预算不足（budget.HasBudget=false）时不再重试，诚实收敛。----
                    var verifier = CompletionVerifier;
                    var finalContent = response.Content;
                    var finalTokensIn = turnTokensIn;
                    var finalTokensOut = turnTokensOut;
                    var finalCost = turnCost;
                    if (verifier is not null)
                    {
                        var goal = messages.LastOrDefault(m =>
                            string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase))?.Content ?? string.Empty;
                        var loop = new CritiqueLoop(verifier, CritiqueOptions);
                        var outcome = await loop.RunAsync(
                            goal,
                            response.Content,
                            async (feedback, retryCt) =>
                            {
                                // R2 修复 MED-2：重试前把本 turn 已在途消耗（totalCost，含本轮及此前重试轮）
                                // 计入预算裁决——HasBudget 读的是 budget.SpentUsd 快照，而 AddActual 要等
                                // 本 turn 终点才写（见下方终点处），只看 HasBudget 会漏记在途消耗、放行超限重试。
                                // 语义 = SpentUsd + totalCost ≥ MaxUsd 即拒绝；无上限（MaxUsd null）不拦。
                                if (budget is { MaxUsd: { } turnMax } && budget.SpentUsd + totalCost >= turnMax)
                                {
                                    return null; // 任务预算不足（含在途消耗）：不再重试（重试消耗计入预算，不绕过）。
                                }

                                var retryMessages = new List<AiChatMessage>(conversation.Count + 2);
                                retryMessages.AddRange(conversation);
                                retryMessages.Add(new AiChatMessage { Role = "assistant", Content = response.Content });
                                retryMessages.Add(new AiChatMessage
                                {
                                    Role = "user",
                                    Content = BuildCritiqueFeedbackText(feedback),
                                });
                                var retryResponse = await provider.ChatAsync(new AiChatRequest
                                {
                                    Model = assignment.ModelId,
                                    Messages = retryMessages,
                                    Stream = false, // 重试是纯文本产出：不带工具
                                    ThinkingEffort = peakEffortToken ?? "high", // 与主请求同档：重试不静默降档
                                }, retryCt);

                                var retryIn = retryResponse.Usage?.PromptTokens ?? 0;
                                var retryOut = retryResponse.Usage?.CompletionTokens ?? 0;
                                var retryCost = CostTracker.Estimate(assignment.Profile, retryIn, retryOut) ?? 0.0;
                                tokensIn += retryIn;
                                tokensOut += retryOut;
                                totalCost += retryCost;
                                BudgetGate?.ReportUsage(retryIn + retryOut); // F-M1：重试轮同样入闸门
                                finalTokensIn = retryIn;
                                finalTokensOut = retryOut;
                                finalCost = retryCost;
                                return retryResponse.Content;
                            },
                            evidence,
                            ct);

                        if (!outcome.Accepted)
                        {
                            // Fail 策略收敛：诚实失败（不冒充完成）。
                            return await FailRunAsync(sink, finalMessage, assignment,
                                $"completion verification failed after {outcome.CritiqueRoundsUsed} critique round(s): {outcome.Summary}",
                                tokensIn, tokensOut, (int)runSw.ElapsedMilliseconds, countInStats: true,
                                budget, totalCost);
                        }

                        finalContent = outcome.Output;
                        if (!outcome.VerificationPassed)
                        {
                            // AcceptWithFindings 收敛：接受产出，但未决发现必须可见（不静默）。
                            _logger.LogWarning(
                                "[DEGRADED] tool-loop completion converged by policy after {Rounds} critique round(s): {Summary}",
                                outcome.CritiqueRoundsUsed, outcome.Summary);
                        }
                    }

                    // ---- R2 修复 MED-4（B5 输出段生产接点）：工具循环最终答复产出点做输出段验证，
                    // 佐证 = 工具输出（C2 evidence 同源）；发现仅记录 + WARN（MarkOnly 语义）。
                    // null = 零成本跳过（现行为）。----
                    if (Guardrail is not null)
                    {
                        var loopOutput = finalContent;
                        var pipeline = Guardrail;
                        LogGuardrailFindings("output", () => pipeline.ValidateOutput(loopOutput, evidence, ct));
                    }

                    // ---- 最终答复：写回占位消息，按常规收尾 ----
                    runSw.Stop();
                    finalMessage.Content = finalContent;
                    finalMessage.Status = MessageStatus.Completed;
                    finalMessage.TokensIn = finalTokensIn;
                    finalMessage.TokensOut = finalTokensOut;
                    finalMessage.CostUsd = finalCost;
                    finalMessage.LatencyMs = turnLatency;
                    await _sessions.UpdateMessageAsync(finalMessage);

                    if (sink is not null && finalContent.Length > 0)
                    {
                        await sink.WriteAsync(new TextDeltaEvent
                        {
                            SessionId = sessionId,
                            MessageId = finalMessage.Id,
                            Delta = finalContent,
                        }, ct);
                    }

                    _catalog.RecordUsage(assignment.ProviderId, assignment.ModelId, turnLatency, failed: false);
                    await SaveCatalogQuietlyAsync();
                    budget?.AddActual(totalCost);

                    await EmitAsync(sink, new MessageCompletedEvent
                    {
                        SessionId = sessionId,
                        MessageId = finalMessage.Id,
                        TokensIn = finalTokensIn,
                        TokensOut = finalTokensOut,
                        CostUsd = finalCost,
                        LatencyMs = turnLatency,
                    });

                    return new WorkerOutcome(finalMessage.Id, assignment.ProviderId, assignment.ModelId,
                        finalContent, Succeeded: true, Cancelled: false, null,
                        tokensIn, tokensOut, totalCost, (int)runSw.ElapsedMilliseconds);
                }

                // ---- 工具轮：助手 tool_calls 消息落库（IsFinal == false，仅供配对回灌）----
                var turnMessage = new ChatMessage
                {
                    SessionId = sessionId,
                    Role = ChatRole.Assistant,
                    ProviderId = assignment.ProviderId,
                    ModelId = assignment.ModelId,
                    OrchestrationRole = role,
                    ParentMessageId = finalMessage.Id,
                    Label = finalMessage.Label,
                    Content = response.Content,
                    ToolCallsJson = JsonSerializer.Serialize(response.ToolCalls),
                    IsFinal = false,
                    Status = MessageStatus.Completed,
                    TokensIn = turnTokensIn,
                    TokensOut = turnTokensOut,
                    CostUsd = turnCost,
                    LatencyMs = turnLatency,
                };
                var appendedTurn = await _sessions.AppendMessageAsync(turnMessage);
                if (!appendedTurn.IsSuccess)
                {
                    return await FailRunAsync(sink, finalMessage, assignment,
                        appendedTurn.Error ?? "persist failed",
                        tokensIn, tokensOut, (int)runSw.ElapsedMilliseconds, countInStats: true,
                        budget, totalCost);
                }

                await EmitAsync(sink, new AssistantMessageStarted
                {
                    SessionId = sessionId,
                    MessageId = turnMessage.Id,
                    ProviderId = assignment.ProviderId,
                    ModelId = assignment.ModelId,
                    OrchestrationRole = role,
                    ParentMessageId = finalMessage.Id,
                    Label = finalMessage.Label,
                    HasToolCalls = true,
                });
                if (sink is not null && response.Content.Length > 0)
                {
                    await sink.WriteAsync(new TextDeltaEvent
                    {
                        SessionId = sessionId,
                        MessageId = turnMessage.Id,
                        Delta = response.Content,
                    }, ct);
                }

                await EmitAsync(sink, new MessageCompletedEvent
                {
                    SessionId = sessionId,
                    MessageId = turnMessage.Id,
                    TokensIn = turnTokensIn,
                    TokensOut = turnTokensOut,
                    CostUsd = turnCost,
                    LatencyMs = turnLatency,
                });

                conversation.Add(new AiChatMessage
                {
                    Role = "assistant",
                    Content = response.Content,
                    ToolCalls = response.ToolCalls,
                });

                // ---- 逐个执行工具调用：先裁决后执行，结果如实回传模型 ----
                var toolOutputs = new List<string>();
                foreach (var call in response.ToolCalls)
                {
                    var toolMessage = new ChatMessage
                    {
                        SessionId = sessionId,
                        Role = ChatRole.Tool,
                        ProviderId = assignment.ProviderId,
                        ModelId = assignment.ModelId,
                        OrchestrationRole = role,
                        ParentMessageId = turnMessage.Id,
                        Name = call.FunctionName,
                        ToolCallId = call.Id,
                        IsFinal = false,
                        Status = MessageStatus.Pending,
                    };
                    var appendedTool = await _sessions.AppendMessageAsync(toolMessage);
                    if (!appendedTool.IsSuccess)
                    {
                        return await FailRunAsync(sink, finalMessage, assignment,
                            appendedTool.Error ?? "persist failed",
                            tokensIn, tokensOut, (int)runSw.ElapsedMilliseconds, countInStats: true,
                            budget, totalCost);
                    }

                    inFlightToolMessage = toolMessage;
                    await EmitAsync(sink, new ToolCallStartedEvent
                    {
                        SessionId = sessionId,
                        MessageId = toolMessage.Id,
                        ToolCallId = call.Id,
                        ToolName = call.FunctionName,
                        ArgumentsJson = call.ArgumentsJson,
                        ParentMessageId = turnMessage.Id,
                    });

                    var toolSw = Stopwatch.StartNew();
                    var result = await _tools.InvokeAsync(call.FunctionName, call.ArgumentsJson, ct);
                    toolSw.Stop();
                    toolOutputs.Add(result.Output);
                    evidence.Add(result.Output); // C2：独立佐证（判定不信自报）

                    toolMessage.Content = result.Output;
                    toolMessage.Status = result.Success ? MessageStatus.Completed : MessageStatus.Degraded;
                    toolMessage.Error = result.Success ? null : result.Error;
                    toolMessage.LatencyMs = (int)toolSw.ElapsedMilliseconds;
                    await _sessions.UpdateMessageAsync(toolMessage);
                    inFlightToolMessage = null;

                    await EmitAsync(sink, new ToolCallCompletedEvent
                    {
                        SessionId = sessionId,
                        MessageId = toolMessage.Id,
                        ToolCallId = call.Id,
                        ToolName = call.FunctionName,
                        Success = result.Success,
                        Denied = result.Denied,
                        OutputPreview = ErrorText.Truncate(result.Output),
                        LatencyMs = toolMessage.LatencyMs,
                    });

                    conversation.Add(new AiChatMessage
                    {
                        Role = "tool",
                        Content = result.Output,
                        Name = call.FunctionName,
                        ToolCallId = call.Id,
                    });
                }

                // ---- 每轮末：偏离检测 + 升级（A2，C-LOOP）----
                if (_deviationDetector is not null && goalAnchor is not null)
                {
                    var report = _deviationDetector.Evaluate(new DeviationInput(
                        turn, goalAnchor, response.Content, toolOutputs));
                    if (report.Verdict == DeviationVerdict.OffGoal)
                    {
                        strikes++;
                        _logger.LogWarning(
                            "loop-guard: off-goal strike {Strikes}/{Max} at turn {Turn}: {Reason}",
                            strikes, maxStrikes, turn, report.Reason);
                    }
                    else
                    {
                        strikes = 0;
                    }

                    if (strikes >= maxStrikes)
                    {
                        var (restoredSeq, restoredFiles) = RestoreLatestCheckpointQuietly();
                        var reason =
                            $"deviation detected in {strikes} consecutive turns: {report.Reason}" +
                            (restoredSeq is { } seq
                                ? $"; checkpoint {seq} restored {restoredFiles} file(s)"
                                : "; no checkpoint restored");
                        var decision = _escalationPolicy?.Handle(new EscalationContext(
                                turn, strikes, goalAnchor, reason, restoredSeq, restoredFiles))
                            ?? EscalationDecision.Abort;
                        if (decision == EscalationDecision.ContinueWithWarning)
                        {
                            // 策略接管责任：strike 清零继续（升级事件已外抛，可见性由策略侧保证）。
                            _logger.LogWarning(
                                "[DEGRADED] loop-guard escalation continued with warning at turn {Turn}; strikes reset",
                                turn);
                            strikes = 0;
                        }
                        else
                        {
                            var error = decision == EscalationDecision.PauseForHuman
                                ? $"paused for human review (deviation escalation): {reason}"
                                : $"aborted by deviation escalation: {reason}";
                            _logger.LogWarning("tool loop escalated at turn {Turn}: {Error}", turn, error);
                            return await FailRunAsync(sink, finalMessage, assignment, error,
                                tokensIn, tokensOut, (int)runSw.ElapsedMilliseconds, countInStats: true,
                                budget, totalCost);
                        }
                    }
                }

                // ---- 每轮末：上下文策展（C-CURATE，α 唯一接线点；L2 修复：真异步链路）----
                var curated = await CurateIfWiredAsync(conversation, frozenPrefixCount, ct);
                if (!ReferenceEquals(curated, conversation))
                {
                    conversation = curated;
                    if (anchorMessage is not null && !conversation.Contains(anchorMessage))
                    {
                        anchorMessage = null; // 策展重建了对话区：下轮重新注入锚定段
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            runSw.Stop();
            var latency = (int)runSw.ElapsedMilliseconds;

            // 进行中的工具消息与最终答复都落取消态（DB 是事实源，不留 Pending 僵尸）。
            if (inFlightToolMessage is not null)
            {
                inFlightToolMessage.Status = MessageStatus.Cancelled;
                inFlightToolMessage.LatencyMs = latency;
                await _sessions.UpdateMessageAsync(inFlightToolMessage);
            }

            finalMessage.Status = MessageStatus.Cancelled;
            finalMessage.LatencyMs = latency;
            await _sessions.UpdateMessageAsync(finalMessage);

            // 用户取消不是模型质量问题：不计入画像统计，也不产生成本。
            await EmitAsync(sink, new MessageCancelledEvent
            {
                SessionId = sessionId,
                MessageId = finalMessage.Id,
            });

            return new WorkerOutcome(finalMessage.Id, assignment.ProviderId, assignment.ModelId,
                string.Empty, Succeeded: false, Cancelled: true, "cancelled by user",
                tokensIn, tokensOut, 0, latency);
        }
        catch (Exception ex)
        {
            runSw.Stop();
            var error = ErrorText.Truncate(ex.Message) ?? ex.Message;
            _logger.LogWarning(
                "tool loop failed: {Provider}/{Model} after {LatencyMs}ms: {Error}",
                assignment.ProviderId, assignment.ModelId, (int)runSw.ElapsedMilliseconds, error);
            return await FailRunAsync(sink, finalMessage, assignment, error,
                tokensIn, tokensOut, (int)runSw.ElapsedMilliseconds, countInStats: true,
                budget, totalCost);
        }
    }

    /// <summary>
    /// 溢出检测 + 压缩（G2-4）。未装配压缩器/阈值为 0 时原样返回（行为不变）。
    /// 达到阈值 → Compactor（TruncateOldest）压缩 → 头部修复保证 tool 配对完整
    /// （Compactor 逐条丢最旧消息可能拆散 assistant tool_calls 与 tool 应答对，
    /// 请求序列非法）；压缩是设计内行为（非降级），Compactor 自身发布 CompactionTriggeredEvent。
    /// F-M4 修复（C-CURATE 启用分支）：策展器已接线且持有冻结边界时优先走 Compactor 的
    /// prefix-aware 重载（冻结前缀逐字保留，对话区才参与压缩），头部修复同样只作用于
    /// 冻结前缀之后的对话区——压缩不再截走稳定前缀。未接线（默认）时与既有 2 参行为一致。
    /// internal for tests（Reviewer-H P1-1 零覆盖修复）：经 InternalsVisibleTo 直测。
    /// </summary>
    internal List<AiChatMessage> CompactIfOverflowing(List<AiChatMessage> conversation, int frozenPrefixCount = 0)
    {
        var threshold = _compaction?.ThresholdTokens ?? 0;
        if (_compactor is null || threshold <= 0)
        {
            return conversation;
        }

        var estimated = EstimateTokens(conversation);
        if (estimated < threshold)
        {
            return conversation;
        }

        // F-M4：prefix-aware 压缩只在策展启用分支生效（冻结边界有效时）——
        // 未启用分支必须与基线逐字节一致，禁动 legacy 路径。
        var prefixAware = Curator is not null && frozenPrefixCount > 0;

        try
        {
            var result = prefixAware
                ? _compactor.Compact(conversation, threshold, frozenPrefixCount)
                : _compactor.Compact(conversation, threshold);
            if (!result.DidCompact)
            {
                return conversation;
            }

            var repaired = prefixAware
                ? DropBrokenToolPairsAtHead(result.Messages, frozenPrefixCount)
                : DropBrokenToolPairsAtHead(result.Messages);
            _logger.LogInformation(
                "tool-loop context compacted: {Original}→{Compacted} tokens (threshold {Threshold}), {Dropped} broken pair heads dropped",
                result.OriginalTokens, result.CompactedTokens, threshold,
                result.Messages.Count - repaired.Count);
            return repaired;
        }
        catch (Exception ex)
        {
            // 压缩失败不中止对话循环：下一轮按未压缩上下文继续（超限风险由 provider 端显式报错兜底）。
            _logger.LogWarning(
                "[DEGRADED] tool-loop compaction failed, continuing uncompacted: {Error}", ex.Message);
            return conversation;
        }
    }

    /// <summary>4 字符 ≈ 1 token 的既有口径；tool_calls 按函数名+参数 JSON 估算。</summary>
    internal static int EstimateTokens(IReadOnlyList<AiChatMessage> messages)
    {
        var total = 0;
        foreach (var m in messages)
        {
            total += TokenCounter.ApproxTokens(m.Content);
            if (m.ToolCalls is { Count: > 0 })
            {
                foreach (var call in m.ToolCalls)
                {
                    total += TokenCounter.ApproxTokens(call.FunctionName + call.ArgumentsJson);
                }
            }
        }

        return total;
    }

    /// <summary>
    /// 头部修复：从头丢弃不满足配对自洽的消息，直到找到一个合法边界——
    /// 该边界之后（含）不存在"tool 应答缺 assistant 携带方"或"assistant 携带的 tool_call 缺应答"。
    /// 至少保留一条消息（不可能发生全丢：Compactor 保留带保证尾部完整）。
    /// F-M4 修复：frozenPrefixCount &gt; 0 时（C-CURATE 启用分支）开头 N 条为冻结前缀
    /// （起始连续 system 稳定段，不参与 tool 配对），逐字保留；配对修复只作用于其后的对话区。
    /// 默认 0 = 既有全序列修复行为（未启用分支不变）。
    /// </summary>
    internal static List<AiChatMessage> DropBrokenToolPairsAtHead(IReadOnlyList<AiChatMessage> messages, int frozenPrefixCount = 0)
    {
        var prefixCount = Math.Clamp(frozenPrefixCount, 0, messages.Count);
        if (prefixCount > 0)
        {
            var prefix = messages.Take(prefixCount).ToList();
            var dialogue = messages.Skip(prefixCount).ToList();
            if (dialogue.Count > 0)
            {
                prefix.AddRange(DropBrokenToolPairsAtHead(dialogue));
            }

            return prefix;
        }

        var start = 0;
        while (start < messages.Count && !IsSelfConsistentFrom(messages, start))
        {
            start++;
        }

        if (start >= messages.Count)
        {
            // 理论不可达的兜底：至少保留最后一条（最新上下文不能全丢）。
            return new List<AiChatMessage> { messages[^1] };
        }

        return messages.Skip(start).ToList();
    }

    /// <summary>从 <paramref name="start"/> 起，消息序列是否满足 tool 配对自洽（每个 tool 应答在携带方之后，每个 tool_call 有应答）。</summary>
    private static bool IsSelfConsistentFrom(IReadOnlyList<AiChatMessage> messages, int start)
    {
        for (var i = start; i < messages.Count; i++)
        {
            var m = messages[i];
            var isToolResponse = string.Equals(m.Role, "tool", StringComparison.OrdinalIgnoreCase);
            if (isToolResponse)
            {
                // tool 应答的 assistant 携带方必须在本段内（start..i-1 中存在含该 ToolCallId 的消息）。
                var partnerFound = false;
                for (var j = start; j < i; j++)
                {
                    if (HasToolCallWithId(messages[j], m.ToolCallId))
                    {
                        partnerFound = true;
                        break;
                    }
                }

                if (!partnerFound)
                {
                    return false;
                }
            }
            else if (m.ToolCalls is { Count: > 0 })
            {
                // assistant 携带的每个 tool_call 都必须在本段内（i..end）有应答。
                foreach (var call in m.ToolCalls)
                {
                    var answered = false;
                    for (var j = i + 1; j < messages.Count; j++)
                    {
                        if (string.Equals(messages[j].Role, "tool", StringComparison.OrdinalIgnoreCase)
                            && string.Equals(messages[j].ToolCallId, call.Id, StringComparison.Ordinal))
                        {
                            answered = true;
                            break;
                        }
                    }

                    if (!answered)
                    {
                        return false;
                    }
                }
            }
        }

        return true;
    }

    private static bool HasToolCallWithId(AiChatMessage message, string? toolCallId)
    {
        if (message.ToolCalls is null || toolCallId is null)
        {
            return false;
        }

        foreach (var call in message.ToolCalls)
        {
            if (string.Equals(call.Id, toolCallId, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 目标锚定注入（A2，C-LOOP）：持有目标且锚定开启时，每轮首把锚定段写入「滚动消息」——
    /// 首轮追加一条 user 消息，其后原地更新同一条消息内容（不逐轮新增，防上下文膨胀）。
    /// 未持有目标/锚定关闭时原样返回（行为不变）。
    /// </summary>
    private AiChatMessage? ApplyGoalAnchor(
        List<AiChatMessage> conversation,
        GoalAnchor? anchor,
        int turn,
        AiChatMessage? existing)
    {
        if (anchor is null || _loopGuard is not { GoalAnchoringEnabled: true })
        {
            return existing;
        }

        var text = anchor.Render(turn, LatestCheckpointSummary());
        if (existing is not null)
        {
            // ChatMessage 全 init-only：原地改内容非法，改为同位替换新实例（锚定消息为纯文本 user 消息，仅 Role+Content）
            var updated = new AiChatMessage { Role = existing.Role, Content = text };
            var index = conversation.IndexOf(existing);
            if (index >= 0)
            {
                conversation[index] = updated;
            }

            return updated;
        }

        var message = new AiChatMessage { Role = "user", Content = text };
        conversation.Add(message);
        return message;
    }

    /// <summary>最近 checkpoint 摘要（锚定段用）；无 store/无 checkpoint/读取失败 → null（不伪造）。</summary>
    private string? LatestCheckpointSummary()
    {
        if (_checkpoints is null)
        {
            return null;
        }

        var maxChars = Math.Max(40, _loopGuard?.CheckpointSummaryChars ?? 200);
        try
        {
            var latest = _checkpoints.List(1).FirstOrDefault();
            if (latest is null)
            {
                return null;
            }

            var text = $"seq {latest.Seq} · {latest.ToolName} · {latest.CreatedUtc:u} · {latest.Paths.Count} 个文件";
            return text.Length <= maxChars ? text : text[..maxChars] + "…";
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[DEGRADED] loop-guard checkpoint summary unavailable: {Error}", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// 恢复最近 checkpoint（两 strike 升级路径；CheckpointStore 公共 API 只调用不改）。
    /// 失败降级可见（[DEGRADED] 日志）但不抛出——升级链路必须继续走到策略。
    /// </summary>
    private (long? Seq, int RestoredFiles) RestoreLatestCheckpointQuietly()
    {
        if (_checkpoints is null)
        {
            return (null, 0);
        }

        try
        {
            var latest = _checkpoints.List(1).FirstOrDefault();
            if (latest is null)
            {
                return (null, 0);
            }

            var restored = _checkpoints.Restore(latest.Seq);
            _logger.LogInformation(
                "loop-guard restored checkpoint {Seq}: {Count} file(s)", latest.Seq, restored);
            return (latest.Seq, restored);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[DEGRADED] loop-guard checkpoint restore failed: {Error}", ex.Message);
            return (null, 0);
        }
    }

    /// <summary>
    /// 上下文策展（C-CURATE，α 唯一接线点；L2 修复：真异步链路）：装配 <see cref="CurationInput"/>
    /// （冻结前缀边界 = 起始连续 system 稳定段；水位阈值经 <see cref="CuratorThresholdTokens"/> 透传）
    /// 调用 <see cref="IContextCurator.CurateAsync"/>——LLM 摘要等待不再 GetResult() 同步阻塞线程池线程；
    /// 触发压缩时结果经既有 <see cref="DropBrokenToolPairsAtHead"/> 做工具配对兜底修复后
    /// 替换在途上下文（冻结前缀不动是策展器的契约，此处只兜底配对完整性）。
    /// 未装配/未触发/异常 → 原样返回（行为不变；策展失败不中止循环）；
    /// 取消（OperationCanceledException）向上传播交还工具循环取消路径（不吞）。
    /// </summary>
    private async Task<List<AiChatMessage>> CurateIfWiredAsync(
        List<AiChatMessage> conversation, int frozenPrefixCount, CancellationToken ct)
    {
        var curator = Curator;
        if (curator is null)
        {
            return conversation;
        }

        try
        {
            var result = await curator.CurateAsync(new CurationInput
            {
                Conversation = conversation,
                FrozenPrefixCount = frozenPrefixCount,
                WatermarkThresholdTokens = CuratorThresholdTokens,
            }, ct);
            if (!result.DidCurate)
            {
                return conversation;
            }

            // F-M4：策展结果的兜底配对修复同样不动冻结前缀（边界为本轮重算后的当前值）。
            var repaired = DropBrokenToolPairsAtHead(result.Conversation, frozenPrefixCount);
            _logger.LogInformation(
                "tool-loop context curated: {Original}→{Curated} tokens, {Facts} overflowed fact(s)",
                result.OriginalTokens, result.CuratedTokens, result.OverflowedFacts.Count);
            return repaired.ToList();
        }
        catch (OperationCanceledException)
        {
            throw; // 取消不吞：交还工具循环取消路径（消息落诚实 Cancelled 终态）。
        }
        catch (Exception ex)
        {
            // 策展失败不中止对话循环：下一轮按未策展上下文继续（超限风险由 provider 端显式报错兜底）。
            _logger.LogWarning("[DEGRADED] tool-loop curation failed, continuing uncurated: {Error}", ex.Message);
            return conversation;
        }
    }

    /// <summary>
    /// critique 重试反馈的用户消息文本。理由/反馈文本过 <see cref="SensitiveTextScrubber"/>
    /// （安全硬门 #4：验证器产出的文本不得把密钥等敏感形态带进模型上下文/日志）。
    /// </summary>
    private static string BuildCritiqueFeedbackText(string? feedback)
    {
        var scrubbed = SensitiveTextScrubber.Scrub(feedback ?? string.Empty);
        return string.IsNullOrWhiteSpace(scrubbed)
            ? "[completion-critique] 上一个最终答复未通过独立校验，请修正并重新给出最终答复。"
            : $"[completion-critique] 上一个最终答复未通过独立校验，请根据以下反馈修正并重新给出最终答复：\n{scrubbed}";
    }

    /// <summary>
    /// ⑤ effort 峰值档裁决（R2 缝合，可选链路）：未注入 <see cref="PeakEffortProfile"/> /
    /// 未请求峰值 / 裁决 Standard（fail-closed：探测未注入或 Missing 一律回落）→ null，
    /// 调用方沿用基线 effort 形态；Peak 命中 → 厂商 token（O=xhigh / A=extended-thinking /
    /// G=deep-think）。裁决异常不阻断调用（[DEGRADED] 可见后回落基线）。
    /// </summary>
    private async Task<string?> ResolvePeakEffortTokenAsync(ModelAssignment assignment)
    {
        var profile = PeakEffortProfile;
        if (profile is null || !PeakEffortRequested)
        {
            return null;
        }

        try
        {
            var decision = await profile.DecideAsync(assignment.ProviderId, peakRequested: true);
            if (decision.Tier == EffortTier.Peak && decision.VendorToken is not null)
            {
                _logger.LogInformation(
                    "peak effort granted for {Provider}/{Model}: token={Token} ({Reason})",
                    assignment.ProviderId, assignment.ModelId, decision.VendorToken, decision.Reason);
                return decision.VendorToken;
            }

            _logger.LogInformation(
                "peak effort not granted for {Provider}/{Model}: {Reason}",
                assignment.ProviderId, assignment.ModelId, decision.Reason);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "[DEGRADED] peak effort decision failed, falling back to baseline effort: {Error}", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// R2 修复 MED-4：guardrail 输入/输出段发现的唯一处置点——只记录 + WARN（MarkOnly 语义：
    /// 不阻断、不改变任何流程走向）。调用方已判空（<see cref="Guardrail"/> 非 null 才进来）；
    /// 验证异常不吞取消（OperationCanceledException 一律向上传播），其余按 [DEGRADED] 记录后
    /// 继续原流程（与 GuardrailPipeline 内单验证器降级口径一致）。发现文本由验证器负责脱敏。
    /// </summary>
    private void LogGuardrailFindings(string stage, Func<GuardrailVerdict> validate)
    {
        GuardrailVerdict verdict;
        try
        {
            verdict = validate();
        }
        catch (OperationCanceledException)
        {
            throw; // 取消不吞（安全硬门口径）。
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "[DEGRADED] guardrail {Stage} validation failed, skipped: {Error}",
                stage, ErrorText.Truncate(ex.Message) ?? ex.Message);
            return;
        }

        if (verdict.HasNoFindings)
        {
            return;
        }

        _logger.LogWarning(
            "[GUARDRAIL] {Stage} findings ({Count}): {Summary}",
            stage,
            verdict.Findings.Count,
            string.Join("; ", verdict.Findings.Select(f => $"{f.Code}:{f.Message}")));
    }

    /// <summary>冻结前缀边界：起始连续 system 消息数（遇到首个非 system 即止；空序列 → 0）。</summary>
    private static int CountLeadingSystemMessages(IReadOnlyList<AiChatMessage> messages)
    {
        var count = 0;
        while (count < messages.Count
               && string.Equals(messages[count].Role, "system", StringComparison.OrdinalIgnoreCase))
        {
            count++;
        }

        return count;
    }

    /// <summary>
    /// 失败收尾：最终答复落 Failed 终态 + 画像统计（可选）+ 终态事件 + outcome。
    /// 预算纪律：工具循环中途失败时，此前各轮 API 调用的成本已真实发生且逐条落库，
    /// 必须同样记入 TurnBudget（spentUsd）——否则同轮后续 worker/judge 会再次花满预算，
    /// 静默突破用户单轮上限。outcome.CostUsd 如实返回累计成本而非 0。
    /// </summary>
    private async Task<WorkerOutcome> FailRunAsync(
        ChannelWriter<ChatEvent>? sink,
        ChatMessage finalMessage,
        ModelAssignment assignment,
        string error,
        int tokensIn,
        int tokensOut,
        int latencyMs,
        bool countInStats,
        TurnBudget? budget,
        double spentUsd)
    {
        finalMessage.Status = MessageStatus.Failed;
        finalMessage.Error = error;
        finalMessage.TokensIn = tokensIn;
        finalMessage.TokensOut = tokensOut;
        finalMessage.LatencyMs = latencyMs;
        await _sessions.UpdateMessageAsync(finalMessage);

        if (countInStats)
        {
            _catalog.RecordUsage(assignment.ProviderId, assignment.ModelId, latencyMs, failed: true);
            await SaveCatalogQuietlyAsync();
        }

        if (spentUsd > 0)
        {
            budget?.AddActual(spentUsd);
        }

        await EmitAsync(sink, new MessageFailedEvent
        {
            SessionId = finalMessage.SessionId,
            MessageId = finalMessage.Id,
            Error = error,
        });

        return new WorkerOutcome(finalMessage.Id, assignment.ProviderId, assignment.ModelId,
            string.Empty, Succeeded: false, Cancelled: false, error,
            tokensIn, tokensOut, spentUsd, latencyMs);
    }

    /// <summary>
    /// 终态事件容错写入：消费方已停（ChannelClosed）或已取消时不抛出——
    /// 终态已落库，事件流只是通知通道。
    /// </summary>
    private static async Task EmitAsync(ChannelWriter<ChatEvent>? sink, ChatEvent ev)
    {
        if (sink is null)
        {
            return;
        }

        try
        {
            await sink.WriteAsync(ev, CancellationToken.None);
        }
        catch (ChannelClosedException)
        {
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task SaveCatalogQuietlyAsync()
    {
        try
        {
            await _catalog.SaveAsync();
        }
        catch (Exception ex)
        {
            // 统计持久化失败不影响主流程（下次保存会覆盖），但降级必须可见。
            _logger.LogWarning("[DEGRADED] failed to persist model profile stats: {Error}", ex.Message);
        }
    }
}
