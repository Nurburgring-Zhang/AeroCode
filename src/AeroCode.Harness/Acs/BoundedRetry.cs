// Copyright (c) AeroCode
// BoundedRetry — ACS v2.3.0 有界重试契约。
// 同法重试即空转：重试次数有上限（默认 2），每次重试必须写 retry_reason 与
// delta_from_last（与上次的差异），超限即换路而不是再来一次。
namespace AeroCode.Harness.Acs;

/// <summary>一次重试请求（契约字段齐备才可放行）。</summary>
public sealed record AcsRetryRequest(int AttemptIndex, string RetryReason, string DeltaFromLast);

/// <summary>重试裁决：放行/拒绝 + 理由。</summary>
public sealed record AcsRetryVerdict(bool Allowed, string Reason);

/// <summary>
/// 有界重试裁决器（无状态；状态由调用方的 attempt 计数提供）。
/// </summary>
public sealed class BoundedRetry
{
    private readonly AcsThresholds _thresholds;

    /// <summary>构造（默认内嵌阈值）。</summary>
    public BoundedRetry(AcsThresholds? thresholds = null)
    {
        _thresholds = thresholds ?? AcsThresholds.Default;
    }

    /// <summary>最大重试次数。</summary>
    public int MaxRetries => _thresholds.Retry.MaxRetries;

    /// <summary>
    /// 裁决一次重试是否放行。
    /// AttemptIndex 从 1 起算（第 1 次重试）。契约字段缺失/过短 = 拒绝（无法核验 = 不放行）。
    /// </summary>
    public AcsRetryVerdict Evaluate(AcsRetryRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var retry = _thresholds.Retry;

        if (request.AttemptIndex < 1)
        {
            return new AcsRetryVerdict(false, $"AttemptIndex 必须 ≥1（收到 {request.AttemptIndex}）");
        }

        if (request.AttemptIndex > retry.MaxRetries)
        {
            return new AcsRetryVerdict(false,
                $"重试次数 {request.AttemptIndex} > 上限 {retry.MaxRetries}（有界重试：超限即换路，不是再来一次）");
        }

        if (string.IsNullOrWhiteSpace(request.RetryReason) ||
            request.RetryReason.Trim().Length < retry.MinReasonChars)
        {
            return new AcsRetryVerdict(false,
                $"retry_reason 缺失或少于 {retry.MinReasonChars} 字符（重试必须写明理由）");
        }

        if (string.IsNullOrWhiteSpace(request.DeltaFromLast) ||
            request.DeltaFromLast.Trim().Length < retry.MinDeltaChars)
        {
            return new AcsRetryVerdict(false,
                $"delta_from_last 缺失或少于 {retry.MinDeltaChars} 字符（重试必须写明与上次的差异，同法重试即空转）");
        }

        return new AcsRetryVerdict(true,
            $"重试 {request.AttemptIndex}/{retry.MaxRetries} 放行（理由与差异已声明）");
    }
}
