// Copyright (c) AeroCode
// 事实断言检测 hook（R1 波次 β）：纯函数启发式——检测输出文本中的事实形断言
// （数字/日期/命名实体断言）且上下文无对应工具证据 → 返回标记列表。
// 只检测不处置：R1 不拦截、不修改任何输出；guard chain 接入归 R2 B5。
using System.Text.RegularExpressions;

namespace AeroAgent.Moa.Curation;

/// <summary>事实断言类别。</summary>
public enum FactAssertionKind
{
    /// <summary>数字断言（计数/百分比/度量值 + 断言谓词）。</summary>
    Number,
    /// <summary>日期断言（绝对日期，本身就是事实形断言）。</summary>
    Date,
    /// <summary>命名实体断言（专名/引号实体 + 断言谓词）。</summary>
    NamedEntity,
}

/// <summary>一条疑似事实断言标记（只标记，不裁决真假）。</summary>
/// <param name="Kind">断言类别。</param>
/// <param name="MatchedText">断言关键值（数字/日期/实体原样文本，供证据比对与后续处置）。</param>
/// <param name="Excerpt">断言所在句摘录（截断，供人工复核）。</param>
public sealed record FactAssertion(FactAssertionKind Kind, string MatchedText, string Excerpt);

/// <summary>
/// 事实断言检测器（纯函数、无状态、无 IO）。
/// 口径：句子含断言谓词（是/为/共/达/超过/等于/完成/返回/支持/is/are/equals/returns/supports…）
/// 且携带关键值（数字/日期/实体），而 groundedEvidence 中找不到该关键值 → 记为疑似未佐证断言。
/// 围栏代码块、表格行不参与检测（代码与数据清单不是叙述性事实断言）。
/// </summary>
public static class FactAssertionDetector
{
    private const string AssertionMarkers =
        "是|为|共|达|超过|少于|等于|完成|返回|支持|命中|成功|失败|等于|合计|总计|" +
        "is|are|was|were|equals|total|totals|returns|returned|supports|completed|exactly|at least|count";

    private static readonly Regex CodeFencePattern =
        new("```.*?```", RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex DatePattern =
        new(@"\d{4}\s?[-/年]\s?\d{1,2}\s?[-/月]\s?\d{1,2}\s?日?", RegexOptions.Compiled);

    private static readonly Regex NumberPattern =
        new(@"\d[\d,]*(?:\.\d+)?\s?(?:%|个|条|次|项|行|层|轮|ms|s|KB|MB|GB| tokens)?",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex LatinEntityPattern =
        new(@"\b[A-Z][A-Za-z0-9_]{2,}\b", RegexOptions.Compiled);

    private static readonly Regex QuotedEntityPattern =
        new("[「“'\"]([^「」“”'\"]{1,24})[」”'\"]", RegexOptions.Compiled);

    private static readonly Regex MarkerPattern =
        new($"(?:{AssertionMarkers})", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// 检测 <paramref name="text"/> 中的事实形断言。
    /// <paramref name="groundedEvidence"/> = 佐证文本集合（工具输出/已保留上下文等）；
    /// 断言关键值在其中任一条出现（忽略大小写、数字去逗号比对）即视为已佐证，不标记。
    /// evidence 为 null/空 = 全部无佐证。
    /// </summary>
    public static IReadOnlyList<FactAssertion> Detect(string? text, IEnumerable<string>? groundedEvidence = null)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<FactAssertion>();
        }

        var evidence = (groundedEvidence ?? Array.Empty<string>()).Where(e => !string.IsNullOrEmpty(e)).ToList();
        var sentences = SplitSentences(CodeFencePattern.Replace(text, " "));
        var found = new List<FactAssertion>();
        var seen = new HashSet<(FactAssertionKind, string)>();

        foreach (var sentence in sentences)
        {
            if (sentence.Length == 0 || !MarkerPattern.IsMatch(sentence))
            {
                continue;
            }

            var excerpt = sentence.Length <= 120 ? sentence : sentence[..120] + "…";
            var numberScan = sentence;

            foreach (Match m in DatePattern.Matches(sentence))
            {
                numberScan = numberScan.Replace(m.Value, " ");
                if (IsGrounded(m.Value, evidence))
                {
                    continue;
                }

                if (seen.Add((FactAssertionKind.Date, m.Value)))
                {
                    found.Add(new FactAssertion(FactAssertionKind.Date, m.Value, excerpt));
                }
            }

            foreach (Match m in NumberPattern.Matches(numberScan))
            {
                if (!IsAssertiveNumber(m.Value.Trim()) || IsGrounded(m.Value, evidence))
                {
                    continue;
                }

                if (seen.Add((FactAssertionKind.Number, m.Value)))
                {
                    found.Add(new FactAssertion(FactAssertionKind.Number, m.Value, excerpt));
                }
            }

            foreach (var (pattern, kind) in new[]
                     {
                         (LatinEntityPattern, FactAssertionKind.NamedEntity),
                         (QuotedEntityPattern, FactAssertionKind.NamedEntity),
                     })
            {
                foreach (Match m in pattern.Matches(sentence))
                {
                    var entity = (m.Groups.Count > 1 ? m.Groups[1].Value : m.Value).Trim();
                    if (entity.Length < 2 || IsGrounded(entity, evidence))
                    {
                        continue;
                    }

                    if (seen.Add((kind, entity)))
                    {
                        found.Add(new FactAssertion(kind, entity, excerpt));
                    }
                }
            }
        }

        return found;
    }

    /// <summary>断言-worthy 数字：≥2 位、含小数、或带单位/百分号（排除孤立 "1" 这类噪声）。</summary>
    private static bool IsAssertiveNumber(string rawValue)
    {
        var value = rawValue.Trim();
        if (value.Length == 0)
        {
            return false;
        }

        if (value.Contains('%') || value.Contains('个') || value.Contains('条') || value.Contains('次')
            || value.Contains('项') || value.Contains('行') || value.Contains('层') || value.Contains('轮')
            || value.EndsWith("ms", StringComparison.OrdinalIgnoreCase)
            || value.EndsWith("KB", StringComparison.OrdinalIgnoreCase)
            || value.EndsWith("MB", StringComparison.OrdinalIgnoreCase)
            || value.EndsWith("GB", StringComparison.OrdinalIgnoreCase)
            || value.EndsWith("tokens", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var digits = value.Replace(",", string.Empty).TrimEnd('%');
        var dot = digits.IndexOf('.');
        if (dot >= 0)
        {
            digits = digits[..dot];
        }

        return digits.Length >= 2;
    }

    /// <summary>关键值是否出现在任一佐证文本中（原样 + 数字去逗号两种比对）。</summary>
    private static bool IsGrounded(string value, IReadOnlyList<string> evidence)
    {
        if (evidence.Count == 0)
        {
            return false;
        }

        var raw = value.Trim();
        var normalized = raw.Replace(",", string.Empty);
        foreach (var e in evidence)
        {
            if (e.Contains(raw, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!string.Equals(normalized, raw, StringComparison.Ordinal)
                && e.Contains(normalized, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> SplitSentences(string text)
        => text.Split(new[] { '\n', '。', '！', '？', '!', '?' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.Length > 0 && !s.StartsWith('|'));
}
