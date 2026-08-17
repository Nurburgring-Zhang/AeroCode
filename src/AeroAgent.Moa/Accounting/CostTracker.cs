using System;
using AeroAgent.Moa.Profiles;

namespace AeroAgent.Moa.Accounting;

/// <summary>
/// 成本核算。只对画像中已知价格的模型计费；价格未知返回 null（UI 显示"未计价"），
/// 绝不拿别的模型的价格估算。
/// </summary>
public static class CostTracker
{
    /// <summary>按画像价格计算一次调用的美元成本；价格未知返回 null。</summary>
    public static double? Estimate(ModelProfile? profile, int tokensIn, int tokensOut)
    {
        if (profile?.CostPerMIn is not { } priceIn || profile.CostPerMOut is not { } priceOut)
        {
            return null;
        }

        if (priceIn < 0 || priceOut < 0 || tokensIn < 0 || tokensOut < 0)
        {
            return null;
        }

        return tokensIn / 1_000_000.0 * priceIn + tokensOut / 1_000_000.0 * priceOut;
    }
}

/// <summary>
/// 单轮预算。策略在发起每个子调用前检查：已花费（真实用量累计）超过上限即中止，
/// 如实报告"预算超限"，不静默继续。未配置上限（null）= 不限制。
/// 注意：预算基于 provider 返回的真实 usage；流式响应若不带 usage 则不计费。
/// </summary>
public sealed class TurnBudget
{
    private readonly object _sync = new();

    public TurnBudget(double? maxUsdPerTurn)
    {
        if (maxUsdPerTurn is { } v && v <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxUsdPerTurn), "budget must be positive or null");
        }

        MaxUsd = maxUsdPerTurn;
    }

    public double? MaxUsd { get; }

    public double SpentUsd
    {
        get { lock (_sync) return _spentUsd; }
    }

    /// <summary>是否还有预算发起下一次调用。</summary>
    public bool HasBudget
    {
        get
        {
            lock (_sync)
            {
                return MaxUsd is not { } max || _spentUsd < max;
            }
        }
    }

    /// <summary>累计一次真实花费。返回累计后是否仍在预算内。</summary>
    public bool AddActual(double usd)
    {
        if (usd < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(usd));
        }

        lock (_sync)
        {
            _spentUsd += usd;
            return MaxUsd is not { } max || _spentUsd < max;
        }
    }

    private double _spentUsd;
}
