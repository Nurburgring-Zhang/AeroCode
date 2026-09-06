// Copyright (c) AeroCode
// AdvisorArgsSanitizer（批次 C 安全切片，R3 波次 γ builder-γ；R3 修复 S-MED-1 重写）。
// 工具参数进入 advisor prompt 前的统一脱敏。词表单一事实源 = AeroCode.Harness.Curation.SensitiveTextScrubber
// （R2 缝合 S-L1 收敛后的唯一词表；Moa 已引用 Harness，直接复用，绝不复制词表制造第二源）。
//
// R3 修复（S-MED-1）：整段序列化过词表存在「嵌套结构 + JSON 转义」绕过——嵌套值序列化后
// key 与 : 之间隔着反斜杠（{\"password\": …}），key=value 与字符类模式失配 → 漏检 →
// Modified=false → 收紧门误判"未修改"。修复后检测与打码都覆盖未转义原始值：
// 递归遍历每个参数值的所有字符串叶子（含嵌套对象/数组；嵌套 JSON 文本解析后逐叶子取
// GetString 未转义原值），每个原始值过 canonical 词表；任一叶子命中 → Modified=true 且
// 顶层参数名入 ModifiedParameterNames，预览中该参数值整体打码。参数名本身命中词表同样计入。
using System.Collections;
using System.Text.Json;
using AeroCode.Harness.Curation;

namespace AeroAgent.Moa.Safety;

/// <summary>一次 args 脱敏的结果报告（advisor prompt 构造前产生；纯数据）。</summary>
/// <param name="SanitizedPreview">脱敏后的参数预览文本（敏感形态以占位符呈现）。</param>
/// <param name="Modified">是否发生脱敏（原始参数含敏感形态）。</param>
/// <param name="ModifiedParameterNames">被脱敏的参数名（顺序 = 参数枚举顺序；自动采纳收紧门消费）。</param>
public sealed record AdvisorArgsSanitizationReport(
    string SanitizedPreview,
    bool Modified,
    IReadOnlyList<string> ModifiedParameterNames);

/// <summary>
/// args 脱敏器（纯函数、无 IO、可独立单测）。
/// 检测：<b>逐参数递归收集字符串叶子的未转义原始值</b>过 canonical 词表——嵌套对象/数组
///（ToolRouter.MaterializeArgs 以 GetRawText 保留为 JSON 文本字符串）解析后逐叶子取
/// <see cref="JsonElement.GetString()"/> 原值，规避 JSON 转义（\"、\uXXXX）造成的模式失配；
/// 参数名本身也过词表。任一命中 → Modified=true + 顶层参数名入 ModifiedParameterNames。
/// 打码：命中参数的值在预览中整体替换为占位符（占位符 = canonical 词表的 [REDACTED]，
/// 与 eval 侧 SensitiveScrubber.RedactionMark 同形，S-L1 收敛口径），序列化后再整体过
/// 词表兜底（fail-closed：宁可多打码，不漏敏感原文）。序列化失败的降级路径（逐项列举）
/// 同样过词表——降级不豁免脱敏。
/// </summary>
public static class AdvisorArgsSanitizer
{
    /// <summary>脱敏占位符（与 Harness canonical 词表打码标记同形）。</summary>
    public const string RedactionPlaceholder = "[REDACTED]";

    /// <summary>
    /// 脱敏物化参数。null/空参数 → 无修改、空预览（"{}"/"(无参数)" 等展示语义由调用方决定）。
    /// </summary>
    public static AdvisorArgsSanitizationReport Sanitize(IReadOnlyDictionary<string, object?>? args)
    {
        if (args is null || args.Count == 0)
        {
            return new AdvisorArgsSanitizationReport(string.Empty, false, Array.Empty<string>());
        }

        // 逐参数检测（原始值口径，不经序列化转义）+ 构造打码预览副本。
        var modifiedParams = new List<string>();
        var previewCopy = new Dictionary<string, object?>(args.Count, StringComparer.Ordinal);
        foreach (var kv in args)
        {
            if (IsSensitiveText(kv.Key) || AnyLeafSensitive(kv.Value))
            {
                modifiedParams.Add(kv.Key);
                // 命中参数的值整体打码——嵌套深处的敏感叶子随顶层值一并掩码，
                // 预览中绝无未转义/已转义的敏感原文残留。
                previewCopy[kv.Key] = RedactionPlaceholder;
            }
            else
            {
                previewCopy[kv.Key] = kv.Value;
            }
        }

        string text;
        try
        {
            text = JsonSerializer.Serialize(previewCopy);
        }
        catch (Exception)
        {
            // 与 PermissionAdvisor 既有降级同口径：逐项列举——但每一项仍必须过词表（下方兜底）。
            text = string.Join("\n", previewCopy.Select(kv => $"{kv.Key} = {kv.Value ?? "null"}"));
        }

        // fail-closed 兜底：整段再过一次 canonical 词表（参数名命中/跨段形态等残余一网打尽）。
        var sanitized = SensitiveTextScrubber.Scrub(text);
        return new AdvisorArgsSanitizationReport(sanitized, modifiedParams.Count > 0, modifiedParams);
    }

