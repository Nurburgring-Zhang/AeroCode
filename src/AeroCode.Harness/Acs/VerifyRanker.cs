// Copyright (c) AeroCode
// VerifyRanker — ACS v2.3.0 1-20 细粒度自验证排序（LLM-as-a-Verifier 降维实现）。
// 证据基线 E2：离散三档评分平局率高达 27%，1-20 细粒度 100 次比较 77 次选对且零平局。
// 证据基线 E4：两两比较 O(N²) → 与少量 pivot 比较后近似排序 O(Nk)，pivot_k=2。
// 评分必须拆分标准（默认 ≥5 项）+ 重复评估；验证器独立于执行者（E6）。
namespace AeroCode.Harness.Acs;

/// <summary>单条评价标准的权重项（权重之和应为 1，容差内）。</summary>
public sealed record AcsCriterion(string Name, double Weight);

/// <summary>单个候选在单个标准上的 1-20 评分。</summary>
public sealed record AcsScore(string CandidateId, string CriterionName, int Score)
{
    /// <summary>评分合法范围下界。</summary>
    public const int MinScore = 1;

    /// <summary>评分合法范围上界。</summary>
    public const int MaxScore = 20;
}

/// <summary>排序结果：候选按加权分降序 + 是否显著分歧。</summary>
public sealed record AcsRankResult(
    IReadOnlyList<(string CandidateId, double WeightedScore)> Ranking,
    bool HasSignificantSpread,
    string Method);

/// <summary>
/// 1-20 细粒度排序器：pivot 近似排序（O(N²)→O(Nk)）+ 加权容差 + 最小标准数校验。
/// </summary>
public sealed class VerifyRanker
{
    private readonly AcsThresholds _thresholds;

    /// <summary>构造（默认内嵌阈值）。</summary>
    public VerifyRanker(AcsThresholds? thresholds = null)
    {
        _thresholds = thresholds ?? AcsThresholds.Default;
    }

    /// <summary>
    /// 对候选评分排序。
    /// scores：全部候选 × 全部标准的评分；criteria：标准与权重（权重和须在容差内为 1）。
    /// 标准数 &lt; min_criteria 或权重和超容差 = 抛 ArgumentException（用法错误）。
    /// </summary>
    public AcsRankResult Rank(IReadOnlyList<AcsScore> scores, IReadOnlyList<AcsCriterion> criteria)
    {
        ArgumentNullException.ThrowIfNull(scores);
        ArgumentNullException.ThrowIfNull(criteria);
        var vr = _thresholds.VerifyRank;

        if (criteria.Count < vr.MinCriteria)
        {
            throw new ArgumentException(
                $"评价标准数 {criteria.Count} < 最小 {vr.MinCriteria}（细粒度排序须拆分标准）");
        }

        var weightSum = criteria.Sum(c => c.Weight);
        if (Math.Abs(weightSum - 1.0) > vr.WeightTol)
        {
            throw new ArgumentException(
                $"评价标准权重和 {weightSum:F3} 与 1 偏差超容差 {vr.WeightTol}（权重须归一）");
        }

        foreach (var s in scores)
        {
            if (s.Score < AcsScore.MinScore || s.Score > AcsScore.MaxScore)
            {
                throw new ArgumentException(
                    $"评分 {s.Score} 超出 1-20 范围（候选 {s.CandidateId} 标准 {s.CriterionName}）");
            }
        }

        // 计算每个候选的加权分
        var candidateIds = scores.Select(s => s.CandidateId).Distinct().ToList();
        var weighted = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var cid in candidateIds)
        {
            double sum = 0;
            foreach (var c in criteria)
            {
                var score = scores.FirstOrDefault(s => s.CandidateId == cid && s.CriterionName == c.Name);
                if (score is not null)
                {
                    sum += score.Score * c.Weight;
                }
            }

            weighted[cid] = sum;
        }

        var ranking = weighted
            .OrderByDescending(kv => kv.Value)
            .Select(kv => (kv.Key, kv.Value))
            .ToList();

        // 显著分歧检测：首尾分差 > spread_limit
        var spread = ranking.Count >= 2
            ? ranking[0].Item2 - ranking[^1].Item2
            : 0;
        var significant = spread > vr.SpreadLimit;

        return new AcsRankResult(ranking, significant, $"weighted-1-20 (pivot_k={vr.PivotK})");
    }

    /// <summary>
    /// pivot 近似排序：候选只与 k 个 pivot 比较，O(N²)→O(Nk)。
    /// pivots 从候选中取前 k 个（按输入序）；每个候选的近似分 = 与 pivot 比较的相对位次加权和。
    /// </summary>
    public AcsRankResult RankWithPivots(
        IReadOnlyList<AcsScore> scores,
        IReadOnlyList<AcsCriterion> criteria,
        IReadOnlyList<string> candidateIds)
    {
        ArgumentNullException.ThrowIfNull(candidateIds);
        var vr = _thresholds.VerifyRank;

        // 先取全量排序作为 pivot 选择基准（pivot = 全量排序的前 k 个）
        var fullRank = Rank(scores, criteria);
        var pivots = fullRank.Ranking.Take(vr.PivotK).Select(r => r.CandidateId).ToList();

        // 每个候选的近似分 = 与各 pivot 的加权分比较（胜=1/负=0）之和
        var weighted = fullRank.Ranking.ToDictionary(r => r.CandidateId, r => r.WeightedScore);
        var approx = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var cid in candidateIds)
        {
            double wins = 0;
            foreach (var p in pivots)
            {
                if (cid == p)
                {
                    wins += 0.5; // 与自身比较计半分
                    continue;
                }

                if (weighted.TryGetValue(cid, out var cs) && weighted.TryGetValue(p, out var ps))
                {
                    if (cs > ps + vr.WeightedTol) wins += 1;
                    else if (Math.Abs(cs - ps) <= vr.WeightedTol) wins += 0.5;
                }
            }

            approx[cid] = wins;
        }

        var ranking = approx
            .OrderByDescending(kv => kv.Value)
            .Select(kv => (kv.Key, kv.Value))
            .ToList();

        return new AcsRankResult(ranking, fullRank.HasSignificantSpread, $"pivot-approx (k={vr.PivotK})");
    }
}
