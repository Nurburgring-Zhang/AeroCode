// Copyright (c) AeroCode
// HandoffCompressor — ACS v2.3.0 压缩交接契约校验。
// 交接只携带「目标锚点 / 已完成 / 下一步」三要素 + 偏离自检标记；
// 字数硬上限（超出 BLOCK）、目标值（超出告警）、锚点/下一步要素缺失 BLOCK。
namespace AeroCode.Harness.Acs;

/// <summary>交接校验结果。</summary>
public sealed record AcsHandoffResult(int ExitCode, IReadOnlyList<string> Issues, bool WarningOnly)
{
    /// <summary>是否通过。</summary>
    public bool Passed => ExitCode == AcsExitCodes.Pass;
}

/// <summary>
/// 压缩交接校验器：校验交接文本是否符合 ACS 交接契约。
/// </summary>
public sealed class HandoffCompressor
{
    private readonly AcsThresholds _thresholds;

    /// <summary>构造（默认内嵌阈值）。</summary>
    public HandoffCompressor(AcsThresholds? thresholds = null)
    {
        _thresholds = thresholds ?? AcsThresholds.Default;
    }

    /// <summary>
    /// 校验交接文本。退出码：0=PASS / 1=BLOCK / 2=USAGE_ERROR（空输入）。
    /// </summary>
    public AcsHandoffResult Validate(string? handoffText)
    {
        var h = _thresholds.Handoff;

        if (string.IsNullOrWhiteSpace(handoffText))
        {
            return new AcsHandoffResult(AcsExitCodes.UsageError,
                new[] { "交接文本为空（无法核验 = 未交接）" }, WarningOnly: false);
        }

        var issues = new List<string>();
        var block = false;
        var len = handoffText.Length;

        // 字数硬上限
        if (len > h.MaxCharsHard)
        {
            issues.Add($"交接 {len} 字 > 硬上限 {h.MaxCharsHard}（压缩后再交接，回灌历史 = 烧 token）");
            block = true;
        }
        else if (len > h.TargetChars + h.CharsTolerance)
        {
            issues.Add($"交接 {len} 字超出目标 {h.TargetChars}（容差外，建议压缩）");
        }

        // 目标锚点
        if (h.RequireGoalAnchor)
        {
            var anchor = ExtractSection(handoffText, "目标", "goal");
            if (anchor is null || anchor.Trim().Length < h.MinGoalAnchorChars)
            {
                issues.Add($"目标锚点缺失或少于 {h.MinGoalAnchorChars} 字符（交接必须含当前目标锚点，防跑偏）");
                block = true;
            }
        }

        // 下一步要素
        var next = ExtractSection(handoffText, "下一步", "next");
        if (next is null || next.Trim().Length < h.MinNextChars)
        {
            issues.Add($"下一步要素缺失或少于 {h.MinNextChars} 字符（交接三要素：目标/已完成/下一步）");
            block = true;
        }

        // 偏离自检标记
        if (h.RequireDriftChecked &&
            handoffText.IndexOf(h.DriftMarker, StringComparison.OrdinalIgnoreCase) < 0)
        {
            issues.Add($"偏离自检标记缺失（必须含 {h.DriftMarker}，锚点缺失/已偏离须自检）");
            block = true;
        }

        return new AcsHandoffResult(
            block ? AcsExitCodes.Block : AcsExitCodes.Pass,
            issues,
            WarningOnly: !block);
    }

    /// <summary>提取「标题：内容」形式的节内容（支持中英文标题，取首个匹配行）。</summary>
    private static string? ExtractSection(string text, params string[] headers)
    {
        var lines = text.Split('\n');
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            foreach (var header in headers)
            {
                if (trimmed.StartsWith(header, StringComparison.OrdinalIgnoreCase))
                {
                    var idx = trimmed.IndexOf('：');
                    if (idx < 0) idx = trimmed.IndexOf(':');
                    if (idx >= 0 && idx < trimmed.Length - 1)
                    {
                        return trimmed[(idx + 1)..];
                    }
                }
            }
        }

        return null;
    }
}
