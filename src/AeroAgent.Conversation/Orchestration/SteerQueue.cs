// Copyright (c) AeroCode
// SteerQueue — 会话级运行中插话队列（批次 B G3，builder-β）。
// 会话级有界 Channel<string>：流式进行中 TryEnqueue 的指令，由编排层在下一轮
// Drain 后注入 user 块；Clear 在会话结束时清空并回收队列。
// 纪律：队列满 = TryEnqueue 诚实返回 false（绝不静默挤掉最旧指令——插话是用户
// 意图，丢弃必须显式可见）；本工程不引用 Harness，SteerRequestedEvent 的发布
// 属于组合根接线，不在本类职责内。
using System.Threading.Channels;

namespace AeroAgent.Conversation.Orchestration;

public sealed class SteerQueue
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Channel<string>> _queues =
        new(StringComparer.Ordinal);

    private readonly int _capacity;

    /// <param name="capacity">单会话排队上限（默认 16）；满后 TryEnqueue 返回 false。</param>
    public SteerQueue(int capacity = 16)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "capacity must be >= 1");
        }

        _capacity = capacity;
    }

    /// <summary>
    /// 运行中插话入队（会话级）。空会话/空文本返回 false；队列已满返回 false
    /// （诚实拒绝，不静默丢弃——消费方应提示用户稍后再试）。
    /// </summary>
    public bool TryEnqueue(string sessionId, string text)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var queue = _queues.GetOrAdd(sessionId, static (_, cap) => Channel.CreateBounded<string>(
            new BoundedChannelOptions(cap)
            {
                SingleReader = false,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait, // 满时 TryWrite 返回 false
            }), _capacity);
        return queue.Writer.TryWrite(text.Trim());
    }

    /// <summary>
    /// 取走该会话当前排队的全部插话（下一轮注入 user 块；FIFO）。
    /// 无排队/未知会话返回空列表。
    /// </summary>
    public IReadOnlyList<string> Drain(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || !_queues.TryGetValue(sessionId, out var queue))
        {
            return Array.Empty<string>();
        }

        var items = new List<string>();
        while (queue.Reader.TryRead(out var item))
        {
            items.Add(item);
        }

        return items;
    }

    /// <summary>会话结束清空：移除该会话队列，返回被丢弃的条数（幂等）。</summary>
    public int Clear(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || !_queues.TryRemove(sessionId, out var queue))
        {
            return 0;
        }

        var dropped = 0;
        while (queue.Reader.TryRead(out _))
        {
            dropped++;
        }

        queue.Writer.TryComplete();
        return dropped;
    }
}
