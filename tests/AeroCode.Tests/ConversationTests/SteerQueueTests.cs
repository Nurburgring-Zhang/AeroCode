// Copyright (c) AeroCode
// SteerQueue 测试（builder-β）：会话级有界 Channel 的入队/取走/清空、容量背压、
// 会话隔离与非法输入诚实拒绝。
using System;
using System.Linq;
using System.Threading.Tasks;
using AeroAgent.Conversation.Orchestration;
using Xunit;

namespace AeroCode.Tests.ConversationTests;

public sealed class SteerQueueTests
{
    [Fact]
    public void EnqueueThenDrain_FifoOrder()
    {
        var q = new SteerQueue();
        Assert.True(q.TryEnqueue("s1", "先跑测试"));
        Assert.True(q.TryEnqueue("s1", "再看日志"));

        var drained = q.Drain("s1");

        Assert.Equal(2, drained.Count);
        Assert.Equal(new[] { "先跑测试", "再看日志" }, drained);
        Assert.Empty(q.Drain("s1")); // 取走即清
    }

    [Fact]
    public void Drain_UnknownSession_ReturnsEmpty()
    {
        var q = new SteerQueue();
        Assert.Empty(q.Drain("missing"));
    }

    [Fact]
    public void CapacityFull_TryEnqueueFalse_NoSilentDropOfOldest()
    {
        var q = new SteerQueue(capacity: 2);
        Assert.True(q.TryEnqueue("s1", "a"));
        Assert.True(q.TryEnqueue("s1", "b"));
        Assert.False(q.TryEnqueue("s1", "c")); // 满：诚实拒绝（不挤掉旧插话）

        var drained = q.Drain("s1");
        Assert.Equal(new[] { "a", "b" }, drained);
    }

    [Fact]
    public void Sessions_AreIsolated()
    {
        var q = new SteerQueue();
        q.TryEnqueue("s1", "for-one");
        q.TryEnqueue("s2", "for-two");

        Assert.Equal(new[] { "for-one" }, q.Drain("s1"));
        Assert.Equal(new[] { "for-two" }, q.Drain("s2"));
        Assert.Empty(q.Drain("s1"));
    }

    [Fact]
    public void Clear_RemovesPending_AndQueue()
    {
        var q = new SteerQueue();
        q.TryEnqueue("s1", "x");
        q.TryEnqueue("s1", "y");

        Assert.Equal(2, q.Clear("s1"));
        Assert.Empty(q.Drain("s1")); // 清空后无残留
        Assert.Equal(0, q.Clear("s1")); // 幂等
    }

    [Fact]
    public void InvalidInput_HonestlyRejected()
    {
        var q = new SteerQueue();
        Assert.False(q.TryEnqueue("", "text"));
        Assert.False(q.TryEnqueue("s1", ""));
        Assert.False(q.TryEnqueue("s1", "   "));
        Assert.False(q.TryEnqueue(null!, "text"));
        Assert.Equal(0, q.Clear(""));
    }

    [Fact]
    public void TextIsTrimmed_WhitespacePadded()
    {
        var q = new SteerQueue();
        q.TryEnqueue("s1", "  focus on tests  ");
        Assert.Equal(new[] { "focus on tests" }, q.Drain("s1"));
    }

    [Fact]
    public void InvalidCapacity_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SteerQueue(capacity: 0));
    }
}
