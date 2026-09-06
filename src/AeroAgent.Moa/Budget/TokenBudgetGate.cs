// Copyright (c) AeroCode
// A1 TokenBudgetGate（契约 C-GATE）：mission 级 token 上限闸门的默认实现。
// 线程安全；Exhausted 转换沿一次性外抛事件；降级单 agent 标志随 Exhausted 置位（终态，不回退）。
namespace AeroAgent.Moa.Budget;

/// <summary>
/// mission 级 token 预算闸门（默认实现）。
/// 警告水位 = limitTokens × warningRatio（默认 0.8）；spentTokens ≥ limitTokens 即 Exhausted。
/// 与 Accounting.TurnBudget（单轮美元成本）互补：本类管 mission 生命周期内的 token 总量。
/// </summary>
public sealed class TokenBudgetGate : ITokenBudgetGate
{
    private readonly object _sync = new();
    private readonly double _warningRatio;
    private long _spent;
    private bool _degraded;
    private bool _exhaustedAnnounced;

    /// <param name="limitTokens">mission 级 token 上限（必须为正）。</param>
    /// <param name="warningRatio">警告水位比例，范围 (0, 1]（默认 0.8 = 80%）。</param>
    public TokenBudgetGate(long limitTokens, double warningRatio = 0.8)
    {
        if (limitTokens <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limitTokens), "token budget limit must be positive");
        }

        if (warningRatio is <= 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(warningRatio), "warning ratio must be in (0, 1]");
        }

        LimitTokens = limitTokens;
        _warningRatio = warningRatio;
    }

    /// <inheritdoc/>
    public long LimitTokens { get; }

    /// <inheritdoc/>
    public long SpentTokens
    {
        get { lock (_sync) return _spent; }
    }

    /// <inheritdoc/>
    public bool DegradedToSingleAgent
    {
        get { lock (_sync) return _degraded; }
    }

    /// <inheritdoc/>
    public BudgetState State => ComputeState(SpentTokens);

    /// <inheritdoc/>
    public event Action<BudgetSnapshot>? BudgetExhausted;

    /// <inheritdoc/>
    public BudgetState ReportUsage(long tokens)
    {
        if (tokens < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tokens), "reported usage must be non-negative");
        }

        BudgetSnapshot? fired = null;
        BudgetState state;
        lock (_sync)
        {
            _spent += tokens;
            state = ComputeState(_spent);
            if (state == BudgetState.Exhausted)
            {
                _degraded = true;
                if (!_exhaustedAnnounced)
                {
                    // 转换沿只发一次；快照在锁内取（一致性），事件在锁外抛（防订阅方回调死锁）。
                    _exhaustedAnnounced = true;
                    fired = SnapshotCore(state);
                }
            }
        }

        if (fired is not null)
        {
            BudgetExhausted?.Invoke(fired);
        }

        return state;
    }

    /// <inheritdoc/>
    public BudgetSnapshot Snapshot()
    {
        lock (_sync)
        {
            return SnapshotCore(ComputeState(_spent));
        }
    }

    private BudgetState ComputeState(long spent)
        => spent >= LimitTokens ? BudgetState.Exhausted
            : spent >= LimitTokens * _warningRatio ? BudgetState.Warning
            : BudgetState.Running;

    // 调用方必须持 _sync。
    private BudgetSnapshot SnapshotCore(BudgetState state) => new(state, _spent, LimitTokens, _degraded);
}