    /// <summary>文本过 canonical 词表后发生变化 = 命中敏感形态（null/空 = 不命中）。</summary>
    private static bool IsSensitiveText(string? text) =>
        !string.IsNullOrEmpty(text) &&
        !string.Equals(SensitiveTextScrubber.Scrub(text), text, StringComparison.Ordinal);

    /// <summary>参数值的任一线符叶子命中词表（收集后逐个过词表）。</summary>
    private static bool AnyLeafSensitive(object? value)
    {
        var leaves = new List<string>();
        CollectRawStringLeaves(value, leaves);
        foreach (var leaf in leaves)
        {
            if (IsSensitiveText(leaf))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 递归收集字符串叶子的<b>未转义原始值</b>：
    /// string → 本身即叶子；若可解析为 JSON 对象/数组（嵌套结构的 GetRawText 形态）→
    /// 继续遍历其字符串叶子（<see cref="JsonElement.GetString()"/> 已还原 \"、\uXXXX 转义）；
    /// JsonElement/字典/枚举 → 递归；数值/布尔/null → 无字符串叶子。
    /// </summary>
    private static void CollectRawStringLeaves(object? value, List<string> leaves)
    {
        switch (value)
        {
            case null:
                return;
            case string s:
                leaves.Add(s);
                AppendJsonStringLeaves(s, leaves);
                return;
            case JsonElement element:
                AppendJsonElementLeaves(element, leaves);
                return;
            case IDictionary dictionary:
                foreach (var v in dictionary.Values)
                {
                    CollectRawStringLeaves(v, leaves);
                }

                return;
            case IEnumerable items:
                foreach (var item in items)
                {
                    CollectRawStringLeaves(item, leaves);
                }

                return;
            default:
                return; // long/double/bool 等标量：无字符串叶子
        }
    }

    /// <summary>字符串若为嵌套 JSON 文本（对象/数组），解析后收集其未转义字符串叶子。</summary>
    private static void AppendJsonStringLeaves(string text, List<string> leaves)
    {
        var trimmed = text.AsSpan().Trim();
        if (trimmed.IsEmpty || (trimmed[0] != '{' && trimmed[0] != '['))
        {
            return; // 非容器形态：字符串本身已作为叶子参与检测
        }

        try
        {
            using var doc = JsonDocument.Parse(text);
            AppendJsonElementLeaves(doc.RootElement, leaves);
        }
        catch (JsonException)
        {
            // 不是合法 JSON：字符串本身已作为叶子参与检测，无嵌套叶子可收集。
        }
    }

    /// <summary>JSON 元素遍历：字符串叶子取 GetString 未转义原值（双重编码递归处理）。</summary>
    private static void AppendJsonElementLeaves(JsonElement element, List<string> leaves)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                var s = element.GetString();
                if (s is not null)
                {
                    leaves.Add(s);
                    AppendJsonStringLeaves(s, leaves);
                }

                break;
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.String)
                    {
                        var propValue = prop.Value.GetString();
                        if (propValue is not null)
                        {
                            // S-MED-1 根因修复：嵌套 JSON 里 keyword 与 : 之间隔着引号
                            //（"password":"v"），canonical key=value 模式失配。合成未转义
                            // key=value 叶子恢复检测口径（值含 \uXXXX 转义时此处已是还原原值）。
                            leaves.Add($"{prop.Name}={propValue}");
                        }
                    }

                    AppendJsonElementLeaves(prop.Value, leaves);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    AppendJsonElementLeaves(item, leaves);
                }

                break;
            default:
                break; // 数值/布尔/null 叶子：无字符串形态
        }
    }
}
