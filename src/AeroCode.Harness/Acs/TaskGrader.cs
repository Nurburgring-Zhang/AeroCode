// Copyright (c) AeroCode
// TaskGrader — ACS v2.3.0 任务分级器（T0-T3）。
// 规则引擎：输入任务画像（可判定信号），输出分级 + N/R/安全阀参数（来自 AcsThresholds）。
// 拿不准取高一级（ACS 纪律），理由可审计。
namespace AeroCode.Harness.Acs;

/// <summary>任务分级（ACS T0-T3）。</summary>
public enum AcsTier
{
    /// <summary>闲聊 / 纯问答 / 单文件只读。</summary>
    T0,

    /// <summary>单文件、可逆、≤15min、无外部副作用。</summary>
    T1,

    /// <summary>多文件 / 有持久状态 / 需测试 / 涉及构建。</summary>
    T2,

    /// <summary>不可逆（删除/迁移/部署/推送）、架构级、安全相关、≥5 文件跨模块。</summary>
    T3,
}

/// <summary>任务画像：分级器的输入信号（全部可由调用方客观判定）。</summary>
public sealed record AcsTaskProfile
{
    /// <summary>是否闲聊/纯问答/单文件只读。</summary>
    public bool IsReadOnly { get; init; }

    /// <summary>触及文件数。</summary>
    public int FileCount { get; init; }

    /// <summary>是否跨模块（≥2 模块）。</summary>
    public bool CrossModule { get; init; }

    /// <summary>是否含不可逆操作（删除/迁移/部署/推送/发布）。</summary>
    public bool Irreversible { get; init; }

    /// <summary>是否架构级变更。</summary>
    public bool Architectural { get; init; }

    /// <summary>是否安全相关。</summary>
    public bool SecurityRelevant { get; init; }

    /// <summary>是否有持久状态变更。</summary>
    public bool PersistentState { get; init; }

    /// <summary>是否需要测试。</summary>
    public bool RequiresTests { get; init; }

    /// <summary>是否涉及构建。</summary>
    public bool InvolvesBuild { get; init; }

    /// <summary>预估时长（分钟）。</summary>
    public int EstimatedMinutes { get; init; }

    /// <summary>是否有外部副作用（网络/远端/第三方）。</summary>
    public bool ExternalSideEffects { get; init; }
}

/// <summary>分级结果：级别 + N/R/安全阀 + 可审计理由。</summary>
public sealed record AcsGradeResult(
    AcsTier Tier,
    string TierName,
    int CandidateCount,
    int RepeatEvaluations,
    int MaxRounds,
    int MaxMinutes,
    string Reason);

/// <summary>
/// ACS 任务分级器：规则判定 + 拿不准取高一级。
/// 判定顺序：T3 触发条件 → T2 触发条件 → T1 触发条件 → T0 兜底。
/// </summary>
public sealed class TaskGrader
{
    private readonly AcsThresholds _thresholds;

    /// <summary>构造（默认用 AcsThresholds.Default 内嵌阈值）。</summary>
    public TaskGrader(AcsThresholds? thresholds = null)
    {
        _thresholds = thresholds ?? AcsThresholds.Default;
    }

    /// <summary>对任务画像分级，返回级别与参数（理由可审计）。</summary>
    public AcsGradeResult Grade(AcsTaskProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var reasons = new List<string>();
        var tier = DecideTier(profile, reasons);
        var tierName = tier.ToString();
        var spec = _thresholds.Tier(tierName);

        return new AcsGradeResult(
            tier,
            tierName,
            spec.N,
            spec.R,
            spec.MaxRounds,
            spec.MaxMinutes,
            string.Join("; ", reasons));
    }

    private static AcsTier DecideTier(AcsTaskProfile p, List<string> reasons)
    {
        // T3：任一命中即 T3
        if (p.Irreversible)
        {
            reasons.Add("不可逆操作（删除/迁移/部署/推送）→ T3");
            return AcsTier.T3;
        }

        if (p.Architectural)
        {
            reasons.Add("架构级变更 → T3");
            return AcsTier.T3;
        }

        if (p.SecurityRelevant)
        {
            reasons.Add("安全相关 → T3");
            return AcsTier.T3;
        }

        if (p.FileCount >= 5 && p.CrossModule)
        {
            reasons.Add($"≥5 文件跨模块（{p.FileCount} 文件）→ T3");
            return AcsTier.T3;
        }

        // T2：任一命中即 T2
        if (p.FileCount > 1)
        {
            reasons.Add($"多文件（{p.FileCount}）→ T2");
            return AcsTier.T2;
        }

        if (p.PersistentState)
        {
            reasons.Add("有持久状态 → T2");
            return AcsTier.T2;
        }

        if (p.RequiresTests)
        {
            reasons.Add("需测试 → T2");
            return AcsTier.T2;
        }

        if (p.InvolvesBuild)
        {
            reasons.Add("涉及构建 → T2");
            return AcsTier.T2;
        }

        // T1：单文件、可逆、短时长、无外部副作用
        if (p.IsReadOnly)
        {
            reasons.Add("只读 → T0");
            return AcsTier.T0;
        }

        if (p.FileCount <= 1 && !p.ExternalSideEffects && p.EstimatedMinutes <= 15)
        {
            reasons.Add("单文件/可逆/≤15min/无外部副作用 → T1");
            return AcsTier.T1;
        }

        // 拿不准取高一级（ACS 纪律）：落到这里说明信号不典型，保守升 T2。
        reasons.Add("信号不典型，拿不准取高一级 → T2");
        return AcsTier.T2;
    }
}
