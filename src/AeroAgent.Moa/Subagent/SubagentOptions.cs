// Copyright (c) AeroCode
// 子代理运行配置（批次 B G1）。Settings 的 Subagent 节（Enabled/MaxDepth/MaxParallel）
// 由主控 B2 统一接入 AppSettings；本类是构造参数注入的配置载体——组合根把设置值
// 映射进来，SubAgentRunner 不直接读全局设置（所有权边界）。
namespace AeroAgent.Moa.Subagent;

/// <summary>
/// 子代理派发配置。MaxDepth 被 <see cref="SubAgentSpec.MaxDepth"/>（硬上限 4）钳制；
/// MaxParallel 是同时运行的子代理实例上限，超限派发排队（信号量）等待空闲槽位；
/// ParallelEnabled 是并行开关（C-GATE）：关闭 = 降级单 agent（并行上限钳制为 1）。
/// </summary>
public sealed class SubagentOptions
{
    /// <summary>是否启用子代理派发。false 时 LaunchAsync 诚实失败（InvalidOperationException）。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>深度上限（层数含自身）。构造时被 <see cref="SubAgentSpec.MaxDepth"/> 钳制。</summary>
    public int MaxDepth { get; set; } = SubAgentSpec.MaxDepth;

    /// <summary>同时运行的子代理实例数上限（≥1）。超限的派发进入队列等待。</summary>
    public int MaxParallel { get; set; } = 2;

    /// <summary>
    /// 并行开关（A1，契约 C-GATE）。false = 降级单 agent：<see cref="EffectiveMaxParallel"/>
    /// 钳制为 1（派发仍可用，但不并行）。Moa 层默认 true = 保持现行为（并行可用）；
    /// 默认值翻转只允许发生在组合根/设置层（R1 缝合窗口 #13）。
    /// </summary>
    public bool ParallelEnabled { get; set; } = true;

    /// <summary>生效的深度上限（构造后计算；≤ <see cref="SubAgentSpec.MaxDepth"/> 硬上限）。</summary>
    public int EffectiveMaxDepth => Math.Clamp(MaxDepth, 1, SubAgentSpec.MaxDepth);

    /// <summary>生效的并行上限：并行开关关闭时钳制为 1，否则取 MaxParallel（≥1）。</summary>
    public int EffectiveMaxParallel => ParallelEnabled ? Math.Max(1, MaxParallel) : 1;
}
