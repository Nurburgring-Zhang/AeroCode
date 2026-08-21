using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AeroAgent.Autonomy.Analysis;
using AeroAgent.Autonomy.Retrospective;
using AeroAgent.Conversation.Models;
using AeroCode.Skills.AutoCreate;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AeroAgent.Autonomy.Learning;

/// <summary>
/// RSI 递归自我改进引擎（P6-T3 / G11 的真实实现，L1-L3 三层 + 安全档）：
/// <list type="bullet">
/// <item>L1 输出修正：失败任务的复盘缺口 → 真实生成针对性修正规则（correction_rules 落库）；</item>
/// <item>L2 记忆积累：未沉淀的修正规则 → methods 经验（经 <see cref="ExperienceStore"/>，Pending 语义）；</item>
/// <item>L3 参数自调优：对 <see cref="RsiParameterSet"/>（StrategySelector 的 Decompose 阈值 /
/// ClarificationGate 的歧义阈值）生成变异候选（±0.05/±0.1 等）→ 在 held-out 标注样本集上真实评估 →
/// 过 gate（准确率不低于当前且不低于设定下限）才应用新参数并快照旧参数（可回退）；
/// 不过 gate 一律回退候选并保持原参数，原因写入 rsi-log.md——禁止"直接应用不验证"的假改进；</item>
/// <item>安全档：组合档（L1+L2）常开；创造档（经 SkillCreator 生成新技能）必须经
/// <see cref="ISkillApproval"/> 批准，默认 <see cref="DenyAllSkillApproval"/> 不批准。</item>
/// </list>
/// 全程留痕：每轮变异/评估/决策、每次回退、每次创造档审批都追加进 rsi-log.md；
/// 参数快照与当前参数真实落盘 JSON。
/// </summary>
public sealed class RsiEngine : IDisposable
{
    /// <summary>准确率比较容差（浮点相等判定）。</summary>
    public const double AccuracyEpsilon = 1e-9;

    /// <summary>澄清阈值合法区间（变异钳制）。</summary>
    public const double ClarificationThresholdMin = 0.05;

    /// <summary>澄清阈值合法区间（变异钳制）。</summary>
    public const double ClarificationThresholdMax = 0.95;

    /// <summary>Decompose 复杂度阈值合法区间（变异钳制；复杂度评分域为 1-5）。</summary>
    public const double DecomposeThresholdMin = 1.0;

    /// <summary>Decompose 复杂度阈值合法区间（变异钳制）。</summary>
    public const double DecomposeThresholdMax = 5.0;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    /// <summary>澄清阈值变异步长（任务规格：±0.05 / ±0.1）。</summary>
    private static readonly double[] ClarificationDeltas = { -0.1, -0.05, 0.05, 0.1 };

    /// <summary>Decompose 阈值变异步长（按复杂度 1-5 域等比放大的 ±0.5 / ±1.0）。</summary>
    private static readonly double[] DecomposeDeltas = { -1.0, -0.5, 0.5, 1.0 };

    private readonly LearningDbContext _db;
    private readonly LearningDataPaths _paths;
    private readonly ExperienceStore _experiences;
    private readonly SkillCreator? _skillCreator;
    private readonly ILogger<RsiEngine> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <param name="db">学习库上下文（correction_rules 表；该实例归本组件独占）。</param>
    /// <param name="paths">学习数据路径（rsi-log / 快照 / 当前参数文件）。</param>
    /// <param name="experiences">经验存储（L2 沉淀目标）。</param>
    /// <param name="skillCreator">技能创建器（创造档；null = 创造档不可用，如实拒绝）。</param>
    /// <param name="logger">日志；null 时用空日志。</param>
    public RsiEngine(
        LearningDbContext db,
        LearningDataPaths paths,
        ExperienceStore experiences,
        SkillCreator? skillCreator = null,
        ILogger<RsiEngine>? logger = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _experiences = experiences ?? throw new ArgumentNullException(nameof(experiences));
        _skillCreator = skillCreator;
        _logger = logger ?? NullLogger<RsiEngine>.Instance;
    }

    public void Dispose() => _gate.Dispose();

    // ============ 并发控制 ============

