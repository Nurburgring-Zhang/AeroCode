using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using AeroAgent.Autonomy.Data;

namespace AeroAgent.Autonomy.Retrospective;

/// <summary>单阶段复盘结论：是否达成 + 真实证据。</summary>
public sealed record PhaseReview(string Phase, bool Achieved, string Evidence);

/// <summary>一个缺口：描述 + 补全建议 + 严重度（info/warning/critical）。</summary>
public sealed record GapItem(string Description, string Suggestion, string Severity);

/// <summary>
/// 任务复盘记录：逐阶段对照计划与实际 + 缺口清单 + 补全建议。
/// 全部由 MissionRecord 真实字段推导，禁止空话。
/// </summary>
public sealed record RetrospectiveRecord
{
    /// <summary>来源任务 Id。</summary>
    public required string MissionId { get; init; }

    /// <summary>逐阶段评审。</summary>
    public IReadOnlyList<PhaseReview> PhaseReviews { get; init; } = Array.Empty<PhaseReview>();

    /// <summary>缺口清单（含补全建议）。</summary>
    public IReadOnlyList<GapItem> Gaps { get; init; } = Array.Empty<GapItem>();

    /// <summary>复盘结论摘要。</summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>生成时刻。</summary>
    public DateTime GeneratedAtUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// 复盘引擎：任务完成后（无论成败）逐阶段自检——分析/澄清/钢人/规划/执行/校验
/// 各阶段是否真实达成，产出缺口清单与补全建议，落盘 md 并生成经验条目。
/// 执行失败不跳过复盘：失败本身就是复盘的首要输入。
/// </summary>
public sealed class RetrospectiveEngine
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    /// <summary>
    /// 依据任务记录与执行结果生成复盘。所有结论来自 record 真实字段。
    /// </summary>
    public RetrospectiveRecord Evaluate(MissionRecord record, Mission.MissionExecutionOutcome? outcome)
    {
        ArgumentNullException.ThrowIfNull(record);

        var reviews = new List<PhaseReview>();
        var gaps = new List<GapItem>();

        // Analyzed
        var analyzed = !string.IsNullOrWhiteSpace(record.AnalysisJson);
        reviews.Add(new PhaseReview(
            "Analyzed", analyzed,
            analyzed ? "AnalysisJson 已落库（任务类型/复杂度/能力需求）" : "AnalysisJson 缺失"));
        if (!analyzed)
        {
            gaps.Add(new GapItem(
                "任务分析产物缺失，策略选择失去依据",
                "检查 TaskAnalyzer 依赖注入与 LLM 降级路径是否正常", "critical"));
        }

        // Clarification
        if (!string.IsNullOrWhiteSpace(record.ClarificationJson))
        {
            var unanswered = CountUnansweredClarifications(record.ClarificationJson);
            reviews.Add(new PhaseReview(
                "Clarification", true,
                unanswered > 0
                    ? $"澄清门已评估，{unanswered} 个问题未获应答（按假设继续，已如实记录）"
                    : "澄清门已评估且无遗留未答问题"));
            if (unanswered > 0)
            {
                gaps.Add(new GapItem(
                    $"{unanswered} 个澄清问题未获应答，执行基于假设推进",
                    "下次任务在交互通道可用时先完成澄清再执行", "warning"));
            }
        }
        else
        {
            reviews.Add(new PhaseReview("Clarification", false, "ClarificationJson 缺失"));
            gaps.Add(new GapItem(
                "澄清门未运行，歧义未被显式评估",
                "确认 ClarificationGate 在状态机中被调用", "warning"));
        }

        // Steelman
        if (!string.IsNullOrWhiteSpace(record.SteelmanJson))
        {
            var degraded = record.SteelmanJson.Contains("\"DegradedToAutoApprove\": true", StringComparison.Ordinal)
                || record.SteelmanJson.Contains("\"DegradedToAutoApprove\":true", StringComparison.Ordinal);
            reviews.Add(new PhaseReview(
                "Steelman", true,
                degraded ? "钢人论证完成（Interactive 无应答方，如实降级 AutoApprove）" : "钢人论证五要素完整"));
        }
        else
        {
            reviews.Add(new PhaseReview("Steelman", false, "SteelmanJson 缺失"));
            gaps.Add(new GapItem(
                "钢人论证未运行，执行前未做双向论证",
                "确认 SteelmanProtocol 在状态机中被调用", "warning"));
        }

        // Planning
        var planned = !string.IsNullOrWhiteSpace(record.PlanJson);
        reviews.Add(new PhaseReview(
            "Planning", planned,
            planned ? "PlanJson 已落库（含步骤与验收标准）" : "PlanJson 缺失"));
        if (!planned)
        {
            gaps.Add(new GapItem(
                "执行计划缺失，执行无既定步骤",
                "检查 Planning 阶段的 LLM/启发式计划生成", "critical"));
        }

        // Executing
        if (!string.IsNullOrWhiteSpace(record.ExecutionJson))
        {
            var executedOk = outcome?.Succeeded ?? false;
            reviews.Add(new PhaseReview(
                "Executing", executedOk,
                executedOk
                    ? $"执行成功（会话 {record.SessionId ?? "-"}，助手消息 {outcome?.AssistantMessages ?? 0} 条，成本 ${outcome?.TotalCostUsd:F4}）"
                    : $"执行失败：{outcome?.Error ?? record.Error ?? "未知错误"}"));
            if (!executedOk)
            {
                gaps.Add(new GapItem(
                    $"执行未成功：{outcome?.Error ?? record.Error ?? "未知错误"}",
                    "检查 provider 配置/网络与编排策略适配；必要时降级策略重试", "critical"));
            }
        }
        else
        {
            reviews.Add(new PhaseReview("Executing", false, "ExecutionJson 缺失（执行未发起）"));
            gaps.Add(new GapItem(
                "执行未发起",
                "检查 IMissionExecutor 注入与状态机推进逻辑", "critical"));
        }

        // Verifying
        if (!string.IsNullOrWhiteSpace(record.VerificationJson))
        {
            var passed = record.VerificationJson.Contains("\"Passed\": true", StringComparison.Ordinal)
                || record.VerificationJson.Contains("\"Passed\":true", StringComparison.Ordinal);
            reviews.Add(new PhaseReview(
                "Verifying", passed,
                passed ? "校验通过（逐条检查含证据）" : "校验未通过（见 VerificationJson 证据）"));
            if (!passed)
            {
                gaps.Add(new GapItem(
                    "校验未通过：执行产物未满足计划验收标准",
                    "对照 VerificationJson 中失败检查项补做，或修订验收标准后重跑", "warning"));
            }
        }
        else
        {
            reviews.Add(new PhaseReview("Verifying", false, "VerificationJson 缺失"));
            gaps.Add(new GapItem(
                "校验未运行，产物质量未经对照",
                "确认 Verifying 阶段在状态机中被调用", "warning"));
        }

        var achievedCount = reviews.FindAll(r => r.Achieved).Count;
        var summary = gaps.Count == 0
            ? $"全链路 {reviews.Count} 阶段全部达成，无缺口。"
            : $"{reviews.Count} 阶段中 {achievedCount} 项达成，发现 {gaps.Count} 个缺口（critical {gaps.FindAll(g => g.Severity == "critical").Count} / warning {gaps.FindAll(g => g.Severity == "warning").Count}）。";

        return new RetrospectiveRecord
        {
            MissionId = record.Id,
            PhaseReviews = reviews,
            Gaps = gaps,
            Summary = summary,
        };
    }

