using System.Collections.Generic;
using AeroAgent.Conversation.Models;
using AiChatMessage = AeroCode.AI.Models.ChatMessage;

namespace AeroAgent.Conversation.Orchestration;

/// <summary>
/// 会话实体历史 → provider 请求消息的映射。
/// 只保留有正文的消息；失败/取消的消息内容不完整，不进上下文；
/// MOA 编排的中间产物（路由分类、规划 JSON、子任务产出、候选答案、评审意见，
/// 即 IsFinal == false 的助手消息）只用于当轮编排与审计留痕，
/// 绝不回灌进后续轮次的模型上下文——否则会上下文爆炸，且会破坏
/// 严格角色交替（如 Anthropic）API 的消息序列。null（早期数据）按最终答复对待。
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

            // 编排中间产物不进上下文（IsFinal==false）；
            // IsFinal==null 是早期版本数据，按最终答复对待以保持多轮连续性。
            if (m.Role == ChatRole.Assistant && m.IsFinal == false)
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