    private async Task<T> WithGateAsync<T>(Func<Task<T>> operation)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await operation().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task WithGateAsync(Func<Task> operation)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await operation().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    // ============ L3：参数自调优 ============

    /// <summary>读取当前生效参数（rsi-params-current.json；不存在时返回默认参数，不虚构历史）。</summary>
    public async Task<RsiParameterSet> LoadActiveParametersAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_paths.ActiveParameterFile))
        {
            return new RsiParameterSet();
        }

        try
        {
            var json = await File.ReadAllTextAsync(_paths.ActiveParameterFile, ct);
            var loaded = JsonSerializer.Deserialize<RsiParameterSet>(json);
            return loaded ?? new RsiParameterSet();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _logger.LogWarning("[DEGRADED] 当前参数文件不可读（{Error}），本轮按默认参数运行。", ex.Message);
            return new RsiParameterSet();
        }
    }

    /// <summary>
    /// 在 held-out 样本集上真实评估一个参数集的准确率。
    /// 每个样本最多贡献两个标注维度（策略选择 + 澄清决策），只统计有标注的维度：
    /// accuracy = 预测正确维度数 / 有标注维度总数。无任何标注时抛 <see cref="ArgumentException"/>（拒绝空评估）。
    /// </summary>
    public double EvaluateParameters(RsiParameterSet parameters, IReadOnlyList<RsiHeldOutSample> samples)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count == 0)
        {
            throw new ArgumentException("held-out 样本集为空，无法评估。", nameof(samples));
        }

        var labeled = 0;
        var correct = 0;
        foreach (var sample in samples)
        {
            if (sample.SuccessfulStrategy is { } strategyLabel)
            {
                labeled++;
                if (DecideStrategy(parameters, sample.Type, sample.Complexity) == strategyLabel)
                {
                    correct++;
                }
            }

            if (sample.ClarificationHelped is { } clarifyLabel)
            {
                labeled++;
                if (PredictClarificationTriggered(parameters, sample.AmbiguityScore) == clarifyLabel)
                {
                    correct++;
                }
            }
        }

        if (labeled == 0)
        {
            throw new ArgumentException("held-out 样本集没有任何有效标注（策略/澄清维度均为空），评估无意义。", nameof(samples));
        }

        return (double)correct / labeled;
    }

    /// <summary>
    /// 按参数集决策编排策略——StrategySelector 规则的可调阈值版本（规则结构逐条对应）：
    /// Composite→Decompose；复杂度 ≥ 阈值→Decompose；Creative→Ensemble；Research→Router；
    /// Analysis→Pipeline；其余→Single。
    /// </summary>
    public static OrchestrationStrategy DecideStrategy(RsiParameterSet parameters, TaskType type, int complexity)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (type == TaskType.Composite)
        {
            return OrchestrationStrategy.Decompose;
        }

        if (complexity >= parameters.DecomposeComplexityThreshold)
        {
            return OrchestrationStrategy.Decompose;
        }

        return type switch
        {
            TaskType.Creative => OrchestrationStrategy.Ensemble,
            TaskType.Research => OrchestrationStrategy.Router,
            TaskType.Analysis => OrchestrationStrategy.Pipeline,
            _ => OrchestrationStrategy.Single,
        };
    }

    /// <summary>按参数集预测澄清门是否触发（歧义度 ≥ 阈值）。</summary>
    public static bool PredictClarificationTriggered(RsiParameterSet parameters, double ambiguityScore)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return ambiguityScore >= parameters.ClarificationThreshold;
    }

    /// <summary>
    /// 生成单维变异候选：澄清阈值 ±0.05/±0.1，Decompose 阈值 ±0.5/±1.0（按 1-5 域放大），
    /// 全部钳制到合法区间；钳制后与当前参数无差异的候选如实剔除。
    /// </summary>
    public static IReadOnlyList<RsiParameterSet> GenerateCandidates(RsiParameterSet current)
    {
        ArgumentNullException.ThrowIfNull(current);
        var candidates = new List<RsiParameterSet>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // 候选必须是恒合法的参数集：未参与变异的"另一维"若基线越界，同样钳制。
        var clarifCarried = Clamp(current.ClarificationThreshold, ClarificationThresholdMin, ClarificationThresholdMax);
        var decomposeCarried = Clamp(current.DecomposeComplexityThreshold, DecomposeThresholdMin, DecomposeThresholdMax);

        foreach (var delta in ClarificationDeltas)
        {
            var value = Clamp(Math.Round(current.ClarificationThreshold + delta, 4),
                ClarificationThresholdMin, ClarificationThresholdMax);
            if (Math.Abs(value - current.ClarificationThreshold) > AccuracyEpsilon)
            {
                var key = $"c{value.ToString("0.####", CultureInfo.InvariantCulture)}";
                if (seen.Add(key)) // 钳制后可能与其他步长收敛到同值，去重不留冗余候选。
                {
                    candidates.Add(current with
                    {
                        ClarificationThreshold = value,
                        DecomposeComplexityThreshold = decomposeCarried,
                        Label = $"clarification{delta.ToString("+0.##;-0.##", CultureInfo.InvariantCulture)}",
                    });
                }
            }
        }

        foreach (var delta in DecomposeDeltas)
        {
            var value = Clamp(Math.Round(current.DecomposeComplexityThreshold + delta, 4),
                DecomposeThresholdMin, DecomposeThresholdMax);
            if (Math.Abs(value - current.DecomposeComplexityThreshold) > AccuracyEpsilon)
            {
                var key = $"d{value.ToString("0.####", CultureInfo.InvariantCulture)}";
                if (seen.Add(key))
                {
                    candidates.Add(current with
                    {
                        DecomposeComplexityThreshold = value,
                        ClarificationThreshold = clarifCarried,
                        Label = $"decompose{delta.ToString("+0.#;-0.#", CultureInfo.InvariantCulture)}",
                    });
                }
            }
        }

        return candidates;
    }

    /// <summary>
    /// 运行一轮 L3 自调优（真实闭环）：读基线 → 评估基线 → 生成变异候选 → 逐个在
    /// held-out 集上真实评估 → 过 gate（准确率不低于基线且不低于下限）的最优候选
    /// 先快照旧参数再应用；无候选过 gate 则保持原参数并把逐候选原因写入 rsi-log.md。
    /// </summary>
    public async Task<RsiRoundResult> RunRoundAsync(
        IReadOnlyList<RsiHeldOutSample> heldOutSamples,
        RsiRoundOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(heldOutSamples);
        options ??= new RsiRoundOptions();
        var roundId = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);

        var baseline = await LoadActiveParametersAsync(ct);
        var baselineAccuracy = EvaluateParameters(baseline, heldOutSamples);

        var candidateResults = new List<RsiCandidateResult>();
        foreach (var candidate in GenerateCandidates(baseline))
        {
            ct.ThrowIfCancellationRequested();
            var accuracy = EvaluateParameters(candidate, heldOutSamples);
            var beatsBaseline = options.RequireStrictImprovement
                ? accuracy > baselineAccuracy + AccuracyEpsilon
                : accuracy >= baselineAccuracy - AccuracyEpsilon;
            var passed = beatsBaseline && accuracy >= options.MinAccuracyFloor - AccuracyEpsilon;

            var note = passed
                ? $"准确率 {accuracy:P1} ≥ 基线 {baselineAccuracy:P1} 且 ≥ 下限 {options.MinAccuracyFloor:P1} → 过 gate"
                : beatsBaseline
                    ? $"准确率 {accuracy:P1} 不低于基线但低于下限 {options.MinAccuracyFloor:P1} → 拒绝"
                    : $"准确率 {accuracy:P1} 低于基线 {baselineAccuracy:P1} → 拒绝";
            candidateResults.Add(new RsiCandidateResult(candidate, accuracy, passed, note));
        }

        var best = candidateResults
            .Where(c => c.PassedGate)
            .OrderByDescending(c => c.Accuracy)
            .ThenBy(c => ParameterDistance(baseline, c.Parameters))
            .ThenBy(c => c.Parameters.Label, StringComparer.Ordinal)
            .FirstOrDefault();

        var logPath = _paths.RsiLogFile;
        if (best is null)
        {
            var reason = candidateResults.Count == 0
                ? "无可用变异候选（全部步长钳制后与当前参数相同），保持原参数。"
                : $"全部 {candidateResults.Count} 个候选均未过 gate（见逐候选留痕），保持原参数。";
            await AppendRsiLogAsync(BuildRoundLog(roundId, heldOutSamples.Count, baseline, baselineAccuracy,
                candidateResults, applied: null, snapshotPath: null, reason, options), ct);
            _logger.LogInformation("RSI 轮次 {Round} 决策：拒绝全部候选，参数保持不变。{Reason}", roundId, reason);
            return new RsiRoundResult
            {
                RoundId = roundId,
                Baseline = baseline,
                BaselineAccuracy = baselineAccuracy,
                Candidates = candidateResults,
                Applied = false,
                Reason = reason,
                LogPath = logPath,
            };
        }

        // 过 gate：先快照旧参数（回退的来源），再应用新参数。
        var snapshotPath = await WriteSnapshotAsync(baseline, roundId,
            $"应用候选 {best.Parameters.Label} 前的旧参数快照", ct);
        await PersistActiveParametersAsync(best.Parameters, ct);

        var appliedReason = $"候选 {best.Parameters.Label} 过 gate（准确率 {best.Accuracy:P1}），已应用；旧参数已快照至 {Path.GetFileName(snapshotPath)}。";
        await AppendRsiLogAsync(BuildRoundLog(roundId, heldOutSamples.Count, baseline, baselineAccuracy,
            candidateResults, applied: best, snapshotPath, appliedReason, options), ct);
        _logger.LogInformation("RSI 轮次 {Round} 决策：{Reason}", roundId, appliedReason);

        return new RsiRoundResult
        {
            RoundId = roundId,
            Baseline = baseline,
            BaselineAccuracy = baselineAccuracy,
            Candidates = candidateResults,
            Applied = true,
            AppliedCandidate = best,
            SnapshotPath = snapshotPath,
            Reason = appliedReason,
            LogPath = logPath,
        };
    }

    /// <summary>
    /// 回退到最近一次参数快照（真实恢复：快照 JSON 读回并覆盖当前参数文件）。
    /// 无快照时如实失败（记 [DEGRADED]），绝不虚构"已回退"。
    /// </summary>
    public async Task<RsiRollbackResult> RollbackAsync(CancellationToken ct = default)
    {
        var snapshotPath = FindLatestSnapshotFile();
        if (snapshotPath is null)
        {
            _logger.LogWarning("[DEGRADED] RSI 回退请求无可用参数快照，如实拒绝。");
            return new RsiRollbackResult(false, null, null, "没有任何参数快照（从未应用过新参数）");
        }

        try
        {
            var json = await File.ReadAllTextAsync(snapshotPath, ct);
            var snapshot = JsonSerializer.Deserialize<RsiSnapshotFile>(json);
            if (snapshot?.Parameters is null)
            {
                return new RsiRollbackResult(false, null, snapshotPath, $"快照 {snapshotPath} 内容无效（缺少 Parameters）");
            }

            await PersistActiveParametersAsync(snapshot.Parameters with { Label = $"rollback-from:{Path.GetFileName(snapshotPath)}" }, ct);
            await AppendRsiLogAsync(
                $"## RSI 回退 {DateTime.UtcNow:O}\n- 来源快照: {snapshotPath}\n- 恢复参数: {DescribeParameters(snapshot.Parameters)}\n", ct);
            _logger.LogInformation("RSI 已回退到快照 {Snapshot}。", snapshotPath);
            return new RsiRollbackResult(true, snapshot.Parameters, snapshotPath, "已从最近快照恢复旧参数");
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _logger.LogWarning("[DEGRADED] RSI 回退读取快照失败: {Error}", ex.Message);
            return new RsiRollbackResult(false, null, snapshotPath, $"快照读取失败: {ex.Message}");
        }
    }

    // ============ L1：输出修正 ============

    /// <summary>
    /// L1：对一个任务的复盘缺口真实生成修正规则并落库（每个缺口一条，内容来自缺口原文，
    /// 不编造）。重复调用同一任务会追加规则——调用方（钩子）按任务粒度只调一次。
    /// </summary>
    public Task<IReadOnlyList<CorrectionRule>> RecordCorrectionsAsync(
        string missionId, IReadOnlyList<GapItem> gaps, CancellationToken ct = default)
        => WithGateAsync(async () =>
        {
            if (string.IsNullOrWhiteSpace(missionId))
            {
                throw new ArgumentException("missionId 不能为空。", nameof(missionId));
            }

            ArgumentNullException.ThrowIfNull(gaps);
            if (gaps.Count == 0)
            {
                return Array.Empty<CorrectionRule>();
            }

            await _db.Database.EnsureCreatedAsync(ct);

            // 按任务幂等：同一任务的缺口只生成一次规则，重复调用如实返回空。
            if (await _db.CorrectionRules.AnyAsync(r => r.MissionId == missionId, ct))
            {
                return Array.Empty<CorrectionRule>();
            }

            var recorded = new List<CorrectionRule>();
            foreach (var gap in gaps)
            {
                if (string.IsNullOrWhiteSpace(gap.Description))
                {
                    continue; // 无缺口描述则无规则可生成（不编造）。
                }

                var ruleText = BuildCorrectionRule(gap);
                var entity = new CorrectionRuleEntity
                {
                    MissionId = missionId,
                    GapDescription = gap.Description.Trim(),
                    RuleText = ruleText,
                    Severity = gap.Severity,
                };
                _db.CorrectionRules.Add(entity);
                recorded.Add(Detach(entity));
            }

            if (recorded.Count > 0)
            {
                await _db.SaveChangesAsync(ct);
                await AppendRsiLogAsync(
                    $"## RSI L1 输出修正 {DateTime.UtcNow:O}\n- 任务: {missionId}\n- 生成修正规则 {recorded.Count} 条（来自复盘缺口原文）\n", ct);
            }

            return (IReadOnlyList<CorrectionRule>)recorded;
        });

    // ============ L2：记忆积累 ============

    /// <summary>
    /// L2：把尚未沉淀的修正规则提升为 methods 经验（经 <see cref="ExperienceStore"/>，
    /// 遵守 Pending 生效语义）。幂等：已沉淀规则不重复入库。返回本次沉淀条数。
    /// </summary>
    public Task<int> PromoteCorrectionsAsync(CancellationToken ct = default)
        => WithGateAsync(async () =>
        {
            await _db.Database.EnsureCreatedAsync(ct);
            var pendingRules = await _db.CorrectionRules
                .Where(r => !r.Promoted)
                .OrderBy(r => r.CreatedAtUtc)
                .ToListAsync(ct);
            if (pendingRules.Count == 0)
            {
                return 0;
            }

            var promoted = 0;
            foreach (var rule in pendingRules)
            {
                var addResult = await _experiences.AddAsync(
                    ExperienceKind.Method,
                    $"修正规则：{Truncate(rule.GapDescription, 60)}",
                    rule.RuleText,
                    sourceKey: $"correction:{rule.Id}",
                    sourceMissionId: rule.MissionId,
                    sourcePhase: "Retrospective",
                    tags: new[] { "rsi-l1", rule.Severity },
                    ct: ct);
                if (addResult.CreatedNew)
                {
                    rule.Promoted = true;
                    promoted++;
                }
                else
                {
                    rule.Promoted = true; // 经验已存在（来源键命中）：规则同样视为已沉淀，避免反复重试。
                }
            }

            await _db.SaveChangesAsync(ct);
            await AppendRsiLogAsync(
                $"## RSI L2 记忆积累 {DateTime.UtcNow:O}\n- 修正规则沉淀为 methods 经验 {promoted} 条（Pending，下次会话生效）\n", ct);
            return promoted;
        });

    /// <summary>组合档（L1+L2）常开入口：复盘缺口 → 修正规则 → methods 经验，一次完成。</summary>
    public async Task<RsiCompositeTierResult> RunCompositeTierAsync(
        string missionId, IReadOnlyList<GapItem> gaps, CancellationToken ct = default)
    {
        var rules = await RecordCorrectionsAsync(missionId, gaps, ct);
        var promoted = await PromoteCorrectionsAsync(ct);
        return new RsiCompositeTierResult(rules.Count, promoted);
    }

    // ============ 创造档：生成新技能（需批准） ============

    /// <summary>
    /// 创造档提案：把候选技能交给批准方；批准后经 <see cref="SkillCreator"/> 真实创建
    /// SKILL.md。默认批准方为 <see cref="DenyAllSkillApproval"/>（不批准）——
    /// 未获批准绝不创建，审批决定写入 rsi-log.md。
    /// </summary>
    public async Task<RsiSkillProposalResult> ProposeSkillAsync(
        AutoCreateCandidate candidate, ISkillApproval? approval = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var approver = approval ?? new DenyAllSkillApproval();

        if (_skillCreator is null)
        {
            await AppendRsiLogAsync(
                $"## RSI 创造档 {DateTime.UtcNow:O}\n- 提案: {candidate.SuggestedId}\n- 决定: 拒绝（未配置 SkillCreator，创造档不可用）\n", ct);
            return new RsiSkillProposalResult(false, false, null, "未配置 SkillCreator，创造档不可用");
        }

        var approved = await approver.ApproveAsync(candidate, ct);
        if (!approved)
        {
            await AppendRsiLogAsync(
                $"## RSI 创造档 {DateTime.UtcNow:O}\n- 提案: {candidate.SuggestedId}\n- 决定: 拒绝（批准方未批准；创造档默认不批准）\n", ct);
            _logger.LogInformation("RSI 创造档提案 {SkillId} 被批准方拒绝，未创建技能。", candidate.SuggestedId);
            return new RsiSkillProposalResult(false, false, null, "批准方未批准（创造档默认不批准）");
        }

        var skill = _skillCreator.TryCreate(candidate);
        if (skill is null)
        {
            await AppendRsiLogAsync(
                $"## RSI 创造档 {DateTime.UtcNow:O}\n- 提案: {candidate.SuggestedId}\n- 决定: 已批准但 SkillCreator 拒绝创建（触发条件未满足或同名技能已存在）\n", ct);
            return new RsiSkillProposalResult(true, false, null, "已批准，但 SkillCreator 拒绝创建（触发条件未满足或同名技能已存在）");
        }

        await AppendRsiLogAsync(
            $"## RSI 创造档 {DateTime.UtcNow:O}\n- 提案: {candidate.SuggestedId}\n- 决定: 批准并创建成功\n- 技能文件: {skill.SourcePath}\n", ct);
        _logger.LogInformation("RSI 创造档提案 {SkillId} 获批准并已创建: {Path}", candidate.SuggestedId, skill.SourcePath);
        return new RsiSkillProposalResult(true, true, skill, "批准方已批准，技能创建成功");
    }

    // ============ 内部实现 ============

    private static string BuildCorrectionRule(GapItem gap)
    {
        var suggestion = string.IsNullOrWhiteSpace(gap.Suggestion)
            ? "复盘未给出补全建议，执行前先人工确认该缺口的处置方式"
            : gap.Suggestion.Trim();
        return $"若再次出现缺口「{gap.Description.Trim()}」（严重度 {gap.Severity}）：{suggestion}";
    }

    private static double ParameterDistance(RsiParameterSet a, RsiParameterSet b) =>
        Math.Abs(a.ClarificationThreshold - b.ClarificationThreshold) +
        Math.Abs(a.DecomposeComplexityThreshold - b.DecomposeComplexityThreshold);

    private static double Clamp(double value, double min, double max) => Math.Clamp(value, min, max);

    private async Task PersistActiveParametersAsync(RsiParameterSet parameters, CancellationToken ct)
    {
        _paths.EnsureDirectories();
        var json = JsonSerializer.Serialize(parameters, JsonOpts);
        var tempFile = _paths.ActiveParameterFile + ".tmp";
        await File.WriteAllTextAsync(tempFile, json, ct);
        File.Move(tempFile, _paths.ActiveParameterFile, overwrite: true);
    }

    private async Task<string> WriteSnapshotAsync(
        RsiParameterSet parameters, string roundId, string reason, CancellationToken ct)
    {
        _paths.EnsureDirectories();
        var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
        var file = Path.Combine(_paths.ParameterSnapshotDirectory, $"rsi-snapshot-{stamp}-{roundId}.json");
        var snapshot = new RsiSnapshotFile(roundId, DateTime.UtcNow, reason, parameters);
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(snapshot, JsonOpts), ct);
        return file;
    }

    private string? FindLatestSnapshotFile()
    {
        if (!Directory.Exists(_paths.ParameterSnapshotDirectory))
        {
            return null;
        }

        var files = Directory.EnumerateFiles(_paths.ParameterSnapshotDirectory, "rsi-snapshot-*.json")
            .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal)
            .ToList();
        return files.Count == 0 ? null : files[^1];
    }

    private string BuildRoundLog(
        string roundId, int sampleCount, RsiParameterSet baseline, double baselineAccuracy,
        IReadOnlyList<RsiCandidateResult> candidates, RsiCandidateResult? applied,
        string? snapshotPath, string reason, RsiRoundOptions options)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"## RSI L3 自调优轮次 {roundId}（{DateTime.UtcNow:O}）");
        sb.AppendLine($"- held-out 样本数: {sampleCount}");
        sb.AppendLine($"- gate: 准确率 ≥ 基线{(options.RequireStrictImprovement ? "（严格优于）" : string.Empty)} 且 ≥ 下限 {options.MinAccuracyFloor:P1}");
        sb.AppendLine($"- 基线: {DescribeParameters(baseline)}；准确率 {baselineAccuracy:P2}");
        var index = 0;
        foreach (var candidate in candidates)
        {
            index++;
            sb.AppendLine($"- 候选 {index} [{candidate.Parameters.Label}]: {DescribeParameters(candidate.Parameters)}；" +
                          $"准确率 {candidate.Accuracy:P2}；{(candidate.PassedGate ? "PASS" : "FAIL")}（{candidate.Note}）");
        }

        if (candidates.Count == 0)
        {
            sb.AppendLine("- 候选: 无（变异步长钳制后与当前参数无差异）");
        }

        sb.AppendLine(applied is null
            ? $"- 决策: 拒绝（参数保持不变）。{reason}"
            : $"- 决策: 应用候选 [{applied.Parameters.Label}]（准确率 {applied.Accuracy:P2}）；旧参数快照: {snapshotPath}");
        sb.AppendLine();
        return sb.ToString();
    }

    private static string DescribeParameters(RsiParameterSet p) =>
        $"DecomposeThreshold={p.DecomposeComplexityThreshold.ToString("0.##", CultureInfo.InvariantCulture)}, " +
        $"ClarificationThreshold={p.ClarificationThreshold.ToString("0.####", CultureInfo.InvariantCulture)}";

    private async Task AppendRsiLogAsync(string block, CancellationToken ct)
    {
        try
        {
            _paths.EnsureDirectories();
            if (!File.Exists(_paths.RsiLogFile))
            {
                await File.WriteAllTextAsync(
                    _paths.RsiLogFile,
                    "# AeroCode RSI 留痕日志（rsi-log）\n\n> 每轮变异/评估/决策、回退、创造档审批的真实记录。\n\n", ct);
            }

            await File.AppendAllTextAsync(_paths.RsiLogFile, block, ct);
        }
        catch (IOException ex)
        {
            _logger.LogWarning("[DEGRADED] rsi-log.md 写入失败（决策本身不受影响，留痕缺失）: {Error}", ex.Message);
        }
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "…";

    private static CorrectionRule Detach(CorrectionRuleEntity e) => new()
    {
        Id = e.Id,
        MissionId = e.MissionId,
        GapDescription = e.GapDescription,
        RuleText = e.RuleText,
        Severity = e.Severity,
        Promoted = e.Promoted,
        CreatedAtUtc = e.CreatedAtUtc,
    };

    /// <summary>参数快照落盘结构（真实 JSON 文件内容）。</summary>
    public sealed record RsiSnapshotFile(
        string RoundId,
        DateTime CreatedAtUtc,
        string Reason,
        RsiParameterSet Parameters);
}
