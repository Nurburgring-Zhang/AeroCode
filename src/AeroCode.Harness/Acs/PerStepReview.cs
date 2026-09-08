// Copyright (c) AeroCode
// PerStepReview — ACS v2.3.0 每步双 AI 自对抗审核。
// 每步收尾做一次 builder/verifier 署名分离的对审 + 自查自检自监督；
// verdict ∈ {pass, pass_with_fixes, fail}；verdict=fail 表示该步未过关，禁止进入下一步；
// 发现问题必须等量闭环（issues_found 非空则 issues_closed 必须非空并写复验记录）；
// verifier ≠ builder（对审人 ≠ 建造者，防同一角色自签）；T3 每步 ≥2 轮。
namespace AeroCode.Harness.Acs;

/// <summary>每步自审 verdict。</summary>
public enum AcsReviewVerdict
{
    /// <summary>通过。</summary>
    Pass,

    /// <summary>通过但带修复项（问题已等量闭环）。</summary>
    PassWithFixes,

    /// <summary>未过关（禁止进入下一步）。</summary>
    Fail,
}

/// <summary>每步自审记录（builder≠verifier 署名）。</summary>
public sealed record AcsStepReviewRecord(
    string Builder,
    string Verifier,
    AcsReviewVerdict Verdict,
    string IssuesFound,
    string IssuesClosed,
    string SelfCheck,
    string? Recheck,
    int Round);

/// <summary>每步自审校验结果。</summary>
public sealed record AcsPerStepReviewResult(bool Accepted, IReadOnlyList<string> Problems);

/// <summary>
/// 每步双 AI 自审核校验器：校验自审记录是否符合 ACS 契约。
/// </summary>
public sealed class PerStepReview
{
    private readonly AcsThresholds _thresholds;

    /// <summary>构造（默认内嵌阈值）。</summary>
    public PerStepReview(AcsThresholds? thresholds = null)
    {
        _thresholds = thresholds ?? AcsThresholds.Default;
    }

    /// <summary>
    /// 校验一条自审记录。返回是否接受 + 问题清单。
    /// tier 用于判定是否适用（apply_tiers）与 T3 轮数下限。
    /// </summary>
    public AcsPerStepReviewResult Validate(AcsStepReviewRecord record, string tier)
    {
        ArgumentNullException.ThrowIfNull(record);
        var psr = _thresholds.PerStepReview;
        var problems = new List<string>();

        // 适用分级检查
        if (!psr.ApplyTiers.Contains(tier, StringComparer.Ordinal))
        {
            return new AcsPerStepReviewResult(true, Array.Empty<string>()); // 不适用分级直接放行
        }

        // builder ≠ verifier
        if (psr.RequireVerifierNeBuilder &&
            string.Equals(record.Builder, record.Verifier, StringComparison.OrdinalIgnoreCase))
        {
            problems.Add("verifier 与 builder 同一署名（对审人必须独立于建造者，禁自签）");
        }

        // verdict=fail = 未过关
        if (record.Verdict == AcsReviewVerdict.Fail)
        {
            problems.Add("verdict=fail：该步未过关，禁止进入下一步");
        }

        // 自查最小字符
        if (string.IsNullOrWhiteSpace(record.SelfCheck) ||
            record.SelfCheck.Trim().Length < psr.MinSelfCheckChars)
        {
            problems.Add($"self_check 缺失或少于 {psr.MinSelfCheckChars} 字符");
        }

        // 等量闭环：issues_found 非空则 issues_closed 必须非空
        if (!string.IsNullOrWhiteSpace(record.IssuesFound) &&
            record.IssuesFound.Trim().Length >= psr.MinIssueChars)
        {
            if (string.IsNullOrWhiteSpace(record.IssuesClosed) ||
                record.IssuesClosed.Trim().Length < psr.MinIssueChars)
            {
                problems.Add("issues_found 非空但 issues_closed 缺失/过短（发现问题必须等量闭环）");
            }

            if (string.IsNullOrWhiteSpace(record.Recheck))
            {
                problems.Add("发现问题但缺复验记录 recheck");
            }
        }

        // T3 每步最少轮数
        if (tier == "T3" && record.Round < psr.T3MinRounds)
        {
            problems.Add($"T3 每步自审须 ≥{psr.T3MinRounds} 轮（当前第 {record.Round} 轮）");
        }

        return new AcsPerStepReviewResult(problems.Count == 0, problems);
    }
}
