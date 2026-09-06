// Copyright (c) AeroCode
// CompareRunner — C3 评测 compare：解析两份三指标报告，判定 after 相对 before 是否回退。
// 语义：任一**已实测**指标回退 → Passed=false（CI 阻断）；PENDING 口径不参与回退判定
// （dry-run 环境两份都 PENDING → 通过）；解析失败/字段缺失 → fail-closed（Errors 非空，绝不默认通过）。
namespace AeroCode.Eval;

/// <summary>指标回退判定方向表：true = 越低越好（幻觉率/单位成本），false = 越高越好（checkpoint 通过率）。</summary>
public static class EvalMetricDirection
{
    private static readonly Dictionary<string, bool> LowerIsBetter = new(StringComparer.Ordinal)
    {
        ["multi_turn_hallucination_rate"] = true,
        ["checkpoint_pass_rate"] = false,
        ["unit_cost_completion"] = true,
    };

    /// <summary>指标值方向；未知口径 → false（fail-closed 由调用方在上游拒绝未知口径）。</summary>
    public static bool IsLowerBetter(string metricKey) =>
        LowerIsBetter.TryGetValue(metricKey, out var lower) && lower;
}

/// <summary>从单份报告解析出的单指标值。</summary>
public sealed record EvalReportMetricValue
{
    public required string MetricKey { get; init; }

    /// <summary>true = PENDING（未实测）；不参与回退判定。</summary>
    public bool IsPending { get; init; }

    /// <summary>已实测数值（<see cref="IsPending"/>=false 时非 null）。</summary>
    public double? Value { get; init; }

    /// <summary>原始值文本（诊断留痕；不含凭据——报告生成端已脱敏）。</summary>
    public string? Raw { get; init; }
}

/// <summary>单指标对比结论。</summary>
public sealed record EvalMetricComparison
{
    /// <summary><see cref="CompareRunner.OutcomeSkipped"/> / <see cref="CompareRunner.OutcomePass"/> /
    /// <see cref="CompareRunner.OutcomeRegression"/>。</summary>
    public required string Outcome { get; init; }

    public required string MetricKey { get; init; }

    public EvalReportMetricValue? Before { get; init; }

    public EvalReportMetricValue? After { get; init; }

    /// <summary>回退幅度（Regression 时非 null；已按指标方向归一为「变差量」，恒为正）。</summary>
    public double? RegressionDelta { get; init; }
}

/// <summary>一次 compare 的结论。<see cref="Errors"/> 非空 = fail-closed（解析失败，退出码必须非 0）。</summary>
public sealed record CompareRunResult
{
    public required bool Passed { get; init; }

    public required IReadOnlyList<EvalMetricComparison> Comparisons { get; init; }

    /// <summary>fail-closed 明细（报告缺失/指标段缺失/值行缺失/值不可解析）。</summary>
    public required IReadOnlyList<string> Errors { get; init; }
}

public sealed record CompareRunOptions
{
    public required string BeforePath { get; init; }

    public required string AfterPath { get; init; }

    /// <summary>进度日志出口（每行经脱敏）；null = 不输出。</summary>
    public TextWriter? Log { get; init; }
}

/// <summary>
/// compare 执行器：读两份报告 → 逐指标解析（PENDING/数值）→ 按「已实测指标任一回退即阻断」裁决。
/// 纯读取组件：不写任何文件；所有错误以 Errors 明细返回（fail-closed），不抛裸异常吞语义。
/// 回退判定容差 1e-9（P2 渲染的同值重跑浮点噪声不判回退）。
/// </summary>
public sealed class CompareRunner
{
    public const string OutcomeSkipped = "Skipped";
    public const string OutcomePass = "Pass";
    public const string OutcomeRegression = "Regression";

    /// <summary>回退判定容差：报告数值以 P2（两位小数）渲染，比较需容忍浮点表示噪声。</summary>
    public const double Tolerance = 1e-9;

