using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AeroAgent.Autonomy.Analysis;
using AeroAgent.Autonomy.Data;
using AeroAgent.Autonomy.Mission;
using AeroAgent.Autonomy.Retrospective;
using AeroAgent.Conversation.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AeroAgent.Autonomy.Learning;

/// <summary>任务结束学习钩子的运行选项。</summary>
public sealed class MissionLearningOptions
{
    /// <summary>触发 L3 参数自调优所需的最少 held-out 标注样本数（不足则如实跳过并记 [DEGRADED]）。</summary>
    public int MinSamplesForTuning { get; init; } = 3;

    /// <summary>构造 held-out 集时扫描的历史任务上限。</summary>
    public int MaxHistoryScan { get; init; } = 50;

    /// <summary>lessons 同步扫描上限。</summary>
    public int MaxLessonsSync { get; init; } = 200;

    /// <summary>L3 一轮的 gate 选项（准确率下限等）。</summary>
    public RsiRoundOptions RsiOptions { get; init; } = new();
}

/// <summary>任务结束学习钩子的完整结果（每一步都如实计数）。</summary>
public sealed record MissionLearningResult
{
    /// <summary>目标任务 Id。</summary>
    public required string MissionId { get; init; }

    /// <summary>目标任务是否存在（不存在时其余字段均为空值语义）。</summary>
    public required bool MissionFound { get; init; }

    /// <summary>lessons→经验存储同步统计（未找到任务时为 null）。</summary>
    public LessonSyncResult? LessonSync { get; init; }

    /// <summary>是否写入了该任务的轨迹经验（幂等：重复调用为 false）。</summary>
    public bool TrajectoryWritten { get; init; }

    /// <summary>L1 生成的修正规则条数。</summary>
    public int CorrectionRulesRecorded { get; init; }

    /// <summary>L2 沉淀的 methods 经验条数。</summary>
    public int MethodsPromoted { get; init; }

    /// <summary>L3 自调优轮次结果（样本不足未触发时为 null）。</summary>
    public RsiRoundResult? RsiRound { get; init; }

    /// <summary>补充说明（跳过原因等，如实记录）。</summary>
    public string? Note { get; init; }
}

/// <summary>
/// 任务结束学习钩子（P6-T3 与 MissionController 的闭环接口，供 P8 组合根接线；
/// 本阶段不修改 MissionController，钩子独立可测）。任务终态后调用一次：
/// <list type="number">
/// <item>复盘 lessons → <see cref="ExperienceBridge"/> 分类同步进 <see cref="ExperienceStore"/>（Pending）；</item>
/// <item>写一条该任务的轨迹经验（三分存储的轨迹通道，幂等）；</item>
/// <item>有复盘缺口时跑组合档（RSI L1 修正规则 → L2 methods 沉淀）；</item>
/// <item>从历史任务真实构造 held-out 标注样本集，样本足够时触发 RSI L3 一轮自调优
/// （过 gate 生效 + 快照可回退 / 不过 gate 保持原参数，全部由 <see cref="RsiEngine"/> 保证）。</item>
/// </list>
/// </summary>
public sealed class MissionLearningHook
{
    private readonly MissionStore _missions;
    private readonly ExperienceBridge _bridge;
    private readonly RsiEngine _rsi;
    private readonly ILogger<MissionLearningHook> _logger;

    public MissionLearningHook(
        MissionStore missions,
        ExperienceBridge bridge,
        RsiEngine rsi,
        ILogger<MissionLearningHook>? logger = null)
    {
        _missions = missions ?? throw new ArgumentNullException(nameof(missions));
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        _rsi = rsi ?? throw new ArgumentNullException(nameof(rsi));
        _logger = logger ?? NullLogger<MissionLearningHook>.Instance;
    }

    /// <summary>
    /// 任务结束后的学习闭环。任务不存在时如实返回 MissionFound=false（不虚构学习成果）。
    /// </summary>
    public async Task<MissionLearningResult> OnMissionCompletedAsync(
        string missionId, MissionLearningOptions? options = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(missionId))
        {
            throw new ArgumentException("missionId 不能为空。", nameof(missionId));
        }

        options ??= new MissionLearningOptions();

        var record = await _missions.GetMissionAsync(missionId, ct);
        if (record is null)
        {
            _logger.LogWarning("学习钩子收到不存在的任务 Id {MissionId}，如实返回未找到。", missionId);
            return new MissionLearningResult { MissionId = missionId, MissionFound = false, Note = "任务不存在" };
        }

        // 1) 复盘 lessons → 三分经验存储（Pending，下次会话生效）。
        var sync = await _bridge.SyncLessonsAsync(options.MaxLessonsSync, ct);

        // 2) 该任务的轨迹经验（幂等：同一任务只写一条）。
        var trajectory = await _bridge.Experiences.AddAsync(
            ExperienceKind.Trajectory,
            $"任务轨迹 {Truncate(record.Id, 12)}（{record.Outcome}）",
            BuildTrajectoryContent(record),
            sourceKey: $"trajectory:{record.Id}",
            sourceMissionId: record.Id,
            sourcePhase: record.State.ToString(),
            tags: new[] { record.Strategy ?? "unknown-strategy", record.Outcome.ToString() },
            ct: ct);

        // 3) 有复盘缺口 → 组合档（L1 修正规则 + L2 methods 沉淀）常开。
        var rulesRecorded = 0;
        var methodsPromoted = 0;
        var retro = TryParseRetrospective(record.RetrospectiveJson);
        if (retro is { Gaps.Count: > 0 })
        {
            var composite = await _rsi.RunCompositeTierAsync(record.Id, retro.Gaps, ct);
            rulesRecorded = composite.CorrectionRulesRecorded;
            methodsPromoted = composite.MethodsPromoted;
        }

