using System;

namespace AeroAgent.Conversation.Models;

/// <summary>
/// 统一对话中的一条消息。
///
/// MOA 归属建模：每条助手消息自带 <see cref="ProviderId"/>/<see cref="ModelId"/>
/// 与 <see cref="OrchestrationRole"/>（策略角色），编排过程（planner 拆解、
/// worker 分工、synthesizer 聚合）中的每个中间产物都是一条真实消息，用
/// <see cref="ParentMessageId"/> 串成树，前端据此渲染调度过程。
/// </summary>
public class ChatMessage
{
    /// <summary>消息唯一标识（GUID 字符串）。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>所属会话。</summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>角色（user/assistant/system/tool）。</summary>
    public ChatRole Role { get; set; } = ChatRole.User;

    /// <summary>消息正文（Markdown）。</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>生成该消息的 provider（助手消息；用户消息为 null）。</summary>
    public string? ProviderId { get; set; }

    /// <summary>生成该消息的模型名。</summary>
    public string? ModelId { get; set; }

    /// <summary>该消息在编排策略中承担的角色。</summary>
    public StrategyRole OrchestrationRole { get; set; } = StrategyRole.None;

    /// <summary>
    /// 父消息 Id。MOA 中 worker/synthesizer 产物指向其上游（planner 或用户消息），
    /// 构成归属树；顶层消息为 null。
    /// </summary>
    public string? ParentMessageId { get; set; }

    /// <summary>处理状态（流式进行中/完成/失败/取消/降级）。</summary>
    public MessageStatus Status { get; set; } = MessageStatus.Pending;

    /// <summary>失败时的错误说明。</summary>
    public string? Error { get; set; }

    /// <summary>输入 token 数（真实 usage，未报则为 0）。</summary>
    public int TokensIn { get; set; }

    /// <summary>输出 token 数。</summary>
    public int TokensOut { get; set; }

    /// <summary>本条消息成本（USD，按画像费率核算；未知为 0）。</summary>
    public double CostUsd { get; set; }

    /// <summary>首 token 延迟（毫秒）。</summary>
    public int LatencyMs { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
