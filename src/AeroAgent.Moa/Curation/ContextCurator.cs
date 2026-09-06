// Copyright (c) AeroCode
// A3 ContextCurator 实现（R1 波次 β）。Moa 侧纯逻辑：不引用 provider、不落库、不发事件。
// 压缩策略复用 Harness Compactor 的 SlidingWindow / LlmSummarize / TruncateOldest 思想；
// 冻结前缀硬语义：前缀区间逐字逐序原样保留（消息实例直接复用，绝不重建）。
// token 估算与 WorkerRunner 同口径（4 字符 ≈ 1 token，tool_calls 按函数名 + 参数 JSON 计）。
using System.Text;
using AeroCode.AI.Models;
using AeroCode.Harness.Compaction;
using AeroCode.Harness.Curation;

namespace AeroAgent.Moa.Curation;

/// <summary>上下文策展器默认实现。可单测：LLM 摘要函数注入，无 LLM 时确定性降级。</summary>
public sealed class ContextCurator : IContextCurator
{
    private readonly CurationOptions _options;

    public ContextCurator(CurationOptions? options = null)
    {
        _options = options ?? new CurationOptions();
    }

    /// <inheritdoc />
    public CurationResult Curate(CurationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        // 旧签名兼容入口（L2 保留）：LLM 摘要等待会阻塞当前线程。生产链路一律走
        // <see cref="CurateAsync"/>（真异步，L2 修复）；本方法仅为既有调用方/测试保行为。
        return CurateCoreAsync(input, CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <inheritdoc />
    public async ValueTask<CurationResult> CurateAsync(CurationInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        return await CurateCoreAsync(input, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>策展核心（L2 改造为真异步）：LLM 摘要 await，不再 GetResult() 同步阻塞。</summary>
    private async Task<CurationResult> CurateCoreAsync(CurationInput input, CancellationToken cancellationToken)
    {
        var conversation = input.Conversation ?? Array.Empty<ChatMessage>();
        var originalTokens = EstimateTokens(conversation);
        var threshold = input.WatermarkThresholdTokens ?? _options.WatermarkThresholdTokens;

        // 关闭 / 未达水位 / 空对话 → 原样返回（输出契约：状态块空串、事实清单空、同实例）。
        if (conversation.Count == 0 || threshold <= 0 || originalTokens < threshold)
        {
            return new CurationResult
            {
                Conversation = conversation,
                OriginalTokens = originalTokens,
                CuratedTokens = originalTokens,
                Reason = threshold <= 0 ? "Curation disabled (threshold <= 0)" : "Below watermark",
            };
        }

        var prefixCount = Math.Clamp(input.FrozenPrefixCount, 0, conversation.Count);

        // 冻结前缀：实例原样复用——一字不动的硬语义（prompt 缓存不受损）。
        var frozen = conversation.Take(prefixCount).ToList();

        // 对话区切分：保留窗口（最近 N 条）+ 压缩区（其余）。
        var dialogueCount = conversation.Count - prefixCount;
        var keep = Math.Clamp(_options.KeepRecentMessages, 1, Math.Max(dialogueCount, 1));
        var windowStart = Math.Max(0, dialogueCount - keep);

        // 窗口头修复：窗口开头若是 tool 应答（其 assistant 携带方在窗口外，留下即孤儿、
        // 请求序列非法）→ 窗口向左扩到携带方，而不是回退孤儿（压缩区只吃完整可弃段）。
        while (windowStart > 0
               && string.Equals(conversation[prefixCount + windowStart].Role, "tool", StringComparison.OrdinalIgnoreCase))
        {
            windowStart--;
        }

        var window = conversation.Skip(prefixCount + windowStart).ToList();
        var evicted = conversation.Skip(prefixCount).Take(windowStart).ToList();

        if (evicted.Count == 0)
        {
            // 对话区全部在保留窗口内，无压缩空间（水位超限风险由 provider 端显式报错兜底）。
            return new CurationResult
            {
                Conversation = conversation,
                OriginalTokens = originalTokens,
                CuratedTokens = originalTokens,
                Reason = "Dialogue zone fits the keep window; nothing to evict",
            };
        }

        // 溢出事实清单：压缩区断言，且保留区（冻结前缀 + 保留窗口）无对应证据。
        var keptEvidence = frozen.Concat(window)
            .Select(m => m.Content ?? string.Empty)
            .Where(c => c.Length > 0)
            .ToList();
        var evictedText = RenderForSummary(evicted);
        var facts = FactAssertionDetector.Detect(evictedText, keptEvidence)
            .Select(a => SensitiveTextScrubber.Scrub(
                $"{KindLabel(a.Kind)}「{a.MatchedText}」：{ExcerptOf(a.Excerpt)}"))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // L2：LLM 摘要真异步等待（异常降级 null；取消向上传播不吞）。
        var llmSummary = await TryLlmSummaryAsync(evictedText, cancellationToken).ConfigureAwait(false);

        var windowTokens = EstimateTokens(frozen.Concat(window).ToList());
        var stateBlock = BuildStateBlock(evicted, evicted.Count, originalTokens, windowTokens, facts, llmSummary);

        // 外置状态块以 system 消息插在冻结前缀之后、保留窗口之前
        // （不拆散窗口内 assistant tool_calls 与 tool 应答的相邻配对）。
        var combined = new List<ChatMessage>(frozen.Count + window.Count + 1);
        combined.AddRange(frozen);
        combined.Add(new ChatMessage { Role = "system", Content = stateBlock });
        combined.AddRange(window);

        return new CurationResult
        {
            Conversation = combined,
            ExternalStateBlock = stateBlock,
            OverflowedFacts = facts,
            DidCurate = true,
            OriginalTokens = originalTokens,
            CuratedTokens = EstimateTokens(combined),
        };
    }

    /// <summary>外置状态块（≤ MaxStateBlockChars）：摘要模板 + 敏感形态过滤 + 逐级降级硬封顶。</summary>
    private string BuildStateBlock(
        IReadOnlyList<ChatMessage> evicted,
        int evictedCount,
        int originalTokens,
        int windowTokens,
        IReadOnlyList<string> facts,
        string? llmSummary)
    {
        var digest = DeterministicDigest(evicted);
        var factText = facts.Count == 0 ? "无" : string.Join("；", facts);

        // 逐级降级：全文 → 去最近动作 → 去 LLM 摘要 → 仅保留事实，最后硬截断。
        foreach (var level in new[] { 3, 2, 1, 0 })
        {
            var sb = new StringBuilder();
            sb.Append("<curated-state>\n");
            sb.Append("[任务状态] 进行中\n");
            sb.Append($"[已完成] 早期对话已外置 {evictedCount} 条（{originalTokens}→{windowTokens} tokens）\n");
            if (level >= 3 && !string.IsNullOrWhiteSpace(llmSummary))
            {
                sb.Append($"[摘要] {llmSummary}\n");
            }

            if (level >= 2)
            {
                sb.Append($"[最近动作] {digest}\n");
            }

            sb.Append($"[溢出事实] {factText}\n");
            sb.Append("</curated-state>");
            var block = SensitiveTextScrubber.Scrub(sb.ToString());
            if (block.Length <= _options.MaxStateBlockChars || level == 0)
            {
                return block.Length <= _options.MaxStateBlockChars
                    ? block
                    : block[.._options.MaxStateBlockChars];
            }
        }

        return string.Empty; // 不可达（level=0 分支必返回）。
    }

    /// <summary>
    /// LLM 摘要（可选注入，L2 改真异步）。TruncateOldest 策略不做摘要；注入缺失/执行异常 → null
    /// （确定性模板降级；异常消息可能含端点/凭据信息，一律不外泄、不打断策展）。
    /// 取消（<see cref="OperationCanceledException"/>）向上传播，不吞——策展等待不阻挠用户取消。
    /// </summary>
    private async Task<string?> TryLlmSummaryAsync(string evictedText, CancellationToken cancellationToken)
    {
        if (_options.Strategy == CompactionStrategy.TruncateOldest || _options.Summarizer is null)
        {
            return null;
        }

        try
        {
            var summary = await _options.Summarizer(evictedText).WaitAsync(cancellationToken).ConfigureAwait(false);
            var scrubbed = SensitiveTextScrubber.Scrub(summary ?? string.Empty).ReplaceLineEndings(" ").Trim();
            return scrubbed.Length == 0 ? null : scrubbed;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>确定性摘要素材：压缩区最后一条非 tool 文本消息的首行（截 60 字）。</summary>
    private static string DeterministicDigest(IReadOnlyList<ChatMessage> evicted)
    {
        var last = evicted.LastOrDefault(m =>
            !string.Equals(m.Role, "tool", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(m.Content));
        if (last is null)
        {
            return "（无文本动作）";
        }

        var line = last.Content!.Trim().ReplaceLineEndings(" ");
        return line.Length <= 60 ? line : line[..60] + "…";
    }

    internal static string RenderForSummary(IReadOnlyList<ChatMessage> messages)
        => string.Join("\n", messages.Select(m => $"[{m.Role}] {m.Content}"));

    internal static string ExcerptOf(string excerpt)
    {
        var s = excerpt.Trim().ReplaceLineEndings(" ");
        return s.Length <= 80 ? s : s[..80] + "…";
    }

    internal static string KindLabel(FactAssertionKind kind) => kind switch
    {
        FactAssertionKind.Date => "日期断言",
        FactAssertionKind.NamedEntity => "实体断言",
        _ => "数字断言",
    };

    /// <summary>4 字符 ≈ 1 token 的既有口径；tool_calls 按函数名 + 参数 JSON 估算（与 WorkerRunner 一致）。</summary>
    internal static int EstimateTokens(IReadOnlyList<ChatMessage> messages)
    {
        var total = 0;
        foreach (var m in messages)
        {
            total += TokenCounter.ApproxTokens(m.Content);
            if (m.ToolCalls is { Count: > 0 })
            {
                foreach (var call in m.ToolCalls)
                {
                    total += TokenCounter.ApproxTokens(call.FunctionName + call.ArgumentsJson);
                }
            }
        }

        return total;
    }
}
