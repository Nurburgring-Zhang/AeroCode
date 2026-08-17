// Copyright (c) AeroCode V3.0
// Compactor — DSH + Hermes context compression.
using AeroCode.AI.Models;
using AeroCode.Harness.EventBus;

namespace AeroCode.Harness.Compaction;

/// <summary>
/// Compaction strategy: how to reduce the conversation context.
/// </summary>
public enum CompactionStrategy
{
    /// <summary>Keep recent N messages, summarize the rest via LLM.</summary>
    SlidingWindow,
    /// <summary>Keep all messages but LLM-summarize long tool results.</summary>
    LlmSummarize,
    /// <summary>Drop oldest messages beyond a token budget (no LLM call).</summary>
    TruncateOldest,
}

/// <summary>
/// Approximate token counter (4 chars ≈ 1 token — common English heuristic).
/// </summary>
public static class TokenCounter
{
    public static int ApproxTokens(string text) => string.IsNullOrEmpty(text) ? 0 : text.Length / 4;

    public static int ApproxTokens(ChatMessage msg)
    {
        var s = msg.Content ?? string.Empty;
        return ApproxTokens(s);
    }

    public static int ApproxTokens(IEnumerable<ChatMessage> messages) => messages.Sum(ApproxTokens);
}

/// <summary>
/// Compactor — reduces context size while preserving coherence.
/// Combines DSH's compaction package with Hermes sliding window + LLM summary.
/// </summary>
public sealed class Compactor
{
    private readonly EventBus.EventBus _eventBus;
    private readonly CompactionStrategy _strategy;
    private readonly int _triggerThreshold;  // e.g. 50% of max tokens
    private readonly int _keepRecentMessages;

    public Compactor(
        EventBus.EventBus eventBus,
        CompactionStrategy strategy = CompactionStrategy.SlidingWindow,
        int triggerThresholdPercent = 50,
        int keepRecentMessages = 10)
    {
        _eventBus = eventBus;
        _strategy = strategy;
        _triggerThreshold = triggerThresholdPercent;
        _keepRecentMessages = keepRecentMessages;
    }

    public int TriggerThresholdPercent => _triggerThreshold;
    public CompactionStrategy Strategy => _strategy;

    /// <summary>Check if compaction should be triggered.</summary>
    public bool ShouldCompact(int currentTokens, int maxTokens)
    {
        if (maxTokens <= 0) return false;
        return currentTokens * 100 / maxTokens >= _triggerThreshold;
    }

    /// <summary>Compact a message list according to the active strategy.</summary>
    public CompactionResult Compact(IReadOnlyList<ChatMessage> messages, int maxTokens, Func<string, Task<string>>? summarizer = null)
    {
        var originalTokens = TokenCounter.ApproxTokens(messages);
        if (!ShouldCompact(originalTokens, maxTokens))
            return new CompactionResult(false, messages, originalTokens, originalTokens, "Below threshold");

        IReadOnlyList<ChatMessage> compacted;
        int compactedTokens;

        switch (_strategy)
        {
            case CompactionStrategy.TruncateOldest:
                (compacted, compactedTokens) = TruncateOldest(messages, maxTokens);
                break;
            case CompactionStrategy.LlmSummarize:
                (compacted, compactedTokens) = LlmSummarizeIfPossible(messages, maxTokens, summarizer);
                break;
            case CompactionStrategy.SlidingWindow:
            default:
                (compacted, compactedTokens) = SlidingWindow(messages);
                break;
        }

        _eventBus.Publish(new CompactionTriggeredEvent(originalTokens, compactedTokens, DateTime.UtcNow));
        return new CompactionResult(true, compacted, originalTokens, compactedTokens, "Compacted");
    }

    private (IReadOnlyList<ChatMessage>, int) SlidingWindow(IReadOnlyList<ChatMessage> messages)
    {
        if (messages.Count <= _keepRecentMessages)
            return (messages, TokenCounter.ApproxTokens(messages));

        // Keep the first system message (if any) + the last N messages.
        var system = messages.Take(1).Where(m => m.Role == "system").ToList();
        var recent = messages.TakeLast(_keepRecentMessages).ToList();
        var combined = system.Concat(recent).Distinct().ToList();
        return (combined, TokenCounter.ApproxTokens(combined));
    }

    private (IReadOnlyList<ChatMessage>, int) TruncateOldest(IReadOnlyList<ChatMessage> messages, int maxTokens)
    {
        var result = new List<ChatMessage>(messages);
        while (TokenCounter.ApproxTokens(result) > maxTokens && result.Count > 1)
        {
            // Don't drop the first system message.
            var idx = result.FindIndex(m => m.Role != "system");
            if (idx < 0 || idx >= result.Count - 1) break;
            result.RemoveAt(idx);
        }
        return (result, TokenCounter.ApproxTokens(result));
    }

    private (IReadOnlyList<ChatMessage>, int) LlmSummarizeIfPossible(IReadOnlyList<ChatMessage> messages, int maxTokens, Func<string, Task<string>>? summarizer)
    {
        if (summarizer is null)
        {
            // No summarizer available: fall back to sliding window.
            return SlidingWindow(messages);
        }

        // Summarize the middle portion of the conversation, keep first + last.
        if (messages.Count < 4) return (messages, TokenCounter.ApproxTokens(messages));
        var first = messages.Take(1).ToList();
        var last = messages.TakeLast(_keepRecentMessages).ToList();
        var middle = messages.Skip(1).Take(messages.Count - 1 - _keepRecentMessages).ToList();

        var middleText = string.Join("\n", middle.Select(m => $"[{m.Role}] {m.Content}"));
        var summary = summarizer(middleText).GetAwaiter().GetResult();

        var systemMsg = first.FirstOrDefault(m => m.Role == "system");
        var summaryMsg = new ChatMessage { Role = "system", Content = $"[Conversation summary so far]\n{summary}" };
        var combined = new List<ChatMessage>();
        if (systemMsg is not null) combined.Add(systemMsg);
        combined.Add(summaryMsg);
        combined.AddRange(last);
        return (combined, TokenCounter.ApproxTokens(combined));
    }
}

/// <summary>Result of a compaction operation.</summary>
public sealed record CompactionResult(
    bool DidCompact,
    IReadOnlyList<ChatMessage> Messages,
    int OriginalTokens,
    int CompactedTokens,
    string? Reason);
