// Copyright (c) AeroCode
// A2 升级策略（契约 C-LOOP）：两 strike 触发 checkpoint 恢复后，由策略决定去向。
// 默认实现 = 暂停待人（事件外抛）；MissionController 接线归编排者（#13），此处只定契约与默认实现。
// 安全硬门：升级审批一次性消费、不可重放（<see cref="EscalationRequest.TryConsume"/>）。
using AeroCode.Harness.EventBus;

namespace AeroAgent.Moa.LoopGuard;

/// <summary>升级决定。</summary>
public enum EscalationDecision
{
    /// <summary>暂停待人（默认语义）：循环诚实终止为 Failed，等待编排者/人恢复。</summary>
    PauseForHuman,

    /// <summary>带警告继续：策略接管责任，strike 计数清零，循环继续。</summary>
    ContinueWithWarning,

    /// <summary>中止：循环诚实终止为 Failed（不等待恢复）。</summary>
    Abort,
}

/// <summary>升级上下文（checkpoint 恢复动作完成后交付给策略；恢复失败如实为 null/0，不伪造）。</summary>
/// <param name="Turn">触发升级的工具轮序号（0 起）。</param>
/// <param name="Strikes">连续 OffGoal strike 数（≥ 阈值）。</param>
/// <param name="Anchor">任务目标锚。</param>
/// <param name="Reason">升级原因（含偏离判定依据与恢复结果摘要）。</param>
/// <param name="RestoredCheckpointSeq">被恢复的 checkpoint 序号；无 checkpoint/恢复失败为 null。</param>
/// <param name="RestoredFiles">实际恢复的文件数（恢复失败为 0）。</param>
public sealed record EscalationContext(
    int Turn,
    int Strikes,
    GoalAnchor Anchor,
    string Reason,
    long? RestoredCheckpointSeq,
    int RestoredFiles);

/// <summary>
/// 升级凭据：<b>一次性消费</b>——<see cref="TryConsume"/> 首次调用返回 true 并置消费位，
/// 此后永远返回 false（审批不可重放，安全硬门）。编排者（MissionController）凭它受理恢复。
/// </summary>
public sealed class EscalationRequest
{
    private int _consumed;

    public EscalationRequest(string id, int turn, int strikes, string reason, DateTime raisedUtc)
    {
        Id = id;
        Turn = turn;
        Strikes = strikes;
        Reason = reason;
        RaisedUtc = raisedUtc;
    }

    /// <summary>唯一凭据 Id（esc-{guid}）。</summary>
    public string Id { get; }

    /// <summary>触发升级的工具轮序号。</summary>
    public int Turn { get; }

    /// <summary>连续偏离 strike 数。</summary>
    public int Strikes { get; }

    /// <summary>升级原因。</summary>
    public string Reason { get; }

    /// <summary>升级时刻（UTC）。</summary>
    public DateTime RaisedUtc { get; }

    /// <summary>是否已被消费。</summary>
    public bool Consumed => Volatile.Read(ref _consumed) == 1;

    /// <summary>一次性消费：首次 true，此后 false（不可重放）。</summary>
    public bool TryConsume() => Interlocked.Exchange(ref _consumed, 1) == 0;
}

/// <summary>偏离升级事件（EventBus 广播面，观测/UI 用；审批凭据以 EscalationRequest 为准）。</summary>
public sealed record DeviationEscalationEvent(string EscalationId, int Turn, int Strikes, string Reason, DateTime RaisedUtc);

/// <summary>
/// 偏离升级策略。实现约束：Handle 不得抛出（自容）；
/// <see cref="EscalationRaised"/> 订阅方异常不得阻断升级链路。
/// </summary>
public interface IEscalationPolicy
{
    /// <summary>对一次升级给出决定（checkpoint 恢复已先行完成）。</summary>
    EscalationDecision Handle(EscalationContext context);

    /// <summary>升级请求外抛（C# 事件）：MissionController 订阅此接收一次性审批凭据。</summary>
    event Action<EscalationRequest>? EscalationRaised;
}

/// <summary>
/// 默认升级策略：暂停待人——生成唯一一次性 <see cref="EscalationRequest"/>，
/// 经 C# 事件外抛（可另经 EventBus 广播 <see cref="DeviationEscalationEvent"/>），返回 <see cref="EscalationDecision.PauseForHuman"/>。
/// </summary>
public sealed class HumanPauseEscalationPolicy : IEscalationPolicy
{
    private readonly EventBus? _bus;

    /// <param name="bus">可选事件总线（广播观测事件；null = 只走 C# 事件）。</param>
    public HumanPauseEscalationPolicy(EventBus? bus = null) => _bus = bus;

    /// <inheritdoc/>
    public event Action<EscalationRequest>? EscalationRaised;

    /// <inheritdoc/>
    public EscalationDecision Handle(EscalationContext context)
    {
        var request = new EscalationRequest(
            $"esc-{Guid.NewGuid():N}", context.Turn, context.Strikes, context.Reason, DateTime.UtcNow);
        _bus?.Publish(new DeviationEscalationEvent(
            request.Id, request.Turn, request.Strikes, request.Reason, request.RaisedUtc));
        try
        {
            EscalationRaised?.Invoke(request);
        }
        catch
        {
            // 订阅方异常不阻断升级链路（凭据已生成，可由编排者经其他途径受理）。
        }

        return EscalationDecision.PauseForHuman;
    }
}
