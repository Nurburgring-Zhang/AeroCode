using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AeroCode.AI.Models;

namespace AeroAgent.Conversation.Orchestration;

/// <summary>流聚合结果。</summary>
public sealed record AggregatedStream(
    string Content,
    string? ReasoningContent,
    string? FinishReason,
    int ElapsedMs);

/// <summary>
/// 流聚合器：消费 provider 的 <see cref="ChatChunk"/> 流，累积正文/推理内容，
/// 逐块回调增量（UI 打字机），返回聚合结果。纯转换逻辑，无 IO、无状态。
/// </summary>
public static class StreamAggregator
{
    /// <summary>
    /// 消费流并聚合。
    /// </summary>
    /// <param name="chunks">provider 流。</param>
    /// <param name="onDelta">正文增量回调（可为空）。</param>
    /// <param name="onReasoningDelta">推理增量回调（可为空）。</param>
    /// <param name="ct">取消令牌。</param>
    public static async Task<AggregatedStream> ConsumeAsync(
        IAsyncEnumerable<ChatChunk> chunks,
        Action<string>? onDelta,
        Action<string>? onReasoningDelta,
        CancellationToken ct)
    {
        var content = new StringBuilder();
        var reasoning = new StringBuilder();
        string? finishReason = null;
        var sw = Stopwatch.StartNew();

        await foreach (var chunk in chunks.WithCancellation(ct))
        {
            if (!string.IsNullOrEmpty(chunk.DeltaContent))
            {
                content.Append(chunk.DeltaContent);
                onDelta?.Invoke(chunk.DeltaContent);
            }

            if (!string.IsNullOrEmpty(chunk.DeltaReasoning))
            {
                reasoning.Append(chunk.DeltaReasoning);
                onReasoningDelta?.Invoke(chunk.DeltaReasoning);
            }

            if (!string.IsNullOrEmpty(chunk.FinishReason))
            {
                finishReason = chunk.FinishReason;
            }
        }

        sw.Stop();
        return new AggregatedStream(
            content.ToString(),
            reasoning.Length > 0 ? reasoning.ToString() : null,
            finishReason,
            (int)sw.ElapsedMilliseconds);
    }
}