    /// <summary>compare 期望在两份报告中都出现的指标口径（与 BaselineRunner.MetricKeys 一致）。</summary>
    public static readonly string[] MetricKeys = BaselineRunner.MetricKeys;

    public CompareRunResult Compare(CompareRunOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var errors = new List<string>();
        var beforeValues = ParseReport(options.BeforePath, "before", errors);
        var afterValues = ParseReport(options.AfterPath, "after", errors);

        var comparisons = new List<EvalMetricComparison>();
        if (errors.Count == 0)
        {
            foreach (var key in MetricKeys)
            {
                comparisons.Add(CompareMetric(key, beforeValues[key], afterValues[key]));
            }
        }

        var passed = errors.Count == 0 && comparisons.All(c => c.Outcome != OutcomeRegression);
        Log(options, $"compare 结论: passed={passed} 回退={comparisons.Count(c => c.Outcome == OutcomeRegression)} " +
            $"跳过(PENDING)={comparisons.Count(c => c.Outcome == OutcomeSkipped)} 解析失败={errors.Count}");
        foreach (var error in errors)
        {
            Log(options, "fail-closed: " + error);
        }

        return new CompareRunResult
        {
            Passed = passed,
            Comparisons = comparisons,
            Errors = errors,
        };
    }

    private static EvalMetricComparison CompareMetric(string key, EvalReportMetricValue before, EvalReportMetricValue after)
    {
        if (before.IsPending || after.IsPending)
        {
            // PENDING 口径不参与回退判定（dry-run 环境两份都 PENDING → 通过）。
            return new EvalMetricComparison
            {
                Outcome = OutcomeSkipped,
                MetricKey = key,
                Before = before,
                After = after,
            };
        }

        var lowerIsBetter = EvalMetricDirection.IsLowerBetter(key);
        var beforeValue = before.Value!.Value;
        var afterValue = after.Value!.Value;
        var delta = afterValue - beforeValue;
        // 容差比较：|delta| ≤ Tolerance 视为持平（不判回退）。
        var regressed = lowerIsBetter ? delta > Tolerance : delta < -Tolerance;
        return new EvalMetricComparison
        {
            Outcome = regressed ? OutcomeRegression : OutcomePass,
            MetricKey = key,
            Before = before,
            After = after,
            RegressionDelta = regressed ? Math.Abs(delta) : null,
        };
    }

