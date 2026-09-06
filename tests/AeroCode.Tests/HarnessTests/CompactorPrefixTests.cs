// Copyright (c) AeroCode
// Compactor 前缀感知压缩重载单测（R1 波次 β）：
// 冻结前缀保留 / 预算语义（前缀占满不硬压）/ 与既有重载行为一致性。
using AeroCode.AI.Models;
using AeroCode.Harness.Compaction;
using AeroCode.Harness.EventBus;
using Xunit;

namespace AeroCode.Tests.HarnessTests;

public sealed class CompactorPrefixTests
{
    private static ChatMessage Msg(string role, string content) => new() { Role = role, Content = content };

    [Fact]
    public void Compact_FrozenPrefix_PrefixPreserved()
    {
        var prefix = new List<ChatMessage>
        {
            Msg("system", "稳定 system 段"),
            Msg("user", "命中 skill 的稳定段"),
        };
        var messages = new List<ChatMessage>(prefix);
        for (var i = 0; i < 60; i++)
        {
            messages.Add(Msg("user", new string('x', 100)));
        }

        var c = new Compactor(new EventBus(), CompactionStrategy.TruncateOldest, triggerThresholdPercent: 1);
        var result = c.Compact(messages, maxTokens: 500, frozenPrefixCount: 2);

        Assert.True(result.DidCompact);
        // 冻结前缀逐条同实例、同顺序。
        Assert.Same(prefix[0], result.Messages[0]);
        Assert.Same(prefix[1], result.Messages[1]);
        Assert.True(result.CompactedTokens < result.OriginalTokens);
    }

    [Fact]
    public void Compact_FrozenPrefixZero_MatchesLegacyOverload()
    {
        var messages = Enumerable.Range(0, 40).Select(_ => Msg("user", new string('x', 100))).ToList();
        var c = new Compactor(new EventBus(), CompactionStrategy.TruncateOldest, triggerThresholdPercent: 1);

        var legacy = c.Compact(messages, maxTokens: 300);
        var prefixed = c.Compact(messages, maxTokens: 300, frozenPrefixCount: 0);

        Assert.Equal(legacy.DidCompact, prefixed.DidCompact);
        Assert.Equal(legacy.CompactedTokens, prefixed.CompactedTokens);
        Assert.Equal(legacy.Messages.Count, prefixed.Messages.Count);
    }

    [Fact]
    public void Compact_PrefixConsumesBudget_NoDialogueCompaction()
    {
        var messages = new List<ChatMessage>
        {
            Msg("system", new string('p', 3600)), // ≈900 tokens
            Msg("user", new string('x', 200)),    // ≈50 tokens
        };
        var c = new Compactor(new EventBus(), CompactionStrategy.TruncateOldest, triggerThresholdPercent: 50);
        var result = c.Compact(messages, maxTokens: 1000, frozenPrefixCount: 1);

        // 总水位 950/1000 ≥ 50% 过闸；对话区 50 ≤ 1000−900 → 无安全压缩空间（不动前缀优先）。
        Assert.False(result.DidCompact);
        Assert.Same(messages, result.Messages);
    }

    [Fact]
    public void Compact_PrefixCountBeyondConversation_FreezesAll_NoCompaction()
    {
        var messages = Enumerable.Range(0, 40).Select(_ => Msg("user", new string('x', 100))).ToList();
        var c = new Compactor(new EventBus(), CompactionStrategy.TruncateOldest, triggerThresholdPercent: 1);

        var clamped = c.Compact(messages, maxTokens: 300, frozenPrefixCount: 999);

        // 越界 clamp 到消息总数 = 全冻结 → 对话区为空 →「前缀占满不硬压」：不压、原实例返回。
        Assert.False(clamped.DidCompact);
        Assert.Same(messages, clamped.Messages);
        Assert.Equal(clamped.OriginalTokens, clamped.CompactedTokens);
    }

    [Fact]
    public void Compact_SlidingWindow_FrozenPrefix_KeepsRecent()
    {
        var messages = new List<ChatMessage>
        {
            Msg("system", "稳定前缀"),
            Msg("user", new string('u', 2000)),
            Msg("assistant", new string('a', 2000)),
            Msg("user", "最近问题"),
        };
        var c = new Compactor(new EventBus(), CompactionStrategy.SlidingWindow, keepRecentMessages: 2, triggerThresholdPercent: 1);
        var result = c.Compact(messages, maxTokens: 100, frozenPrefixCount: 1);

        Assert.True(result.DidCompact);
        Assert.Same(messages[0], result.Messages[0]);          // 前缀保留
        Assert.Equal("最近问题", result.Messages[^1].Content);  // 最新上下文保留
        Assert.True(result.Messages.Count < messages.Count);
    }
}
