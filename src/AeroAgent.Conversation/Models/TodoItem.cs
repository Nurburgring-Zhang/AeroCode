using System;

namespace AeroAgent.Conversation.Models;

/// <summary>
/// 会话 Todo 项（批次 B G1）。按 <see cref="SessionId"/> 会话隔离，
/// Position 为会话内排序键（同序按 Id 稳定次序）。
/// </summary>
public class TodoItem
{
    /// <summary>项唯一标识（GUID 字符串）。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>所属会话（会话隔离键）。</summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>待办内容。</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>是否已完成。</summary>
    public bool IsCompleted { get; set; }

    /// <summary>会话内排序键（追加时取会话内最大值 +1）。</summary>
    public int Position { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
