using System.Threading.Tasks;
using AeroAgent.Conversation.Models;
using AeroCode.Core.Common;

namespace AeroAgent.Conversation.Services;

/// <summary>
/// 会话分叉能力（批次 B G1）。独立成窄接口而非并入 <see cref="ISessionService"/>：
/// ISessionService 已有多个既有实现（App 测试替身等），动公共接口会波及所有权之外
/// 的文件；分叉能力只有真实持久化实现提供，消费方按需注入。
/// </summary>
public interface ISessionFork
{
    /// <summary>
    /// 从既有会话分叉出一个新会话：复制会话元数据与消息集（含全部归属/用量字段），
    /// 消息 Id 重新生成、<see cref="ChatMessage.ParentMessageId"/> 链同步重映射；
    /// 分叉点之前但父消息未包含在内的消息（理论上仅对中间产物链出现）ParentMessageId 置 null。
    /// </summary>
    /// <param name="sessionId">源会话 Id。</param>
    /// <param name="uptoMessageId">分叉点消息 Id：复制到该消息为止（含）；
    /// null = 复制全部消息。消息不存在时 Fail。</param>
    /// <returns>新会话（标题带“（fork）”后缀，置顶状态不继承）。源会话完全不动。</returns>
    Task<Result<ChatSession>> ForkAsync(string sessionId, string? uptoMessageId = null);
}
