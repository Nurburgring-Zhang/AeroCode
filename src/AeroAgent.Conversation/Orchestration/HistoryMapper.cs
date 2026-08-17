using System.Collections.Generic;
using AeroAgent.Conversation.Models;
using AiChatMessage = AeroCode.AI.Models.ChatMessage;

namespace AeroAgent.Conversation.Orchestration;

/// <summary>
/// 会话实体历史 → provider 请求消息的映射。
/// 只保留有正文的消息；失败/取消的消息内容不完整，不进上下文。
/// </summary>
public static class HistoryMapper
{
    public static IReadOnlyList<AiChatMessage> ToProviderMessages(
        IReadOnlyList<ChatMessage> history)
    {
        var result = new List<AiChatMessage>(history.Count);
        foreach (var m in history)
        {
            if (string.IsNullOrEmpty(m.Content))
            {
                continue;
            }

            // 失败/取消的消息不进上下文（内容不完整会误导模型）。
            if (m.Status is MessageStatus.Failed or MessageStatus.Cancelled)
            {
                continue;
            }

            var role = m.Role switch
            {
                ChatRole.User => "user",
                ChatRole.Assistant => "assistant",
                ChatRole.System => "system",
                ChatRole.Tool => "tool",
                _ => "user",
            };

            result.Add(new AiChatMessage { Role = role, Content = m.Content });
        }

        return result;
    }
}
