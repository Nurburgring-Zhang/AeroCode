// Copyright (c) AeroCode
// AcsDoubleBlindAudit — ACS v2.3.0 双盲审查合规校验（E23）。
// 证据基线 E23：两名互相看不见对方推理的独立审查者；同一个人审两轮不是双盲；
// 分歧说明验收标准有歧义，verdict 冲突必须仲裁留痕。
// 本校验器验证审查记录集是否满足：最少审查者数、builder≠reviewer、verdict 冲突有仲裁。
namespace AeroCode.Harness.Acs;

/// <summary>单条审查记录。</summary>
public sealed record AcsReviewRecord(
    string ReviewerId,
    string BuilderId,
    string Verdict,
    string? ArbitrationNote);

/// <summary>双盲审计结果。</summary>
public sealed record AcsDoubleBlindResult(bool Compliant, IReadOnlyList<string> Problems);

/// <summary>
/// 双盲审查合规校验器。
/// </summary>
public sealed class AcsDoubleBlindAudit
{
    private readonly AcsThresholds _thresholds;

    /// <summary>构造（默认内嵌阈值）。</summary>
    public AcsDoubleBlindAudit(AcsThresholds? thresholds = null)
    {
        _thresholds = thresholds ?? AcsThresholds.Default;
    }

    /// <summary>
    /// 校验一组审查记录是否满足 ACS 双盲纪律。
    /// minReviewers 取 checklist.min_adversarial_reviews（默认 2）。
    /// </summary>
    public AcsDoubleBlindResult Audit(IReadOnlyList<AcsReviewRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var problems = new List<string>();

        // 最少独立审查者数
        var distinctReviewers = records.Select(r => r.ReviewerId)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count();
        if (distinctReviewers < 2)
        {
            problems.Add($"独立审查者 {distinctReviewers} < 2（E23：双盲须 ≥2 独立审查者，同一人审两轮不是双盲）");
        }

        // builder ≠ reviewer（禁建造者自审）
        foreach (var r in records)
        {
            if (string.Equals(r.BuilderId, r.ReviewerId, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"审查者 {r.ReviewerId} 即建造者（禁建造者自审，验证者必须独立于执行者 E6）");
            }
        }

        // verdict 冲突必须仲裁留痕
        var verdicts = records.Select(r => r.Verdict)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (verdicts.Count > 1)
        {
            // 有分歧：每条记录都必须有仲裁留痕
            var missingArbitration = records.Where(r => string.IsNullOrWhiteSpace(r.ArbitrationNote)).ToList();
            if (missingArbitration.Count > 0)
            {
                problems.Add(
                    $"verdict 分歧（{string.Join("/", verdicts)}）但 {missingArbitration.Count} 条记录缺仲裁留痕（E23：分歧必须仲裁留痕）");
            }
        }

        return new AcsDoubleBlindResult(problems.Count == 0, problems);
    }
}