    /// <summary>把复盘转为经验条目（每个缺口一条 lesson，真实对应）。</summary>
    public IReadOnlyList<LessonRecord> BuildLessons(RetrospectiveRecord retro)
    {
        ArgumentNullException.ThrowIfNull(retro);
        var lessons = new List<LessonRecord>();
        foreach (var gap in retro.Gaps)
        {
            lessons.Add(new LessonRecord
            {
                MissionId = retro.MissionId,
                Phase = InferPhase(gap.Description),
                Gap = gap.Description,
                Suggestion = gap.Suggestion,
                Severity = gap.Severity,
            });
        }

        return lessons;
    }

    /// <summary>复盘 md 落盘（真实写文件），返回文件路径。</summary>
    public string WriteMarkdown(AutonomyDataPaths paths, RetrospectiveRecord retro, MissionRecord record)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(retro);
        paths.EnsureDirectories();

        var sb = new StringBuilder();
        sb.AppendLine($"# 任务复盘 {retro.MissionId}");
        sb.AppendLine();
        sb.AppendLine($"- 任务文本: {Truncate(record.TaskText, 200)}");
        sb.AppendLine($"- 终局: {record.Outcome}；状态: {record.State}");
        sb.AppendLine($"- 生成时间: {retro.GeneratedAtUtc:O}");
        sb.AppendLine();
        sb.AppendLine($"**结论**: {retro.Summary}");
        sb.AppendLine();
        sb.AppendLine("## 逐阶段评审");
        foreach (var r in retro.PhaseReviews)
        {
            sb.AppendLine($"- {(r.Achieved ? "✅" : "❌")} **{r.Phase}**: {r.Evidence}");
        }

        if (retro.Gaps.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## 缺口与补全建议");
            foreach (var g in retro.Gaps)
            {
                sb.AppendLine($"- [{g.Severity}] {g.Description}");
                sb.AppendLine($"  - 建议: {g.Suggestion}");
            }
        }

        var file = Path.Combine(paths.RetrospectivesDirectory, $"retro-{retro.MissionId}.md");
        File.WriteAllText(file, sb.ToString());
        return file;
    }

    private static int CountUnansweredClarifications(string clarificationJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(clarificationJson);
            if (doc.RootElement.TryGetProperty("UnansweredCount", out var el) && el.ValueKind == JsonValueKind.Number)
            {
                return el.GetInt32();
            }
        }
        catch (JsonException)
        {
            // 结构不符时如实返回 0（不虚构缺口）。
        }

        return 0;
    }

    private static string InferPhase(string gapDescription)
    {
        if (gapDescription.Contains("分析", StringComparison.Ordinal)) return "Analyzed";
        if (gapDescription.Contains("澄清", StringComparison.Ordinal)) return "Clarification";
        if (gapDescription.Contains("钢人", StringComparison.Ordinal)) return "Steelman";
        if (gapDescription.Contains("计划", StringComparison.Ordinal)) return "Planning";
        if (gapDescription.Contains("执行", StringComparison.Ordinal)) return "Executing";
        if (gapDescription.Contains("校验", StringComparison.Ordinal)) return "Verifying";
        return "Mission";
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "...";
}
