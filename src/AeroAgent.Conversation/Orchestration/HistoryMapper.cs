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
///
/// 附件历史驱逐（review L1）：附件注入正文单轮可达 ~120K 字符且随 Content
/// 永久持久化；若不驱逐，多附件长会话每轮都把所有旧注入重发模型，
/// 上下文与内存无界增长。策略：仅最近 <see cref="AttachmentRetentionUserTurns"/>
/// 个「实际进上下文的用户轮」保留完整注入正文，更早的带附件用户轮降级为
/// 「元信息存根 + 原始用户文本」（锚点来自 UserText 列）。仅作用于模型上下文，
/// 数据库与 UI 展示保持完整；无 UserText 的早期数据保持原样（不做破坏性猜测）。
/// </summary>
public static class HistoryMapper
{
    /// <summary>
    /// 附件正文保留窗口（按实际进上下文的用户轮数计，含当前轮）。
    /// 2 = 当前轮 + 上一轮用户消息保留完整附件正文，更早的驱逐为元信息存根：
    /// 既覆盖「刚发的文件下一轮接着问」的常见模式，又把附件上下文钉死在
    /// 最多 2×注入预算 的硬上界内。
    /// </summary>
    private const int AttachmentRetentionUserTurns = 2;

    public static IReadOnlyList<AiChatMessage> ToProviderMessages(
        IReadOnlyList<EntityChatMessage> history, string? systemPrompt = null,
        IReadOnlyList<ImageContent>? visionImages = null)
    {
        var result = new List<AiChatMessage>(history.Count + 1);

        // review L1：按发出顺序记录进上下文的用户轮（result 下标 + 源实体），供驱逐后处理。
        // AiChatMessage 为 init-only，驱逐通过替换 result 条目实现而非原地改写。
        var userTurns = new List<(int Index, EntityChatMessage Source)>();

        // 系统上下文（SOUL + instructions）作为独立 system 消息前置：
        // 不持久化、每轮新鲜注入，长文（数万至数十万字符）不占历史。
        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            result.Add(new AiChatMessage { Role = "system", Content = systemPrompt });
        }

        HashSet<string>? emittedToolCallIds = null;
        AiChatMessage? lastUserMessage = null;

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

            var mapped = new AiChatMessage
            {
                Role = role,
                Content = m.Content,
                ToolCalls = hasToolCalls ? toolCalls : null,
                Name = m.Role == ChatRole.Tool ? m.Name : null,
                ToolCallId = m.Role == ChatRole.Tool ? m.ToolCallId : null,
            };
            result.Add(mapped);
            if (role == "user")
            {
                lastUserMessage = mapped;
                // review MED-1：steer 插话是本轮在途指令（门面在本轮用户消息之后追加），
                // 不是独立的附件上下文轮——不计入保留窗口；否则排队 ≥2 条插话会把
                // 当前轮刚发的附件消息顶出窗口，模型在自己这轮就看不到刚上传的文件。
                if (!string.Equals(m.Label, "steer", System.StringComparison.Ordinal))
                {
                    userTurns.Add((result.Count - 1, m));
                }
            }

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

        // review L1：附件历史驱逐——超出保留窗口的旧附件用户轮降级为
        // 「元信息存根 + 原文」。只改模型上下文副本，DB/UI 的 Content 不受影响。
        // 被驱逐的不可能是最后一条用户消息（它总在保留窗口内），vision 图像不受影响。
        var evictBefore = userTurns.Count - AttachmentRetentionUserTurns;
        for (var i = 0; i < evictBefore; i++)
        {
            var (index, source) = userTurns[i];
            if (string.IsNullOrEmpty(source.AttachmentsJson) || source.UserText is null)
            {
                // 无附件，或早期数据缺 UserText 锚点：保持原样（不做破坏性猜测）。
                continue;
            }

            var old = result[index];
            result[index] = new AiChatMessage
            {
                Role = old.Role,
                Content = BuildEvictedAttachmentContent(source.AttachmentsJson, source.UserText),
                Name = old.Name,
                ToolCallId = old.ToolCallId,
                ToolCalls = old.ToolCalls,
                ReasoningContent = old.ReasoningContent,
                Images = old.Images,
            };
        }

        // vision：把本轮图像挂到最后一条用户消息（即本轮用户消息），供支持的 provider 上送。
        if (lastUserMessage is not null && visionImages is { Count: > 0 })
        {
            lastUserMessage.Images = visionImages;
        }

        return result;
    }

    /// <summary>
    /// 旧附件用户轮被驱逐后的上下文内容：元信息存根（文件名/大小，来自
    /// AttachmentsJson）+ 原始用户文本。元数据解析失败如实降级为无清单存根，
    /// 原文始终保留——驱逐只丢附件正文，绝不丢用户的话。
    /// </summary>
    private static string BuildEvictedAttachmentContent(string attachmentsJson, string userText)
    {
        var entries = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(attachmentsJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    // review MED-2：逐项防御非法形态——TryGetProperty 对非对象元素、
                    // GetString 对非字符串值都会抛 InvalidOperationException；
                    // 承诺的诚实降级不允许任何异常逃出驱逐路径。
                    if (el.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var name = el.TryGetProperty("FileName", out var n)
                        && n.ValueKind == JsonValueKind.String
                            ? n.GetString()
                            : null;
                    if (name is null)
                    {
                        continue;
                    }

                    var size = el.TryGetProperty("SizeBytes", out var s) && s.TryGetInt64(out var sz)
                        ? sz
                        : 0L;
                    entries.Add($"{name}（{FormatSize(size)}）");
                }
            }
        }
        catch (JsonException)
        {
            // 元数据损坏：降级为无清单存根，不阻塞也不伪造清单。
        }

        var stub = entries.Count > 0
            ? $"[历史附件正文已从上下文驱逐，仅保留元信息：{string.Join("、", entries)}]"
            : "[历史附件正文已从上下文驱逐，仅保留元信息]";
        return stub + "\n\n" + userText;
    }

    /// <summary>人类可读文件大小（与 ChatViewModel/MessageAttachment 口径一致）。</summary>
    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes}B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1}KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):F1}MB",
        _ => $"{bytes / (1024.0 * 1024.0 * 1024.0):F2}GB",
    };

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
