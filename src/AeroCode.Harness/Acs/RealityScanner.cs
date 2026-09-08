// Copyright (c) AeroCode
// RealityScanner — ACS 真实性扫描：模糊措辞检测 + 验收绑定校验。
// 证据基线 E22：行为测试全绿 ≠ 有产出——验收命令必须绑定声明的产出物，
// 尺子量的是「改没改坏」不是「有没有改」。
// 模糊措辞（"应该可以"/"looks good" 等）出现在验收/证据描述中 = 无法核验 = finding。
// 行内含 ACS-ALLOW 标记的模糊措辞放行（显式豁免）。
namespace AeroCode.Harness.Acs;

/// <summary>真实性 finding。</summary>
public sealed record RealityFinding(string Kind, string Phrase, int LineIndex, string Line);

/// <summary>
/// 真实性扫描器：模糊措辞 + 验收绑定。
/// </summary>
public sealed class RealityScanner
{
    private readonly AcsThresholds _thresholds;

    /// <summary>构造（默认内嵌阈值）。</summary>
    public RealityScanner(AcsThresholds? thresholds = null)
    {
        _thresholds = thresholds ?? AcsThresholds.Default;
    }

    /// <summary>
    /// 扫描验收/证据文本中的模糊措辞。
    /// side=acceptance 用验收词表，side=evidence 用证据词表。
    /// 行内含 ACS-ALLOW 标记的行放行。finding 数封顶 default_max_findings。
    /// </summary>
    public IReadOnlyList<RealityFinding> ScanVaguePhrases(string? text, string side = "acceptance")
    {
        if (string.IsNullOrEmpty(text))
        {
            return Array.Empty<RealityFinding>();
        }

        var phrases = side == "evidence"
            ? _thresholds.VaguePhrases.Evidence
            : _thresholds.VaguePhrases.Acceptance;
        var allowMarker = _thresholds.RealityScan.AllowMarker;
        var maxFindings = _thresholds.RealityScan.DefaultMaxFindings;

        var findings = new List<RealityFinding>();
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length && findings.Count < maxFindings; i++)
        {
            var line = lines[i];
            if (line.IndexOf(allowMarker, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                continue; // 显式豁免
            }

            foreach (var phrase in phrases)
            {
                if (line.IndexOf(phrase, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    findings.Add(new RealityFinding("vague-phrase", phrase, i, line.Trim()));
                    break; // 一行只报一个措辞
                }
            }
        }

        return findings;
    }

    /// <summary>
    /// 验收绑定校验：验收标准必须引用至少一个声明的产出物（文件名/工件名）。
    /// acceptance 中未出现任何 declaredArtifact 的基名 = 未绑定 = finding（E22）。
    /// </summary>
    public IReadOnlyList<RealityFinding> CheckAcceptanceBinding(
        string acceptanceText,
        IReadOnlyList<string> declaredArtifacts)
    {
        ArgumentNullException.ThrowIfNull(declaredArtifacts);
        if (string.IsNullOrWhiteSpace(acceptanceText))
        {
            return new[] { new RealityFinding("acceptance-unbound", "(empty)", 0, "验收文本为空") };
        }

        if (declaredArtifacts.Count == 0)
        {
            return new[] { new RealityFinding("acceptance-unbound", "(no artifacts)", 0, "无声明产出物，验收无从绑定") };
        }

        var bound = declaredArtifacts.Any(a =>
            !string.IsNullOrWhiteSpace(a) &&
            acceptanceText.IndexOf(System.IO.Path.GetFileName(a), StringComparison.OrdinalIgnoreCase) >= 0);

        if (!bound)
        {
            return new[]
            {
                new RealityFinding("acceptance-unbound", "(unbound)", 0,
                    "验收标准未引用任何声明产出物的基名（E22：验收必须绑定产出物）"),
            };
        }

        return Array.Empty<RealityFinding>();
    }
}
