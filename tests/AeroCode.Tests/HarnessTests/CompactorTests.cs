// Copyright (c) AeroCode V3.0
// Compactor tests.
using AeroCode.AI.Models;
using AeroCode.Harness.Compaction;
using AeroCode.Harness.EventBus;
using Xunit;

namespace AeroCode.Tests.HarnessTests;

public class CompactorTests
{
    [Fact]
    public void ShouldCompact_BelowThreshold_False()
    {
        var c = new Compactor(new EventBus());
        Assert.False(c.ShouldCompact(100, 1000));
    }

    [Fact]
    public void ShouldCompact_AtThreshold_True()
    {
        var c = new Compactor(new EventBus(), triggerThresholdPercent: 50);
        Assert.True(c.ShouldCompact(500, 1000));
    }

    [Fact]
    public void ShouldCompact_AboveThreshold_True()
    {
        var c = new Compactor(new EventBus());
        Assert.True(c.ShouldCompact(900, 1000));
    }

    [Fact]
    public void Compact_SlidingWindow_KeepsRecent()
    {
        var c = new Compactor(new EventBus(), CompactionStrategy.SlidingWindow, keepRecentMessages: 3);
        var messages = Enumerable.Range(0, 10)
            .Select(i => new ChatMessage { Role = "user", Content = new string('x', 1000) })
            .ToList();

        // maxTokens small enough to trigger compaction (10 * 250 = 2500 > 100)
        var result = c.Compact(messages, maxTokens: 100);
        Assert.True(result.DidCompact);
        Assert.True(result.Messages.Count <= 3);  // last 3 (no system in test)
    }

    [Fact]
    public void Compact_BelowThreshold_NoOp()
    {
        var c = new Compactor(new EventBus());
        var messages = new List<ChatMessage>
        {
            new() { Role = "user", Content = "hi" },
        };
        var result = c.Compact(messages, maxTokens: 1_000_000);
        Assert.False(result.DidCompact);
    }

    [Fact]
    public void TokenCounter_ApproxTokens_ApproximatesFourCharsPerToken()
    {
        var text = "a".PadLeft(400, 'a');
        Assert.Equal(100, TokenCounter.ApproxTokens(text));
    }

    [Fact]
    public void TokenCounter_EmptyString_Zero()
    {
        Assert.Equal(0, TokenCounter.ApproxTokens(""));
        Assert.Equal(0, TokenCounter.ApproxTokens((string?)null!));
    }

    [Fact]
    public void Compact_TruncateOldest_DropsMiddleMessages()
    {
        var c = new Compactor(new EventBus(), CompactionStrategy.TruncateOldest, triggerThresholdPercent: 1);
        var messages = Enumerable.Range(0, 100)
            .Select(i => new ChatMessage { Role = "user", Content = new string('x', 100) })
            .ToList();
        var result = c.Compact(messages, maxTokens: 100);
        Assert.True(result.DidCompact);
        Assert.True(result.Messages.Count < 100);
    }
}
