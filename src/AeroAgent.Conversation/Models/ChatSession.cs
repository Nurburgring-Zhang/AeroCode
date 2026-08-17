using System;
using System.Collections.Generic;

namespace AeroAgent.Conversation.Models;

/// <summary>
/// 一个统一对话会话。会话是消息的容器，承载编排策略与模型偏好配置。
/// 软删除（IsDeleted）支持回收站语义。
/// </summary>
public class ChatSession
{
    /// <summary>会话唯一标识（GUID 字符串）。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>会话标题。默认取首条用户消息截断，用户可改名。</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>本会话默认编排策略。</summary>
    public OrchestrationStrategy Strategy { get; set; } = OrchestrationStrategy.Single;

    /// <summary>Single/偏好模式下指定的 provider（可空=用全局默认）。</summary>
    public string? PreferredProviderId { get; set; }

    /// <summary>Single/偏好模式下指定的模型名（可空=用 provider 默认）。</summary>
    public string? PreferredModel { get; set; }

    /// <summary>置顶。</summary>
    public bool IsPinned { get; set; }

    /// <summary>软删除标记。</summary>
    public bool IsDeleted { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>导航属性：会话内消息（按 CreatedAt 升序由查询保证）。</summary>
    public List<ChatMessage> Messages { get; set; } = new();
}
