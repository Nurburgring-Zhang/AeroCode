// Copyright (c) AeroCode
// ApprovalCircuitBreaker — 审批熔断器（批次 B G3，builder-β）。
// IPermissionBroker 装饰器：正常时把 Ask 送"快速通道"（低风险自动采纳 broker；
// 未装配自动采纳 broker 时快速通道即真实弹窗 broker，行为与无熔断完全一致）。
// 会话级两个阈值（构造注入）任一触发即熔断：
//   1. 连续批准计数达到 maxConsecutiveApprovals（人工 Deny 会重置计数——链被打断）；
//   2. 会话累计成本（RecordCost，来自真实 usage 计费）达到 maxSessionCostUsd。
// 熔断后所有裁决强制走真实人工弹窗并发布一次 ApprovalCircuitBrokenEvent；
// 熔断状态会话内锁存（人工再批准也不解熔），Reset 只应在新会话时调用。
using AeroAgent.Moa.Tools;
using AeroCode.Harness.EventBus;
using AeroCode.Harness.Permission;

namespace AeroAgent.Moa.Safety;

public sealed class ApprovalCircuitBreaker : IPermissionBroker
{
    private readonly IPermissionBroker _interactive;
    private readonly IPermissionBroker? _autoAdopt;
    private readonly EventBus? _eventBus;
    private readonly string _sessionId;
    private readonly int _maxConsecutiveApprovals;
    private readonly double _maxSessionCostUsd;
    private readonly object _sync = new();
    private int _consecutiveAllows;
    private double _costUsd;
    private bool _broken;
    private string? _brokenReason;

    /// <param name="interactiveBroker">真实人工授权通道（弹窗 broker）；熔断后强制走这里。</param>
    /// <param name="autoAdoptBroker">可空快速通道（低风险自动采纳）。null = 无自动采纳，行为不变。</param>
    /// <param name="eventBus">熔断沿发布 <see cref="ApprovalCircuitBrokenEvent"/>。</param>
    /// <param name="sessionId">事件归属会话（审计用）。</param>
    /// <param name="maxConsecutiveApprovals">连续批准阈值，≥1。</param>
    /// <param name="maxSessionCostUsd">会话累计成本阈值（美元），&gt;0。</param>
    public ApprovalCircuitBreaker(
        IPermissionBroker interactiveBroker,
        IPermissionBroker? autoAdoptBroker = null,
        EventBus? eventBus = null,
        string sessionId = "default",
        int maxConsecutiveApprovals = 10,
        double maxSessionCostUsd = 5.0)
    {
        _interactive = interactiveBroker ?? throw new ArgumentNullException(nameof(interactiveBroker));
        _autoAdopt = autoAdoptBroker;
        _eventBus = eventBus;
        _sessionId = string.IsNullOrWhiteSpace(sessionId) ? "default" : sessionId;
        if (maxConsecutiveApprovals < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxConsecutiveApprovals), "threshold must be >= 1");
        }

        if (double.IsNaN(maxSessionCostUsd) || maxSessionCostUsd <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxSessionCostUsd), "cost threshold must be positive");
        }

        _maxConsecutiveApprovals = maxConsecutiveApprovals;
        _maxSessionCostUsd = maxSessionCostUsd;
    }

    /// <summary>当前是否已熔断（诊断用）。</summary>
    public bool IsBroken
    {
        get { lock (_sync) return _broken; }
    }

    /// <summary>当前连续批准计数（诊断用）。</summary>
    public int ConsecutiveApprovals
    {
        get { lock (_sync) return _consecutiveAllows; }
    }

    /// <summary>会话累计成本（诊断用）。</summary>
    public double CostUsd
    {
        get { lock (_sync) return _costUsd; }
    }

    /// <summary>熔断原因（未熔断为 null）。</summary>
    public string? BrokenReason
    {
        get { lock (_sync) return _brokenReason; }
    }

    /// <summary>新会话重置（计数/成本/熔断状态全部清零）。</summary>
    public void Reset()
    {
        lock (_sync)
        {
            _consecutiveAllows = 0;
            _costUsd = 0;
            _broken = false;
            _brokenReason = null;
        }
    }

    /// <summary>
    /// 累计一次真实会话成本（来自 CostTracker 的真实计费，绝不估算）；达到阈值即熔断。
    /// 返回累计后的会话总成本。
    /// </summary>
    public double RecordCost(double usd)
    {
        if (double.IsNaN(usd) || usd < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(usd), "cost must be a non-negative number");
        }

        string? tripReason = null;
        double total;
        lock (_sync)
        {
            _costUsd += usd;
            total = _costUsd;
            if (!_broken && _costUsd >= _maxSessionCostUsd)
            {
                _broken = true;
                tripReason = _brokenReason =
                    $"session cost {total:0.##} USD reached threshold {_maxSessionCostUsd:0.##}";
            }
        }

        if (tripReason is not null)
        {
            Publish(tripReason);
        }

        return total;
    }

    /// <inheritdoc />
    public async ValueTask<PermissionDecision> ResolveAsync(
        string toolName,
        IReadOnlyDictionary<string, object?>? args,
        CancellationToken ct)
    {
        string? tripReason = null;
        bool escalate;
        lock (_sync)
        {
            if (_broken)
            {
                escalate = true; // 已熔断：锁存，全部走人工
            }
            else if (_consecutiveAllows >= _maxConsecutiveApprovals)
            {
                _broken = true;
                tripReason = _brokenReason =
                    $"consecutive approvals reached {_maxConsecutiveApprovals}";
                escalate = true;
            }
            else
            {
                escalate = false;
            }
        }

        if (tripReason is not null)
        {
            Publish(tripReason);
        }

        var path = escalate ? _interactive : (_autoAdopt ?? _interactive);
        var decision = await path.ResolveAsync(toolName, args, ct).ConfigureAwait(false);

        // 契约：broker 只允许 Allow/Deny；Ask 视为 Deny（不得借道静默放行）。
        if (decision == PermissionDecision.Ask)
        {
            decision = PermissionDecision.Deny;
        }

        if (!escalate)
        {
            lock (_sync)
            {
                if (decision == PermissionDecision.Allow)
                {
                    _consecutiveAllows++;
                }
                else if (decision == PermissionDecision.Deny)
                {
                    _consecutiveAllows = 0; // 人工拒绝打断连续链
                }
            }
        }

        return decision;
    }

    private void Publish(string reason) =>
        _eventBus?.Publish(new ApprovalCircuitBrokenEvent(_sessionId, reason, DateTime.UtcNow));
}
