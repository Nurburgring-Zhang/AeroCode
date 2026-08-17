using System.Collections.Generic;
using System.Text.Json;
using AeroAgent.Conversation.Models;
using AeroCode.AI.Models;
using AiChatMessage = AeroCode.AI.Models.ChatMessage;
using EntityChatMessage = AeroAgent.Conversation.Models.ChatMessage;

namespace AeroAgent.Conversation.Orchestration;

/// <summary>
/// 会话实体历史 → provider 请求消息的映射。
/// 只保留有正文（或有工具调用）的消息；失败/取消的消息内容不完整，不进上下文；
/// MOA 编排的中间产物（路由分类、规划 JSON、子任务产出、候选答案、评审意见，
/// 即 IsFinal == false 的助手消息）只用于当轮编排与审计留痕，
/// 绝不回灌进后续轮次的模型上下文——否则会上下文爆炸，且会破坏
/// 严格角色交替（如 Anthropic）API 的消息序列。null（早期数据）按最终答复对待。
///
/// 工具循环例外：带 ToolCallsJson 的助手轮（IsFinal == false）必须回灌——
/// 模型要看到自己发起的工具调用，紧随其后的 tool 结果消息才有归属。
/// 孤立的 tool 结果（对应助手轮缺失，如脏数据）会被丢弃：
/// 严格 API 要求 tool 消息必须跟在携带匹配 tool_calls 的助手消息之后。
/// </summary>
public static class HistoryMapper
{
    public static IReadOnlyList<AiChatMessage> ToProviderMessages(
        IReadOnlyList<EntityChatMessage> history)
    {
        var result = new List<AiChatMessage>(history.Count);
        HashSet<string>? emittedToolCallIds = null;

        foreach (var m in history)
        {
            // 失败/取消的消息不进上下文（内容不完整会误导模型）。
            if (m.Status is MessageStatus.Failed or MessageStatus.Cancelled)
            {
                continue;
            }

            var toolCalls = m.Role == ChatRole.Assistant ? ParseToolCalls(m.ToolCallsJson) : null;
            var hasToolCalls = toolCalls is { Count: > 0 };

            if (string.IsNullOrEmpty(m.Content) && !hasToolCalls)
            {
                continue;
            }

            // 编排中间产物不进上下文（IsFinal==false）；
            // 例外：携带工具调用的助手轮必须保留（tool 结果依赖它）。
            // IsFinal==null 是早期版本数据，按最终答复对待以保持多轮连续性。
            if (m.Role == ChatRole.Assistant && m.IsFinal == false && !hasToolCalls)
            {
                continue;
            }

            // tool 结果只在对应助手轮已回灌时才有意义。
            if (m.Role == ChatRole.Tool)
            {
                if (m.ToolCallId is null
                    || emittedToolCallIds is null
                    || !emittedToolCallIds.Contains(m.ToolCallId))
                {
                    continue;
                }
            }

            var role = m.Role switch
            {
                ChatRole.User => "user",
                ChatRole.Assistant => "assistant",
                ChatRole.System => "system",
                ChatRole.Tool => "tool",
                _ => "user",
            };

            result.Add(new AiChatMessage
            {
                Role = role,
                Content = m.Content,
                ToolCalls = hasToolCalls ? toolCalls : null,
                Name = m.Role == ChatRole.Tool ? m.Name : null,
                ToolCallId = m.Role == ChatRole.Tool ? m.ToolCallId : null,
            });

            if (hasToolCalls)
            {
                emittedToolCallIds ??= new HashSet<string>(System.StringComparer.Ordinal);
                foreach (var tc in toolCalls!)
                {
                    if (!string.IsNullOrEmpty(tc.Id))
                    {
                        emittedToolCallIds.Add(tc.Id);
                    }
                }
            }
        }

        return result;
    }

    /// <summary>反序列化助手轮的 ToolCallsJson；空串/损坏数据返回 null（诚实降级，不抛）。</summary>
    private static IReadOnlyList<ToolCall>? ParseToolCalls(string? toolCallsJson)
    {
        if (string.IsNullOrEmpty(toolCallsJson))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<List<ToolCall>>(toolCallsJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
