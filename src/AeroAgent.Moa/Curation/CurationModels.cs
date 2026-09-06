// Copyright (c) AeroCode
// A3 ContextCurator 数据模型（C-CURATE，R1 波次 β）。纯数据记录，无行为。
using AeroCode.AI.Models;
using AeroCode.Harness.Compaction;

namespace AeroAgent.Moa.Curation;

/// <summary>
/// LLM 摘要函数抽象：输入待摘要文本，输出摘要文本。
/// 与 Harness Compactor 接受的 Func&lt;string, Task&lt;string&gt;&gt; 同形，可经
/// ContextCuratorLlmAdapter 桥接真实 LLM。实现侧自行负责超时；执行异常由策展器
/// 捕获并降级为确定性模板（异常消息不得进入策展输出，防凭据泄漏）。
/// </summary>
public delegate Task<string> CurationSummarizer(string conversationText);

/// <summary>策展配置。默认值即安全值：水位 8000 token、保留最近 8 条、状态块 ≤400 字。</summary>
public sealed record CurationOptions
{
    /// <summary>水位阈值（token 估算，4 字符 ≈ 1 token 既有口径）。≤0 = 关闭策展（行为与未装配一致）。</summary>
    public int WatermarkThresholdTokens { get; init; } = 8000;

    /// <summary>压缩时保留的最近对话条数（滑动窗口思想，与 Compactor keepRecentMessages 同语义）。</summary>
    public int KeepRecentMessages { get; init; } = 8;

    /// <summary>外置状态块字符硬上限（≤400 字摘要模板纪律，超限逐级降级后硬截断）。</summary>
    public int MaxStateBlockChars { get; init; } = 400;

    /// <summary>
    /// 压缩策略：复用 Harness Compactor 既有枚举语义——
    /// LlmSummarize（默认）= 保留窗口 + 压缩区摘要（有 LLM 用 LLM，无则确定性模板）；
    /// SlidingWindow = 保留窗口 + 压缩区确定性摘要；TruncateOldest = 仅丢弃压缩区，不做摘要。
    /// </summary>
    public CompactionStrategy Strategy { get; init; } = CompactionStrategy.LlmSummarize;

    /// <summary>LLM 摘要函数（可选）。null 或执行异常 → 降级为确定性模板（无 LLM 也可策展）。</summary>
    public CurationSummarizer? Summarizer { get; init; }
}

/// <summary>一次策展请求：对话区消息 + 冻结前缀边界 + 水位阈值。</summary>
public sealed record CurationInput
{
    /// <summary>对话区消息（冻结前缀含在前缀区间内一并传入；策展器保证其原样保留）。</summary>
    public required IReadOnlyList<ChatMessage> Conversation { get; init; }

    /// <summary>
    /// 冻结前缀边界：Conversation 开头 N 条为一字不动的稳定段
    /// （system/tools/命中 skill 的稳定段）。越界自动收敛到 [0, Conversation.Count]。
    /// </summary>
    public int FrozenPrefixCount { get; init; }

    /// <summary>本次水位阈值（token 估算）；null = 用 <see cref="CurationOptions.WatermarkThresholdTokens"/>。≤0 = 关闭。</summary>
    public int? WatermarkThresholdTokens { get; init; }
}

/// <summary>策展结果：压缩后对话区 + 外置状态块 + 溢出事实清单。</summary>
public sealed record CurationResult
{
    /// <summary>
    /// 压缩后对话区。触发时 = 冻结前缀（原样）+ 外置状态块（system 消息，紧随前缀）+ 保留窗口；
    /// 未触发时与输入同实例。
    /// </summary>
    public required IReadOnlyList<ChatMessage> Conversation { get; init; }

    /// <summary>外置状态块文本（≤ MaxStateBlockChars；未触发时为空串）。与 Conversation 内状态块消息内容一致。</summary>
    public string ExternalStateBlock { get; init; } = string.Empty;

    /// <summary>
    /// 溢出事实清单：压缩区断言、且冻结前缀与保留窗口均无对应证据的事实
    /// （逐条人类可读，承载"离开上下文但可能仍被引用"的信息）。
    /// </summary>
    public IReadOnlyList<string> OverflowedFacts { get; init; } = Array.Empty<string>();

    /// <summary>是否实际发生了压缩。</summary>
    public bool DidCurate { get; init; }

    /// <summary>压缩前对话区 token 估算。</summary>
    public int OriginalTokens { get; init; }

    /// <summary>压缩后对话区 token 估算。</summary>
    public int CuratedTokens { get; init; }

    /// <summary>未触发/降级原因（触发压缩成功时为 null）。</summary>
    public string? Reason { get; init; }
}
