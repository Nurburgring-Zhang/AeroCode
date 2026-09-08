// Copyright (c) AeroCode
// HonestyLedger — ACS v2.3.0 诚实台账。
// 三重零容忍（零虚假/零模拟实现/零降级）的登记面：必须降级时显式标注并登记
// 原因/影响面/差距；降维实现必须说明降到了什么、差距在哪，不得宣称等效。
// 台账条目必填字段缺失 = 登记无效（诚实纪律不接受半截声明）。
using System.Text;
using System.Text.Json;

namespace AeroCode.Harness.Acs;

/// <summary>台账条目类型。</summary>
public enum HonestyEntryKind
{
    /// <summary>[DEGRADED] 降级：能力受限但显式声明。</summary>
    Degraded,

    /// <summary>[降维实现]：无法复现上游精确做法，降到近似实现。</summary>
    ReducedDimension,
}

/// <summary>台账条目（必填字段齐备才有效）。</summary>
public sealed record HonestyEntry(
    HonestyEntryKind Kind,
    string Subject,
    string Reason,
    string Impact,
    string Gap,
    DateTime TimestampUtc)
{
    /// <summary>条目标记文本（[DEGRADED] / [降维实现]）。</summary>
    public string Marker => Kind == HonestyEntryKind.Degraded ? "[DEGRADED]" : "[降维实现]";
}

/// <summary>台账登记结果。</summary>
public sealed record HonestyRecordResult(bool Accepted, string? Error, HonestyEntry? Entry);

/// <summary>
/// 诚实台账：登记/校验/导出。线程安全。
/// </summary>
public sealed class HonestyLedger
{
    /// <summary>必填字段最小字符数。</summary>
    public const int MinFieldChars = 4;

    private readonly object _lock = new();
    private readonly List<HonestyEntry> _entries = new();

    /// <summary>当前全部条目（只读快照）。</summary>
    public IReadOnlyList<HonestyEntry> Entries
    {
        get { lock (_lock) return _entries.ToArray(); }
    }

    /// <summary>
    /// 登记一条降级/降维声明。必填字段（主题/原因/影响面/差距）任一缺失或过短 = 拒绝登记。
    /// </summary>
    public HonestyRecordResult Record(HonestyEntryKind kind, string subject, string reason, string impact, string gap)
    {
        var invalid = FirstInvalidField(
            ("主题", subject), ("原因", reason), ("影响面", impact), ("差距", gap));
        if (invalid is not null)
        {
            return new HonestyRecordResult(false,
                $"诚实台账登记被拒：{invalid} 缺失或少于 {MinFieldChars} 字符（诚实纪律不接受半截声明）", null);
        }

        var entry = new HonestyEntry(kind, subject.Trim(), reason.Trim(), impact.Trim(), gap.Trim(), DateTime.UtcNow);
        lock (_lock) _entries.Add(entry);
        return new HonestyRecordResult(true, null, entry);
    }

    /// <summary>导出台账为 JSON（审计/交付报告用）。</summary>
    public string ExportJson()
    {
        lock (_lock)
        {
            return JsonSerializer.Serialize(_entries, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
        }
    }

    /// <summary>导出台账为 Markdown（交付报告附录用）。</summary>
    public string ExportMarkdown()
    {
        lock (_lock)
        {
            if (_entries.Count == 0)
            {
                return "_诚实台账：无降级/降维声明。_";
            }

            var sb = new StringBuilder();
            sb.AppendLine("| 标记 | 主题 | 原因 | 影响面 | 差距 |");
            sb.AppendLine("|---|---|---|---|---|");
            foreach (var e in _entries)
            {
                sb.AppendLine($"| {e.Marker} | {e.Subject} | {e.Reason} | {e.Impact} | {e.Gap} |");
            }

            return sb.ToString();
        }
    }

    /// <summary>清空台账（新任务开始时）。</summary>
    public void Clear()
    {
        lock (_lock) _entries.Clear();
    }

    private static string? FirstInvalidField(params (string Name, string Value)[] fields)
    {
        foreach (var (name, value) in fields)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Trim().Length < MinFieldChars)
            {
                return name;
            }
        }

        return null;
    }
}
