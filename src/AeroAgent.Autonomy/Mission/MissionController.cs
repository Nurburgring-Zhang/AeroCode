using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AeroAgent.Autonomy.Analysis;
using AeroAgent.Autonomy.Clarification;
using AeroAgent.Autonomy.Data;
using AeroAgent.Autonomy.Experience;
using AeroAgent.Autonomy.Llm;
using AeroAgent.Autonomy.Retrospective;
using AeroAgent.Autonomy.Steelman;
using AeroAgent.Conversation.Models;
using AeroAgent.Moa.LoopGuard;
using AeroAgent.Moa.Tools.Workspace;
using AeroCode.Harness.Curation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AeroAgent.Autonomy.Mission;

/// <summary>
/// 任务元控制器（G1 差距项的真实实现）：任务接收→分析→澄清→钢人→规划→执行→
/// 校验→复盘→经验写入的完整状态机。每一步真实运行并把产物落库（数据库是事实源），
/// 状态转移全程留痕；执行失败不跳过复盘。策略由任务分析驱动（非人工下拉）。
/// </summary>
public sealed class MissionController
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly TaskAnalyzer _analyzer;
    private readonly StrategySelector _strategySelector;
    private readonly ClarificationGate _clarificationGate;
    private readonly SteelmanProtocol _steelman;
    private readonly MissionStore _store;
    private readonly IMissionExecutor _executor;
    private readonly RetrospectiveEngine _retrospective;
    private readonly ExperienceInjector _experience;
    private readonly AutonomyLlmClient _llm;
    private readonly AutonomyDataPaths _paths;
    private readonly ILogger<MissionController> _logger;
    private readonly IEscalationPolicy? _escalationPolicy;
    private readonly CheckpointStore? _checkpoints;
    private readonly ResumePlanner? _resumePlanner;
    private readonly ConcurrentDictionary<string, EscalationRequest> _pendingEscalations = new();
    private int _escalationCount;

    public MissionController(
        TaskAnalyzer analyzer,
        StrategySelector strategySelector,
        ClarificationGate clarificationGate,
        SteelmanProtocol steelman,
        MissionStore store,
        IMissionExecutor executor,
        RetrospectiveEngine retrospective,
        ExperienceInjector experience,
        AutonomyLlmClient llm,
        AutonomyDataPaths paths,
        ILogger<MissionController>? logger = null,
        IEscalationPolicy? escalationPolicy = null,
        CheckpointStore? checkpoints = null)
    {
        _analyzer = analyzer ?? throw new ArgumentNullException(nameof(analyzer));
        _strategySelector = strategySelector ?? throw new ArgumentNullException(nameof(strategySelector));
        _clarificationGate = clarificationGate ?? throw new ArgumentNullException(nameof(clarificationGate));
        _steelman = steelman ?? throw new ArgumentNullException(nameof(steelman));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _retrospective = retrospective ?? throw new ArgumentNullException(nameof(retrospective));
        _experience = experience ?? throw new ArgumentNullException(nameof(experience));
        _llm = llm ?? throw new ArgumentNullException(nameof(llm));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _logger = logger ?? NullLogger<MissionController>.Instance;
        _escalationPolicy = escalationPolicy;
        _checkpoints = checkpoints;
        _resumePlanner = checkpoints is null ? null : new ResumePlanner(checkpoints.Root);
        if (_escalationPolicy is not null)
        {
            // C-LOOP 接线（R1 缝合 #13）：订阅一次性审批凭据。凭据只在人显式审批时消费
            //（TryApproveEscalation → TryConsume 一次性语义）；本类绝不自动代批、绝不重复消费。
            _escalationPolicy.EscalationRaised += OnEscalationRaised;
        }
    }

    /// <summary>偏离升级受理事件（UI/观测可订阅；订阅方异常不影响受理链路）。</summary>
    public event Action<EscalationRequest>? EscalationReceived;

    /// <summary>
    /// 实时任务事件流（增量观测面，不改变 RunAsync 终态语义）：状态机转换（阶段进入/离开，
    /// 每次 <c>AdvanceAsync</c> 留痕后发一条 <see cref="MissionEventKind.StateTransition"/>）、
    /// 步骤进展（Planning 产出计划后发 <see cref="MissionEventKind.StepProgress"/>）、
    /// 升级产生（受理时发 <see cref="MissionEventKind.EscalationRaised"/>，凭据 id 截断不入事件）。
    /// 逐订阅者 try/catch 吞异常——任何订阅方故障绝不阻断 mission；无订阅者零开销。
    /// </summary>
    public event EventHandler<MissionEvent>? MissionEventRaised;

    /// <summary>待人工审批的升级请求快照（Id → 一次性凭据；顺序不保证）。</summary>
    public IReadOnlyList<EscalationRequest> PendingEscalations => _pendingEscalations.Values.ToArray();

    /// <summary>累计受理的升级请求数（诊断）。</summary>
    public int EscalationCount => Volatile.Read(ref _escalationCount);

    /// <summary>
    /// 升级凭据受理（C-LOOP）：入队留痕 + 转发观测事件 + 日志可见。
    /// 任务的「暂停」由上游升级决定（PauseForHuman → 工具循环诚实终止）承载，
    /// 本类负责把凭据真实保存到人可审批的位置，绝不自动代批。
    /// </summary>
    private void OnEscalationRaised(EscalationRequest request)
    {
        _pendingEscalations[request.Id] = request;
        Interlocked.Increment(ref _escalationCount);
        // S-L2（硬门约定）：Reason 进入日志前过既有敏感形态脱敏器（复用 SensitiveTextScrubber，
        // 不新造第二套）——升级理由可能拼接自模型/工具输出，凭据形态不得原样入日志。
        // S-LOW-5：凭据全量 id 不进日志——截断为前 8 字符 + 省略号（MaskCredentialId）。
        _logger.LogWarning(
            "任务升级待人工审批：id={EscalationId} turn={Turn} strikes={Strikes} reason={Reason}（凭据一次性，待审批）",
            MaskCredentialId(request.Id), request.Turn, request.Strikes, SensitiveTextScrubber.Scrub(request.Reason));
        try
        {
            EscalationReceived?.Invoke(request);
        }
        catch
        {
            // 订阅方（UI）异常不阻断升级受理链路。
        }

        // 实时事件流：升级产生（Detail 只含截断凭据 id，全量凭据不入事件/日志）。
        FireMissionEvent(
            MissionEventKind.EscalationRaised,
            null,
            $"升级待人工审批：凭据 {MaskCredentialId(request.Id)} turn={request.Turn} strikes={request.Strikes}");
    }

    /// <summary>
    /// 人工审批一次升级请求（一次性消费语义的显式消费点）：凭据
    /// <see cref="EscalationRequest.TryConsume"/> 首次消费返回 true 并移出待审批队列；
    /// 已消费/不存在 → false（不重复消费、不伪造成功）。本方法只消费凭据，
    /// 不代批任务语义；后续恢复由人显式调用 <see cref="ResumeLatestCheckpointAsync"/>。
    /// </summary>
    public bool TryApproveEscalation(string escalationId)
    {
        if (string.IsNullOrWhiteSpace(escalationId))
        {
            return false;
        }

        // S-LOW-5：凭据全量 id 不进日志（一律 MaskCredentialId 截断形态）。
        if (!_pendingEscalations.TryGetValue(escalationId, out var request))
        {
            _logger.LogWarning("升级审批被拒：凭据 {EscalationId} 不存在或已移出待审批队列", MaskCredentialId(escalationId));
            return false;
        }

        if (!request.TryConsume())
        {
            _pendingEscalations.TryRemove(escalationId, out _);
            _logger.LogWarning("升级审批被拒：凭据 {EscalationId} 已被消费（一次性语义，不可重放）", MaskCredentialId(escalationId));
            return false;
        }

        _pendingEscalations.TryRemove(escalationId, out _);
        _logger.LogInformation("升级凭据 {EscalationId} 已被人审批消费（一次性）", MaskCredentialId(escalationId));
        return true;
    }

    /// <summary>
    /// C-RESUME 恢复路径（R1 缝合 #13）：用 <see cref="ResumePlanner"/> 构建最近有效
    /// checkpoint 的续跑计划（fail-closed），再重放该 checkpoint（CheckpointStore.Restore）。
    /// 未接线 checkpoint 存储 / 无有效 checkpoint / 重放失败均如实返回失败结果——
    /// 可见、不抛出、不阻断调用方；恢复动作只依据显式计划，不伪造恢复。
    /// </summary>
    public async Task<MissionResumeResult> ResumeLatestCheckpointAsync(
        long? checkpointSeq = null, CancellationToken ct = default)
    {
        if (_checkpoints is null || _resumePlanner is null)
        {
            _logger.LogWarning("[DEGRADED] 恢复请求被拒：MissionController 未接线 checkpoint 存储（组合根未提供）");
            return MissionResumeResult.Failed("checkpoint store not wired; resume unavailable");
        }

        ResumePlan? plan;
        try
        {
            plan = _resumePlanner.TryBuildResumePlan(checkpointSeq);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[DEGRADED] 续跑计划构建失败（如实可见，不阻断）：{Error}", ex.Message);
            return MissionResumeResult.Failed($"resume plan build failed: {ex.Message}");
        }

        if (plan is null)
        {
            _logger.LogInformation(
                "无有效 checkpoint 可恢复（seq={Seq}）——如实返回，不伪造恢复",
                checkpointSeq?.ToString(CultureInfo.InvariantCulture) ?? "latest");
            return MissionResumeResult.Failed("no valid checkpoint available");
        }

        var skipped = plan.Actions.Count(a => a.Kind == ResumeActionKind.SkipUntracked);
        try
        {
            var restored = await Task.Run(() => _checkpoints.Restore(plan.CheckpointSeq), ct).ConfigureAwait(false);
            var summary =
                $"checkpoint {plan.CheckpointSeq} 已重放（{plan.ToolName}）：恢复 {restored} 个文件，" +
                $"计划动作 {plan.Actions.Count} 项，如实跳过 {skipped} 项（超大文件未被捕获）";
            _logger.LogInformation(
                "checkpoint {Seq} 重放完成：restored={Restored} planned={Planned} skipped={Skipped}",
                plan.CheckpointSeq, restored, plan.Actions.Count, skipped);
            return new MissionResumeResult(true, plan.CheckpointSeq, restored, plan.Actions.Count, null, summary);
        }
        catch (OperationCanceledException)
        {
            return MissionResumeResult.Failed("resume cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "[DEGRADED] checkpoint {Seq} 重放失败（如实可见，不阻断）：{Error}", plan.CheckpointSeq, ex.Message);
            return MissionResumeResult.Failed($"checkpoint {plan.CheckpointSeq} restore failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 运行一次完整任务。返回终态 MissionRecord（含全部阶段产物与转移轨迹）。
    /// R3 缝合（α S8）：mission 生命周期钩子在入口/finally 触发——桌面端无订阅者 = 零行为变化。
    /// </summary>
    public async Task<MissionRecord> RunAsync(string taskText, MissionRunOptions? options = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(taskText))
        {
            throw new ArgumentException("任务文本不能为空。", nameof(taskText));
        }

        var effectiveOptions = options ?? new MissionRunOptions();
        var record = new MissionRecord { TaskText = taskText };

        // R3 缝合（α S8）：mission 生命周期钩子（Android 前台保活的触发面）。
        // 触发一律 try/catch 吞异常，绝不阻断 mission；无订阅者 null 检查短路（零开销）。
        FireMissionStarted(record.Id);
        try
        {
            return await RunCoreAsync(taskText, record, effectiveOptions, ct).ConfigureAwait(false);
        }
        finally
        {
            FireMissionStopped(record.Id);
        }
    }

    /// <summary>R3 缝合（α S8）：触发 mission 启动钩子（无订阅者零开销；异常吞掉绝不阻断 mission）。</summary>
    private static void FireMissionStarted(string missionId)
    {
        var hook = MissionLifetimeHook.MissionStarted;
        if (hook is null)
        {
            return;
        }

        try
        {
            hook(missionId);
        }
        catch
        {
            // 契约：订阅方必须不抛；即便抛了也绝不阻断 mission 本身。
        }
    }

    /// <summary>R3 缝合（α S8）：触发 mission 结束钩子（成功/失败/取消统一；异常吞掉）。
    /// R4 δ-4：携带 missionId，停止侧据此只解除该 mission 的保活。</summary>
    private static void FireMissionStopped(string missionId)
    {
        var hook = MissionLifetimeHook.MissionStopped;
        if (hook is null)
        {
            return;
        }

        try
        {
            hook(missionId);
        }
        catch
        {
            // 契约：订阅方必须不抛；即便抛了也绝不阻断 mission 收尾。
        }
    }

    /// <summary>R3 缝合（α S8）：mission 状态机本体（原 RunAsync 实现，钩子层在其外包裹）。</summary>
    private async Task<MissionRecord> RunCoreAsync(
        string taskText, MissionRecord record, MissionRunOptions options, CancellationToken ct)
    {
        var transitions = new List<MissionTransition>();

        try
        {
            await _store.EnsureCreatedAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return await CancelAsync(record, transitions, CancellationToken.None);
        }
        await AdvanceAsync(record, MissionState.Received, transitions, "任务已接收", ct);

        // ---------- Analyzed ----------
        TaskAnalysis analysis;
        StrategyDecision strategyDecision;
        try
        {
            analysis = await _analyzer.AnalyzeAsync(taskText, ct);
            strategyDecision = options.StrategyOverride is { } overrideStrategy
                ? new StrategyDecision(overrideStrategy, $"显式策略覆盖（人工指定 {overrideStrategy}）")
                : _strategySelector.Select(analysis);

            record.AnalysisJson = JsonSerializer.Serialize(analysis, JsonOpts);
            record.Strategy = strategyDecision.Strategy.ToString();
            record.StrategyRationale = strategyDecision.Rationale;
            await AdvanceAsync(record, MissionState.Analyzed, transitions,
                $"类型={analysis.Type} 复杂度={analysis.Complexity} 策略={strategyDecision.Strategy}", ct);
        }
        catch (OperationCanceledException) { return await CancelAsync(record, transitions, ct); }
        catch (Exception ex)
        {
            return await FailAsync(record, transitions, $"Analyzed 阶段失败: {ex.Message}", retroOutcome: null, ct);
        }

        // ---------- Clarification ----------
        var effectiveTaskText = taskText;
        try
        {
            var clarification = await _clarificationGate.EvaluateAsync(taskText, options.ClarificationThreshold, ct);
            IReadOnlyList<string> answers = Array.Empty<string>();
            var unanswered = 0;

            if (clarification.RequiresClarification && clarification.Questions.Count > 0)
            {
                if (options.ClarificationResponder is { } responder)
                {
                    answers = await responder.AnswerAsync(clarification.Questions, ct);
                    unanswered = Math.Max(0, clarification.Questions.Count - answers.Count);
                }
                else
                {
                    unanswered = clarification.Questions.Count;
                    _logger.LogWarning("[DEGRADED] 澄清门触发 {Count} 个问题但无应答方，按假设继续并如实记录。", unanswered);
                }

                if (answers.Count > 0)
                {
                    var qb = new System.Text.StringBuilder();
                    for (var i = 0; i < answers.Count && i < clarification.Questions.Count; i++)
                    {
                        qb.AppendLine($"补充澄清（{clarification.Questions[i].Dimension}）: {clarification.Questions[i].Question} → {answers[i]}");
                    }

                    effectiveTaskText = taskText + Environment.NewLine + qb.ToString().TrimEnd();
                }
            }

            record.ClarificationJson = JsonSerializer.Serialize(new
            {
                clarification.AmbiguityScore,
                clarification.RequiresClarification,
                clarification.Questions,
                Answers = answers,
                UnansweredCount = unanswered,
                clarification.Source,
            }, JsonOpts);
            await AdvanceAsync(record, MissionState.Clarification, transitions,
                $"歧义度={clarification.AmbiguityScore:F2} 需澄清={clarification.RequiresClarification} 未答={unanswered}", ct);
        }
        catch (OperationCanceledException) { return await CancelAsync(record, transitions, ct); }
        catch (Exception ex)
        {
            return await FailAsync(record, transitions, $"Clarification 阶段失败: {ex.Message}", null, ct);
        }

        // ---------- Steelman ----------
        SteelmanRecord steelman;
        try
        {
            steelman = await _steelman.RunAsync(effectiveTaskText, options.SteelmanMode, options.SteelmanResponder, ct);
            if (!string.IsNullOrWhiteSpace(steelman.KeyQuestionAnswer))
            {
                effectiveTaskText += Environment.NewLine + $"钢人关键问题应答: {steelman.KeyQuestionAnswer}";
            }

            record.SteelmanJson = JsonSerializer.Serialize(steelman, JsonOpts);
            await AdvanceAsync(record, MissionState.Steelman, transitions,
                $"模式={steelman.Mode} 降级={steelman.DegradedToAutoApprove} 来源={steelman.Source}", ct);
        }
        catch (OperationCanceledException) { return await CancelAsync(record, transitions, ct); }
        catch (Exception ex)
        {
            return await FailAsync(record, transitions, $"Steelman 阶段失败: {ex.Message}", null, ct);
        }

        // ---------- Planning ----------
        MissionPlan plan;
        try
        {
            plan = await BuildPlanAsync(effectiveTaskText, analysis, steelman, ct);
            record.PlanJson = JsonSerializer.Serialize(plan, JsonOpts);
            await AdvanceAsync(record, MissionState.Planning, transitions,
                $"计划 {plan.Steps.Count} 步（来源 {plan.Source}）", ct);
            // 实时事件流：步骤进展。计划步数是真实可观测事实（执行器原子运行，
            // 不伪造逐步进展）；执行起止由 Executing 的两条 StateTransition 事件承载。
            FireMissionEvent(MissionEventKind.StepProgress, MissionState.Planning,
                $"计划 {plan.Steps.Count} 步（来源 {plan.Source}）");
        }
        catch (OperationCanceledException) { return await CancelAsync(record, transitions, ct); }
        catch (Exception ex)
        {
            return await FailAsync(record, transitions, $"Planning 阶段失败: {ex.Message}", null, ct);
        }

        // ---------- Executing ----------
        MissionExecutionOutcome? outcome = null;
        try
        {
            var injection = await _experience.BuildSystemPromptAsync(options.MaxLessonsInjected, ct);
            var context = new MissionExecutionContext(
                record.Id, effectiveTaskText, injection.SystemPrompt, strategyDecision.Strategy, analysis);

            // 进入执行期也走 AdvanceAsync 留痕（执行前落库，执行可能耗时很久）；
            // 直接赋值 State 会跳过迁移轨迹，已被 ValidateTransition 禁止。
            await AdvanceAsync(record, MissionState.Executing, transitions, "进入执行期（执行前落库）", ct);
            outcome = await _executor.ExecuteAsync(context, ct);

            record.SessionId = outcome.SessionId;
            record.ExecutionJson = JsonSerializer.Serialize(outcome, JsonOpts);
            await AdvanceAsync(record, MissionState.Executing, transitions,
                $"执行完成 succeeded={outcome.Succeeded} messages={outcome.AssistantMessages} cost=${outcome.TotalCostUsd:F4}", ct);
        }
        catch (OperationCanceledException) { return await CancelAsync(record, transitions, ct); }
        catch (Exception ex)
        {
            outcome = new MissionExecutionOutcome(false, false, string.Empty, ex.Message, record.SessionId, 0, 0);
            record.ExecutionJson = JsonSerializer.Serialize(outcome, JsonOpts);
            return await FailAsync(record, transitions, $"Executing 阶段异常: {ex.Message}", outcome, ct);
        }

        // ---------- Verifying ----------
        VerificationResult verification;
        try
        {
            verification = await VerifyAsync(plan, outcome, ct);
            record.VerificationJson = JsonSerializer.Serialize(verification, JsonOpts);
            await AdvanceAsync(record, MissionState.Verifying, transitions,
                $"校验 passed={verification.Passed}（{verification.Checks.Count} 项检查）", ct);
        }
        catch (OperationCanceledException) { return await CancelAsync(record, transitions, ct); }
        catch (Exception ex)
        {
            return await FailAsync(record, transitions, $"Verifying 阶段失败: {ex.Message}", outcome, ct);
        }

        // ---------- Retrospective + ExperienceWritten（失败也必达）----------
        return await FinishAsync(record, transitions, outcome, verification, ct);
    }

    // ============ 阶段内部实现 ============

    private async Task<MissionPlan> BuildPlanAsync(
        string effectiveTaskText, TaskAnalysis analysis, SteelmanRecord steelman, CancellationToken ct)
    {
        if (_llm.IsAvailable)
        {
            var system =
                "你是任务规划器。把用户任务拆成可执行步骤，只输出 JSON：\n" +
                "{\"steps\":[{\"title\":\"步骤标题\",\"description\":\"做什么\",\"acceptance\":\"如何判定该步完成\"}]}\n" +
                "要求 1-6 步；每步 acceptance 必须可检验；不要输出其他文本。";
            var user = $"任务: {effectiveTaskText}\n任务类型: {analysis.Type}；复杂度: {analysis.Complexity}。\n" +
                       $"钢人论证提示的风险: {steelman.ConArgument}";
            var completion = await _llm.CompleteAsync(system, user, temperature: 0.2, ct);
            if (completion is not null)
            {
                var plan = TryParsePlan(completion.Content);
                if (plan is not null)
                {
                    return plan with { Source = "llm" };
                }

                _logger.LogWarning("[DEGRADED] LLM 计划输出不可解析，退回确定性计划。");
            }
        }
        else
        {
            _logger.LogWarning("[DEGRADED] 无已配置 LLM provider，Planning 使用确定性推导。");
        }

        return BuildHeuristicPlan(effectiveTaskText, analysis);
    }

    internal static MissionPlan? TryParsePlan(string llmOutput)
    {
        var start = llmOutput.IndexOf('{');
        var end = llmOutput.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        try
        {
            using var doc = JsonDocument.Parse(llmOutput[start..(end + 1)]);
            if (!doc.RootElement.TryGetProperty("steps", out var stepsEl) || stepsEl.ValueKind != JsonValueKind.Array)
                return null;

            var steps = new List<PlanStep>();
            foreach (var s in stepsEl.EnumerateArray())
            {
                var title = s.TryGetProperty("title", out var t) ? t.GetString() : null;
                var desc = s.TryGetProperty("description", out var d) ? d.GetString() : null;
                var acceptance = s.TryGetProperty("acceptance", out var a) ? a.GetString() : null;
                if (string.IsNullOrWhiteSpace(title)) continue;
                steps.Add(new PlanStep(
                    title!.Trim(),
                    (desc ?? string.Empty).Trim(),
                    string.IsNullOrWhiteSpace(acceptance) ? "产出与该步标题直接对应且可检查" : acceptance!.Trim()));
                if (steps.Count >= 6) break;
            }

            return steps.Count == 0 ? null : new MissionPlan { Steps = steps };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static MissionPlan BuildHeuristicPlan(string taskText, TaskAnalysis analysis)
    {
        var subject = taskText.Trim();
        if (subject.Length > 60) subject = subject[..60];

        var steps = new List<PlanStep>
        {
            new("准备与上下文确认",
                $"确认执行「{subject}」所需的输入、权限与环境真实可用",
                "所需输入与能力清单齐备，缺失项已显式记录"),
            new("核心执行",
                $"按任务原文要求真实执行「{subject}」的主体工作（类型={analysis.Type}，复杂度={analysis.Complexity}）",
                "产出与任务目标直接对应的可检验成果，无编造内容"),
            new("自检与交付",
                "对产出逐项对照验收标准自检，整理交付物与证据",
                "自检结论与证据落库，失败项如实标注"),
        };

        if (analysis.Complexity <= 2)
        {
            // 简单任务不强行三步：只保留核心执行 + 自检。
            steps.RemoveAt(0);
        }

        return new MissionPlan { Steps = steps, Source = "heuristic-degraded" };
    }

    private async Task<VerificationResult> VerifyAsync(MissionPlan plan, MissionExecutionOutcome outcome, CancellationToken ct)
    {
        var checks = new List<VerificationCheck>
        {
            new("执行成功",
                outcome.Succeeded,
                outcome.Succeeded
                    ? $"会话 {outcome.SessionId}，助手消息 {outcome.AssistantMessages} 条，成本 ${outcome.TotalCostUsd:F4}"
                    : $"执行失败: {outcome.Error ?? "被取消"}"),
            new("产出内容非空",
                !string.IsNullOrWhiteSpace(outcome.FinalContent),
                $"最终内容长度 {outcome.FinalContent?.Length ?? 0} 字符"),
            new("产出内容达到最小可检验规模(≥20字符)",
                (outcome.FinalContent?.Length ?? 0) >= 20,
                $"长度 {outcome.FinalContent?.Length ?? 0}，阈值 20"),
        };

        // 有 LLM 时追加真实的内容-验收对照判断（对计划首步验收标准）。
        if (_llm.IsAvailable && outcome.Succeeded && plan.Steps.Count > 0)
        {
            var system =
                "你是验收评审员。判断【产出内容】是否满足【验收标准】。只输出 JSON：\n" +
                "{\"passed\":true或false,\"reason\":\"一句话理由\"}";
            var user = $"验收标准: {plan.Steps[^1].AcceptanceCriteria}\n\n产出内容（截断 3000 字符）:\n" +
                       Truncate(outcome.FinalContent, 3000);
            var completion = await _llm.CompleteAsync(system, user, temperature: 0.0, ct);
            if (completion is not null)
            {
                var verdict = TryParseVerdict(completion.Content);
                checks.Add(new VerificationCheck(
                    "LLM 内容-验收对照",
                    verdict?.Passed ?? false,
                    verdict?.Reason ?? $"LLM 输出不可解析: {Truncate(completion.Content, 120)}"));
            }
            else
            {
                checks.Add(new VerificationCheck(
                    "LLM 内容-验收对照", false, "[DEGRADED] LLM 调用失败，该项按未通过处理（不虚报）"));
            }
        }

        var passed = checks.TrueForAll(c => c.Passed);
        var failedCount = checks.Count - checks.FindAll(c => c.Passed).Count;
        return new VerificationResult
        {
            Passed = passed,
            Checks = checks,
            Summary = passed
                ? $"全部 {checks.Count} 项检查通过。"
                : $"{checks.Count} 项检查中 {failedCount} 项未通过。",
        };
    }

    internal record VerdictResult(bool Passed, string Reason);

    internal static VerdictResult? TryParseVerdict(string llmOutput)
    {
        var start = llmOutput.IndexOf('{');
        var end = llmOutput.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        try
        {
            using var doc = JsonDocument.Parse(llmOutput[start..(end + 1)]);
            if (!doc.RootElement.TryGetProperty("passed", out var p)) return null;
            var passed = p.ValueKind == JsonValueKind.True;
            var reason = doc.RootElement.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "";
            return new VerdictResult(passed, reason);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<MissionRecord> FinishAsync(
        MissionRecord record, List<MissionTransition> transitions, MissionExecutionOutcome? outcome,
        VerificationResult? verification, CancellationToken ct)
    {
        try
        {
            var retro = _retrospective.Evaluate(record, outcome);
            record.RetrospectiveJson = JsonSerializer.Serialize(retro, JsonOpts);
            await AdvanceAsync(record, MissionState.Retrospective, transitions, retro.Summary, ct);

            var mdPath = _retrospective.WriteMarkdown(_paths, retro, record);
            var lessons = _retrospective.BuildLessons(retro);
            var written = await _store.AddLessonsAsync(lessons, ct);
            await AdvanceAsync(record, MissionState.ExperienceWritten, transitions,
                $"复盘 md: {mdPath}；经验 {written} 条已入库", ct);

            // 终局契约：取消→Cancelled；执行成功且校验通过→Succeeded；其余→Failed（原因如实）。
            var verified = verification?.Passed ?? false;
            record.Outcome = outcome is { Cancelled: true }
                ? MissionOutcome.Cancelled
                : outcome is { Succeeded: true } && verified
                    ? MissionOutcome.Succeeded
                    : MissionOutcome.Failed;
            record.Error = outcome is { Succeeded: false }
                ? outcome.Error
                : outcome is { Succeeded: true } && !verified
                    ? $"校验未通过: {verification?.Summary ?? "无校验结果"}"
                    : null;
            return await _store.UpsertMissionAsync(record, ct);
        }
        catch (OperationCanceledException)
        {
            record.Outcome = MissionOutcome.Cancelled;
            record.TransitionsJson = JsonSerializer.Serialize(transitions, JsonOpts);
            return await _store.UpsertMissionAsync(record, CancellationToken.None);
        }
    }

    private async Task<MissionRecord> FailAsync(
        MissionRecord record, List<MissionTransition> transitions, string error,
        MissionExecutionOutcome? retroOutcome, CancellationToken ct)
    {
        _logger.LogWarning("任务 {Id} 失败: {Error}", record.Id, error);
        record.Error = error;
        record.Outcome = MissionOutcome.Failed;

        // 失败也必达复盘（能评多少评多少）。状态推进同样走 AdvanceAsync 留痕，
        // 不直接赋值 State——直接赋值会让迁移轨迹断链（审计已修复项）。
        try
        {
            var retro = _retrospective.Evaluate(record, retroOutcome);
            record.RetrospectiveJson = JsonSerializer.Serialize(retro, JsonOpts);
            await AdvanceAsync(record, MissionState.Retrospective, transitions, $"失败复盘: {retro.Summary}", ct);
            var lessons = _retrospective.BuildLessons(retro);
            var written = await _store.AddLessonsAsync(lessons, ct);
            await AdvanceAsync(record, MissionState.ExperienceWritten, transitions,
                $"失败路径经验 {written} 条已入库", ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("失败路径复盘亦失败（如实记录）: {Error}", ex.Message);
        }

        record.TransitionsJson = JsonSerializer.Serialize(transitions, JsonOpts);
        // 终兜底落库不可取消：失败记录本身不能丢（与 CancelAsync 对称）。
        return await _store.UpsertMissionAsync(record, CancellationToken.None);
    }

    private async Task<MissionRecord> CancelAsync(MissionRecord record, List<MissionTransition> transitions, CancellationToken ct)
    {
        record.Outcome = MissionOutcome.Cancelled;
        record.Error = "任务被取消";
        transitions.Add(new MissionTransition(record.State, record.State, DateTime.UtcNow, "取消"));
        record.TransitionsJson = JsonSerializer.Serialize(transitions, JsonOpts);
        var saved = await _store.UpsertMissionAsync(record, CancellationToken.None);
        // 实时事件流：取消路径的自迁移留痕同样外发（与轨迹一一对应）。
        FireMissionEvent(MissionEventKind.StateTransition, record.State, "取消");
        return saved;
    }

    private async Task AdvanceAsync(
        MissionRecord record, MissionState to, List<MissionTransition> transitions, string artifact, CancellationToken ct)
    {
        var from = record.State;
        ValidateTransition(from, to, transitions);
        record.State = to;
        transitions.Add(new MissionTransition(from, to, DateTime.UtcNow, artifact));
        record.TransitionsJson = JsonSerializer.Serialize(transitions, JsonOpts);
        await _store.UpsertMissionAsync(record, ct);
        // 实时事件流：状态机转换（阶段进入/离开）——落库成功后才发，事件与留痕轨迹一一对应。
        FireMissionEvent(MissionEventKind.StateTransition, to, artifact);
    }

    /// <summary>
    /// 状态迁移纪律守卫：只允许前进（含失败路径的阶段跳跃）或留痕自迁移（from==to），
    /// 不允许回退；且 from 必须与轨迹末站首尾相接——任何绕过 AdvanceAsync 直接赋值
    /// State 的行为都会在此断链并立即暴露。
    /// </summary>
    private static void ValidateTransition(MissionState from, MissionState to, List<MissionTransition> transitions)
    {
        if (to < from)
        {
            throw new InvalidOperationException(
                $"非法状态回退: {from} → {to}（状态机只允许前进或留痕自迁移）");
        }

        if (transitions.Count > 0 && transitions[^1].To != from)
        {
            throw new InvalidOperationException(
                $"状态链断裂: 轨迹末站 {transitions[^1].To} 与当前状态 {from} 不一致（疑似绕过 AdvanceAsync 直接赋值 State）");
        }
    }

    /// <summary>
    /// 实时事件流发射点：逐订阅者 try/catch 吞异常（含 null 短路零开销）——
    /// 任何订阅方故障绝不阻断 mission 本身（与 MissionLifetimeHook 同一契约）。
    /// </summary>
    private void FireMissionEvent(MissionEventKind kind, MissionState? state, string detail)
    {
        var handler = MissionEventRaised;
        if (handler is null)
        {
            return;
        }

        var evt = new MissionEvent(kind, state, detail, DateTime.UtcNow);
        foreach (var del in handler.GetInvocationList())
        {
            try
            {
                ((EventHandler<MissionEvent>)del).Invoke(this, evt);
            }
            catch
            {
                // 契约：订阅方必须不抛；即便抛了也绝不阻断 mission。
            }
        }
    }

    /// <summary>
    /// S-LOW-5：凭据全量 id 不进日志/事件——截断为前 8 字符 + 省略号（可关联、不可复原）。
    /// 真实升级凭据形如 esc-{guid:N}（36 字符），截断后仅保留前缀，无法凭日志重放审批。
    /// </summary>
    internal static string MaskCredentialId(string? id)
        => string.IsNullOrEmpty(id) ? "(空凭据)" : (id!.Length <= 8 ? id + "…" : id[..8] + "…");

    private static string Truncate(string? s, int max)
        => string.IsNullOrEmpty(s) ? string.Empty : (s.Length <= max ? s : s[..max]);
}

/// <summary>
/// checkpoint 恢复结果（C-RESUME，R1 缝合 #13）：成败都在结果里，不抛出。
/// RestoredFiles 为实际恢复的文件数；PlannedActions 为计划动作总数
/// （含如实跳过的未捕获超大文件项）。
/// </summary>
public sealed record MissionResumeResult(
    bool Succeeded,
    long? CheckpointSeq,
    int RestoredFiles,
    int PlannedActions,
    string? Error,
    string? Summary)
{
    /// <summary>诚实失败结果（恢复未发生或未完成；Error 人类可读，不含敏感信息）。</summary>
    public static MissionResumeResult Failed(string error) => new(false, null, 0, 0, error, error);
}

/// <summary>实时任务事件种类（MissionController.MissionEventRaised 的载荷分类）。</summary>
public enum MissionEventKind
{
    /// <summary>状态机转换（阶段进入/离开）：与 TransitionsJson 留痕一一对应。</summary>
    StateTransition,

    /// <summary>步骤进展：计划步数等真实可观测事实（不伪造逐步进展）。</summary>
    StepProgress,

    /// <summary>升级产生：偏离升级受理待人工审批（Detail 只含截断凭据 id）。</summary>
    EscalationRaised,
}

/// <summary>
/// 实时任务事件（纯数据 record）：<see cref="Kind"/> 分类、<see cref="State"/> 关联状态
///（升级事件来自工具循环、无状态机上下文时为 null）、<see cref="Detail"/> 人类可读摘要
///（不含全量凭据 id）、<see cref="TimestampUtc"/> 发射时刻。
/// </summary>
public sealed record MissionEvent(
    MissionEventKind Kind,
    MissionState? State,
    string Detail,
    DateTime TimestampUtc);
