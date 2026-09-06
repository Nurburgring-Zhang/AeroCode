// Copyright (c) AeroCode
// A3 ContextCurator 契约（C-CURATE，R1 波次 β 定义，α 按此接线）：
// 输入 = 对话区消息 + 冻结前缀边界 + 水位阈值；
// 语义 = 只压对话区，冻结前缀（system/tools/命中 skill 的稳定段）一字不动（prompt 缓存不受损是硬语义）；
// 触发 = 每轮结束按上下文水位超阈值（WorkerRunner 每轮末 Curator != null 且超阈值 → 调用，α 唯一接线点）；
// 输出 = 压缩后对话区 + 外置状态块（≤400 字摘要模板）+ 溢出事实清单。
using AeroCode.AI.Models;

namespace AeroAgent.Moa.Curation;

/// <summary>
/// 上下文策展器。实现必须是 Moa 侧纯逻辑：不引用 provider、不落库、不发事件；
/// LLM 摘要函数经 <see cref="CurationOptions.Summarizer"/> 注入（可用
/// Harness 的 ContextCuratorLlmAdapter 桥接），缺失或失败时降级为确定性模板。
/// </summary>
public interface IContextCurator
{
    /// <summary>
    /// 策展一次对话区。
    /// 水位未超阈值（或阈值 ≤0 = 关闭）→ 原样返回（<see cref="CurationResult.DidCurate"/> = false，
    /// 外置状态块为空串，对话区与输入同实例）。
    /// 超阈值 → 只压缩 <see cref="CurationInput.Conversation"/> 中冻结前缀之后的对话区：
    /// 冻结前缀逐字逐序原样保留（消息实例直接复用，绝不重建/裁剪/重排）；
    /// 压缩区的事实经外置状态块（≤400 字，敏感形态过滤）与溢出事实清单承载。
    /// </summary>
    CurationResult Curate(CurationInput input);

    /// <summary>
    /// L2（R2 波次 γ）：真异步策展入口。默认实现委托同步 <see cref="Curate"/>（既有实现零破坏）；
    /// <see cref="ContextCurator"/> 覆写为真异步（LLM 摘要 await，无线程池阻塞）。
    /// 生产链路（WorkerRunner 每轮末策展）应调用本方法。
    /// </summary>
    ValueTask<CurationResult> CurateAsync(CurationInput input, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Curate(input));
}