    /// <summary>解析一份报告为三指标值表；任何结构/取值问题都进 errors（fail-closed，不默认通过）。</summary>
    private static Dictionary<string, EvalReportMetricValue> ParseReport(
        string path, string label, ICollection<string> errors)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            errors.Add($"{label} 报告不可读: {path}（{ex.GetType().Name}: {ex.Message}）");
            return new Dictionary<string, EvalReportMetricValue>(StringComparer.Ordinal);
        }

        var values = new Dictionary<string, EvalReportMetricValue>(StringComparer.Ordinal);
        foreach (var key in MetricKeys)
        {
            var headingIndex = FindMetricHeading(lines, key);
            if (headingIndex < 0)
            {
                errors.Add($"{label} 报告缺少指标段 ({key}): {path}");
                continue;
            }

            var valueText = ExtractValueText(lines, headingIndex);
            if (valueText is null)
            {
                errors.Add($"{label} 报告指标段 ({key}) 缺少「值」行: {path}");
                continue;
            }

            var parsed = ParseValueText(key, valueText);
            if (parsed is null)
            {
                errors.Add($"{label} 报告指标 ({key}) 值不可解析（既非 PENDING 也非本口径数值形态）: {valueText}");
                continue;
            }

            values[key] = parsed with { MetricKey = key };
        }

        return values;
    }

    /// <summary>定位指标段标题行（形如 <c>## N. 显示名 (metric_key)</c>）；找不到返回 -1。</summary>
    private static int FindMetricHeading(string[] lines, string metricKey)
    {
        var marker = $"({metricKey})";
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimStart();
            if (line.StartsWith("## ", StringComparison.Ordinal) && line.EndsWith(marker, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>在指标段内（到下一个 ## 标题为止）提取「值」行反引号加粗内的文本。</summary>
    private static string? ExtractValueText(string[] lines, int headingIndex)
    {
        for (var i = headingIndex + 1; i < lines.Length; i++)
        {
            var line = lines[i].TrimStart();
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                return null; // 进入下一段仍未见到值行。
            }

            if (!line.StartsWith("- 值:", StringComparison.Ordinal) &&
                !line.StartsWith("- 值：", StringComparison.Ordinal))
            {
                continue;
            }

            var open = line.IndexOf("**", StringComparison.Ordinal);
            if (open < 0)
            {
                return string.Empty.Trim(); // 值行存在但无加粗标记 → 空值形态，交由上层判不可解析。
            }

            var close = line.IndexOf("**", open + 2, StringComparison.Ordinal);
            return close < 0
                ? line[(open + 2)..].Trim()
                : line[(open + 2)..close].Trim();
        }

        return null;
    }

    /// <summary>
    /// 解析值文本：PENDING → 未实测；幻觉率/通过率 → 百分比（P2 形态）；单位成本 → Σcost/Σquality 数值。
    /// 容忍千分位逗号小数（文化差异），其余形态一律不可解析（fail-closed）。
    /// </summary>
    private static EvalReportMetricValue? ParseValueText(string metricKey, string valueText)
    {
        if (string.IsNullOrWhiteSpace(valueText))
        {
            return null;
        }

        if (string.Equals(valueText.Trim(), EvalStatus.Pending, StringComparison.Ordinal))
        {
            return new EvalReportMetricValue
            {
                MetricKey = metricKey,
                IsPending = true,
                Raw = valueText,
            };
        }

        double? number = metricKey switch
        {
            "multi_turn_hallucination_rate" => ParseLeadingPercent(valueText),
            "checkpoint_pass_rate" => ParseLeadingPercent(valueText),
            "unit_cost_completion" => ParseUnitCost(valueText),
            _ => null,
        };
        return number is null
            ? null
            : new EvalReportMetricValue
            {
                MetricKey = metricKey,
                IsPending = false,
                Value = number,
                Raw = valueText,
            };
    }

    /// <summary>解析 P2 百分比形态（如 <c>33.33%（1/3 任务判定幻觉）</c>）。</summary>
    private static double? ParseLeadingPercent(string text)
    {
        var match = PercentPattern.Match(text);
        if (!match.Success)
        {
            return null;
        }

        return double.Parse(match.Groups["num"].Value.Replace(",", ".", StringComparison.Ordinal),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>解析单位成本形态（如 <c>Σcost/Σquality = 0.001234 USD/分；…</c>）。</summary>
    private static double? ParseUnitCost(string text)
    {
        var match = UnitCostPattern.Match(text);
        if (!match.Success)
        {
            return null;
        }

        return double.Parse(match.Groups["num"].Value.Replace(",", ".", StringComparison.Ordinal),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static readonly System.Text.RegularExpressions.Regex PercentPattern = new(
        @"^\s*(?<num>-?\d+(?:[.,]\d+)?)\s*%",
        System.Text.RegularExpressions.RegexOptions.Compiled,
        matchTimeout: TimeSpan.FromSeconds(2));

    private static readonly System.Text.RegularExpressions.Regex UnitCostPattern = new(
        @"Σcost/Σquality\s*=\s*(?<num>-?\d+(?:[.,]\d+)?)",
        System.Text.RegularExpressions.RegexOptions.Compiled,
        matchTimeout: TimeSpan.FromSeconds(2));

    private static void Log(CompareRunOptions options, string message)
    {
        if (options.Log is not null)
        {
            options.Log.WriteLine("[eval] " + message);
        }
    }
}
