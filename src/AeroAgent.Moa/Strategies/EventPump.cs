using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using AeroAgent.Conversation.Orchestration;

namespace AeroAgent.Moa.Strategies;

/// <summary>
/// 事件泵：把后台编排任务写入 channel 的事件流式 yield 给上层。
/// 取消时优雅停止（不抛 OperationCanceledException）——后台任务持有同一个 ct，
/// 会自行把进行中的消息落库为 Cancelled。
/// </summary>
internal static class EventPump
{
    public static async IAsyncEnumerable<ChatEvent> DrainAsync(
        Channel<ChatEvent> channel,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var enumerator = channel.Reader.ReadAllAsync(ct).GetAsyncEnumerator();
        try
        {
            while (true)
            {
                ChatEvent? ev = null;
                bool moved;
                try
                {
                    moved = await enumerator.MoveNextAsync();
                    if (moved)
                    {
                        ev = enumerator.Current;
                    }
                }
                catch (OperationCanceledException)
                {
                    moved = false;
                }
                catch (ChannelClosedException)
                {
                    moved = false;
                }

                if (!moved)
                {
                    break;
                }

                // yield 位于 try 之外（C# 禁止在带 catch 的 try 内 yield）。
                yield return ev!;
            }
        }
        finally
        {
            await enumerator.DisposeAsync();
        }
    }
}
