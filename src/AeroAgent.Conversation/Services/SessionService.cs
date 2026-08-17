using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AeroAgent.Conversation.Data;
using AeroAgent.Conversation.Models;
using AeroCode.Core.Common;
using Microsoft.EntityFrameworkCore;

namespace AeroAgent.Conversation.Services;

/// <summary>
/// <see cref="ISessionService"/> 的 EF Core 实现。真实读写 chat_sessions /
/// chat_messages，无内存缓存、无假数据；并发安全交给数据库事务。
/// </summary>
public sealed class SessionService : ISessionService
{
    private readonly ConversationDbContext _db;

    public SessionService(ConversationDbContext db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    public async Task<Result<ChatSession>> CreateSessionAsync(
        OrchestrationStrategy strategy = OrchestrationStrategy.Single,
        string? preferredProviderId = null,
        string? preferredModel = null,
        string? title = null)
    {
        var session = new ChatSession
        {
            Title = string.IsNullOrWhiteSpace(title)
                ? $"新会话 {DateTime.Now:yyyy-MM-dd HH:mm}"
                : title.Trim(),
            Strategy = strategy,
            PreferredProviderId = preferredProviderId,
            PreferredModel = preferredModel,
        };
        _db.Sessions.Add(session);
        await _db.SaveChangesAsync();
        return Result<ChatSession>.Ok(session);
    }

    public async Task<Result<IReadOnlyList<ChatSessionSummary>>> ListSessionsAsync(bool includeDeleted = false)
    {
        var query = _db.Sessions.AsNoTracking();
        if (!includeDeleted)
        {
            query = query.Where(s => !s.IsDeleted);
        }

        var sessions = await query
            .OrderByDescending(s => s.IsPinned)
            .ThenByDescending(s => s.UpdatedAtUtc)
            .ToListAsync();

        if (sessions.Count == 0)
        {
            return Result<IReadOnlyList<ChatSessionSummary>>.Ok(
                new List<ChatSessionSummary>());
        }

        // 消息计数单独分组查询，避开 EF group-join + DefaultIfEmpty 的物化坑。
        var ids = sessions.Select(s => s.Id).ToList();
        var countRows = await _db.Messages.AsNoTracking()
            .Where(m => ids.Contains(m.SessionId))
            .GroupBy(m => m.SessionId)
            .Select(g => new { SessionId = g.Key, Count = g.Count() })
            .ToListAsync();
        var counts = countRows.ToDictionary(r => r.SessionId, r => r.Count);

        var summaries = sessions
            .Select(s => new ChatSessionSummary(
                s.Id, s.Title, s.Strategy, s.PreferredProviderId, s.PreferredModel,
                s.IsPinned, counts.GetValueOrDefault(s.Id, 0), s.CreatedAtUtc, s.UpdatedAtUtc))
            .ToList();

        return Result<IReadOnlyList<ChatSessionSummary>>.Ok(summaries);
    }

    public async Task<Result<ChatSession>> GetSessionAsync(string id)
    {
        var session = await _db.Sessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id);
        return session is null
            ? Result<ChatSession>.Fail($"session '{id}' not found")
            : Result<ChatSession>.Ok(session);
    }

    public async Task<Result<ChatSession>> RenameSessionAsync(string id, string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return Result<ChatSession>.Fail("title must not be empty");
        }

        var session = await _db.Sessions.FirstOrDefaultAsync(s => s.Id == id);
        if (session is null)
        {
            return Result<ChatSession>.Fail($"session '{id}' not found");
        }

        session.Title = title.Trim();
        session.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Result<ChatSession>.Ok(session);
    }

    public async Task<Result<ChatSession>> SetStrategyAsync(
        string id,
        OrchestrationStrategy strategy,
        string? preferredProviderId,
        string? preferredModel)
    {
        var session = await _db.Sessions.FirstOrDefaultAsync(s => s.Id == id);
        if (session is null)
        {
            return Result<ChatSession>.Fail($"session '{id}' not found");
        }

        session.Strategy = strategy;
        session.PreferredProviderId = preferredProviderId;
        session.PreferredModel = preferredModel;
        session.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Result<ChatSession>.Ok(session);
    }

    public async Task<Result<ChatSession>> TogglePinAsync(string id)
    {
        var session = await _db.Sessions.FirstOrDefaultAsync(s => s.Id == id);
        if (session is null)
        {
            return Result<ChatSession>.Fail($"session '{id}' not found");
        }

        session.IsPinned = !session.IsPinned;
        session.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Result<ChatSession>.Ok(session);
    }

    public async Task<Result<bool>> DeleteSessionAsync(string id)
    {
        var session = await _db.Sessions.FirstOrDefaultAsync(s => s.Id == id);
        if (session is null)
        {
            return Result<bool>.Fail($"session '{id}' not found");
        }

        session.IsDeleted = true;
        session.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Result<bool>.Ok(true);
    }

    public async Task<Result<bool>> RestoreSessionAsync(string id)
    {
        var session = await _db.Sessions.FirstOrDefaultAsync(s => s.Id == id);
        if (session is null)
        {
            return Result<bool>.Fail($"session '{id}' not found");
        }

        session.IsDeleted = false;
        session.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Result<bool>.Ok(true);
    }

    public async Task<Result<IReadOnlyList<ChatMessage>>> GetMessagesAsync(string sessionId)
    {
        var exists = await _db.Sessions.AsNoTracking().AnyAsync(s => s.Id == sessionId);
        if (!exists)
        {
            return Result<IReadOnlyList<ChatMessage>>.Fail($"session '{sessionId}' not found");
        }

        var messages = await _db.Messages.AsNoTracking()
            .Where(m => m.SessionId == sessionId)
            .OrderBy(m => m.CreatedAtUtc)
            .ToListAsync();
        return Result<IReadOnlyList<ChatMessage>>.Ok(messages);
    }

    public async Task<Result<ChatMessage>> AppendMessageAsync(ChatMessage message)
    {
        if (message is null)
        {
            return Result<ChatMessage>.Fail("message must not be null");
        }

        var session = await _db.Sessions.FirstOrDefaultAsync(s => s.Id == message.SessionId);
        if (session is null)
        {
            return Result<ChatMessage>.Fail($"session '{message.SessionId}' not found");
        }

        // 首条用户消息自动成为会话标题（仅当标题仍是默认占位时）。
        if (message.Role == ChatRole.User
            && session.Title.StartsWith("新会话 ", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(message.Content))
        {
            session.Title = message.Content.Length <= 40
                ? message.Content
                : message.Content[..40] + "…";
        }

        session.UpdatedAtUtc = DateTime.UtcNow;
        _db.Messages.Add(message);
        await _db.SaveChangesAsync();
        return Result<ChatMessage>.Ok(message);
    }

    public async Task<Result<ChatMessage>> UpdateMessageAsync(ChatMessage message)
    {
        if (message is null)
        {
            return Result<ChatMessage>.Fail("message must not be null");
        }

        var existing = await _db.Messages.FirstOrDefaultAsync(m => m.Id == message.Id);
        if (existing is null)
        {
            return Result<ChatMessage>.Fail($"message '{message.Id}' not found");
        }

        existing.Content = message.Content;
        existing.Status = message.Status;
        existing.Error = message.Error;
        existing.TokensIn = message.TokensIn;
        existing.TokensOut = message.TokensOut;
        existing.CostUsd = message.CostUsd;
        existing.LatencyMs = message.LatencyMs;
        await _db.SaveChangesAsync();
        return Result<ChatMessage>.Ok(existing);
    }
}
