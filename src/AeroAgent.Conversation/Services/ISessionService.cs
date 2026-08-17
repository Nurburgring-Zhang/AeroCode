using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AeroAgent.Conversation.Models;
using AeroCode.Core.Common;

namespace AeroAgent.Conversation.Services;

/// <summary>会话列表项（轻量 DTO）。</summary>
public sealed record ChatSessionSummary(
    string Id,
    string Title,
    OrchestrationStrategy Strategy,
    string? PreferredProviderId,
    string? PreferredModel,
    bool IsPinned,
    int MessageCount,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

/// <summary>
/// 会话应用服务：会话 CRUD + 消息读写的唯一入口。
/// 所有方法返回 <see cref="Result{T}"/>，错误路径显式。
/// </summary>
public interface ISessionService
{
    /// <summary>创建会话。title 为空时按"新会话+时间"占位，首条用户消息后可自动改名。</summary>
    Task<Result<ChatSession>> CreateSessionAsync(
        OrchestrationStrategy strategy = OrchestrationStrategy.Single,
        string? preferredProviderId = null,
        string? preferredModel = null,
        string? title = null);

    /// <summary>会话列表（默认排除软删除；置顶优先，其余按更新时间倒序）。</summary>
    Task<Result<IReadOnlyList<ChatSessionSummary>>> ListSessionsAsync(bool includeDeleted = false);

    Task<Result<ChatSession>> GetSessionAsync(string id);

    Task<Result<ChatSession>> RenameSessionAsync(string id, string title);

    /// <summary>切换会话编排策略/模型偏好。</summary>
    Task<Result<ChatSession>> SetStrategyAsync(
        string id,
        OrchestrationStrategy strategy,
        string? preferredProviderId,
        string? preferredModel);

    Task<Result<ChatSession>> TogglePinAsync(string id);

    /// <summary>软删除。</summary>
    Task<Result<bool>> DeleteSessionAsync(string id);

    Task<Result<bool>> RestoreSessionAsync(string id);

    /// <summary>会话消息（按时间升序）。</summary>
    Task<Result<IReadOnlyList<ChatMessage>>> GetMessagesAsync(string sessionId);

    /// <summary>追加消息并刷新会话 UpdatedAt。空会话追加首条用户消息时自动以其为标题。</summary>
    Task<Result<ChatMessage>> AppendMessageAsync(ChatMessage message);

    /// <summary>更新已存在消息（流式收尾写状态/token/成本/正文）。</summary>
    Task<Result<ChatMessage>> UpdateMessageAsync(ChatMessage message);
}