        // 4) 历史任务真实构造 held-out 样本 → 样本足够才触发 L3 一轮自调优。
        var samples = await BuildHeldOutSamplesAsync(options.MaxHistoryScan, ct);
        RsiRoundResult? round = null;
        string? note = null;
        if (samples.Count >= Math.Max(1, options.MinSamplesForTuning))
        {
            round = await _rsi.RunRoundAsync(samples, options.RsiOptions, ct);
        }
        else
        {
            note = $"历史标注样本 {samples.Count} 条 < {options.MinSamplesForTuning}，本轮跳过 L3 参数自调优";
            _logger.LogWarning("[DEGRADED] {Note}（仅完成经验同步与组合档）。", note);
        }

        return new MissionLearningResult
        {
            MissionId = missionId,
            MissionFound = true,
            LessonSync = sync,
            TrajectoryWritten = trajectory.CreatedNew,
            CorrectionRulesRecorded = rulesRecorded,
            MethodsPromoted = methodsPromoted,
            RsiRound = round,
            Note = note,
        };
    }

    /// <summary>
    /// 从历史任务真实构造 held-out 标注样本集（只取可证明的标签）：
    /// 成功任务的所用策略 = 策略维度的真实标签；澄清维度只在证据明确时标注
    /// （成功且无未答澄清 → 有帮助；失败且澄清未答 → 无帮助；其余证据不足不标注）。
    /// </summary>
    public async Task<IReadOnlyList<RsiHeldOutSample>> BuildHeldOutSamplesAsync(
        int maxHistoryScan, CancellationToken ct = default)
    {
        var missions = await _missions.ListMissionsAsync(Math.Max(1, maxHistoryScan), ct);
        var samples = new List<RsiHeldOutSample>();

        foreach (var mission in missions)
        {
            if (mission.Outcome != MissionOutcome.Succeeded && mission.Outcome != MissionOutcome.Failed)
            {
                continue; // 非终局（Pending/Cancelled）任务没有可证明的成败标签。
            }

            var analysis = TryParseAnalysis(mission.AnalysisJson);
            if (analysis is null)
            {
                continue; // 无分析产物 → 类型/复杂度特征缺失，不能构造样本。
            }

            OrchestrationStrategy? strategyLabel = null;
            if (mission.Outcome == MissionOutcome.Succeeded
                && Enum.TryParse<OrchestrationStrategy>(mission.Strategy, out var strategy))
            {
                strategyLabel = strategy; // 成功任务所用策略 = 被证明成功的策略。
            }

            var clarification = TryParseClarification(mission.ClarificationJson);
            bool? clarifyLabel = clarification switch
            {
                { Requires: false } when mission.Outcome == MissionOutcome.Succeeded => true,
                { Requires: true, Unanswered: 0 } when mission.Outcome == MissionOutcome.Succeeded => true,
                { Requires: true, Unanswered: > 0 } when mission.Outcome == MissionOutcome.Failed => false,
                _ => null,
            };

            if (strategyLabel is null && clarifyLabel is null)
            {
                continue; // 两个维度都没有可证明的标签 → 不作为样本（不虚构标签）。
            }

            samples.Add(new RsiHeldOutSample
            {
                Type = analysis.Type,
                Complexity = analysis.Complexity,
                AmbiguityScore = clarification?.Ambiguity ?? 0.0,
                SuccessfulStrategy = strategyLabel,
                ClarificationHelped = clarifyLabel,
                Provenance = $"mission {mission.Id}（{mission.Outcome}）",
            });
        }

        return samples;
    }

    // ============ 内部实现 ============

    private static string BuildTrajectoryContent(MissionRecord record)
    {
        var content =
            $"任务: {Truncate(record.TaskText, 120)}" + Environment.NewLine +
            $"策略: {record.Strategy ?? "-"}；终局: {record.Outcome}；状态: {record.State}";
        if (!string.IsNullOrWhiteSpace(record.Error))
        {
            content += Environment.NewLine + $"失败原因: {Truncate(record.Error, 200)}";
        }

        return content;
    }

    private static RetrospectiveRecord? TryParseRetrospective(string? retrospectiveJson)
    {
        if (string.IsNullOrWhiteSpace(retrospectiveJson))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<RetrospectiveRecord>(retrospectiveJson);
        }
        catch (JsonException)
        {
            return null; // 结构不符时如实当作无复盘（不虚构缺口）。
        }
    }

    private static TaskAnalysis? TryParseAnalysis(string? analysisJson)
    {
        if (string.IsNullOrWhiteSpace(analysisJson))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<TaskAnalysis>(analysisJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static (double Ambiguity, bool Requires, int Unanswered)? TryParseClarification(string? clarificationJson)
    {
        if (string.IsNullOrWhiteSpace(clarificationJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(clarificationJson);
            var root = doc.RootElement;
            var ambiguity = root.TryGetProperty("AmbiguityScore", out var a) && a.ValueKind == JsonValueKind.Number
                ? a.GetDouble()
                : 0.0;
            var requires = root.TryGetProperty("RequiresClarification", out var r)
                && r.ValueKind is JsonValueKind.True or JsonValueKind.False
                && r.GetBoolean();
            var unanswered = root.TryGetProperty("UnansweredCount", out var u) && u.ValueKind == JsonValueKind.Number
                ? u.GetInt32()
                : 0;
            return (ambiguity, requires, unanswered);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Truncate(string? s, int max)
        => string.IsNullOrEmpty(s) ? string.Empty : (s.Length <= max ? s : s[..max] + "…");
}
