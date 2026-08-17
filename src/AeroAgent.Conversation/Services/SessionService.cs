using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AeroAgent.Conversation.Data;
using AeroAgent.Conversation.Models;
using AeroCode.Core.Common;
using Microsoft.EntityFrameworkCore;

namespace AeroAgent.Conversation.Services;

/// <summary>
/// <see cref="ISessionService"/> 的 EF Core 实现。真实读写 chat_sessions /
/// chat_messages，无内存缓存、无假数据。
///
/// 并发模型：MOA 策略（Ensemble/Decompose）会并行编排多个 worker，
/// 而 <see cref="ConversationDbContext"/> 并非线程安全（共享变更跟踪器）。
/// 因此本服务用互斥锁把每个 DbContext 操作单元串行化——DB 事务语义不变，
/// 且并行 worker 的 HTTP 调用本身不受锁影响（锁只覆盖持久化瞬间）。
/// 读取方法一律返回脱离跟踪的实体副本：调用方持有的实例不会混入后续保存。
/// </summary>
public sealed class SessionService : ISessionService, IDisposable
{
    private readonly ConversationDbContext _db;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SessionService(ConversationDbContext db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    public void Dispose() => _gate.Dispose();

    private async Task<T> WithDbAsync<T>(Func<Task<T>> operation)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await operation().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static ChatSession Detach(ChatSession s) => new()
    {
        Id = s.Id,
        Title = s.Title,
        Strategy = s.Strategy,
        PreferredProviderId = s.PreferredProviderId,
        PreferredModel = s.PreferredModel,
        IsPinned = s.IsPinned,
        IsDeleted = s.IsDeleted,
        CreatedAtUtc = s.CreatedAtUtc,
        UpdatedAtUtc = s.UpdatedAtUtc,
    };

    private static ChatMessage Detach(ChatMessage m) => new()
    {
        Id = m.Id,
        SessionId = m.SessionId,
        Role = m.Role,
        Content = m.Content,
        ProviderId = m.ProviderId,
        ModelId = m.ModelId,
        OrchestrationRole = m.OrchestrationRole,
        ParentMessageId = m.ParentMessageId,
        Label = m.Label,
        IsFinal = m.IsFinal,
        ToolCallsJson = m.ToolCallsJson,
        ToolCallId = m.ToolCallId,
        Name = m.Name,
        Status = m.Status,
        Error = m.Error,
        TokensIn = m.TokensIn,
        TokensOut = m.TokensOut,
        CostUsd = m.CostUsd,
        LatencyMs = m.LatencyMs,
        CreatedAtUtc = m.CreatedAtUtc,
    };

    public Task<Result<ChatSession>> CreateSessionAsync(
        OrchestrationStrategy strategy = OrchestrationStrategy.Single,
        string? preferredProviderId = null,
        string? preferredModel = null,
        string? title = null)
        => WithDbAsync(async () =>
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
            var detached = Detach(session);
            _db.Entry(session).State = EntityState.Detached;
            return Result<ChatSession>.Ok(detached);
        });

    public Task<Result<IReadOnlyList<ChatSessionSummary>>> ListSessionsAsync(bool includeDeleted = false)
        => WithDbAsync(async () =>
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
        });

    public Task<Result<ChatSession>> GetSessionAsync(string id)
        => WithDbAsync(async () =>
        {
            var session = await _db.Sessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id);
            return session is null
                ? Result<ChatSession>.Fail($"session '{id}' not found")
                : Result<ChatSession>.Ok(Detach(session));
        });

    public Task<Result<ChatSession>> RenameSessionAsync(string id, string title)
        => WithDbAsync(async () =>
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
            var renamed = Detach(session);
            _db.Entry(session).State = EntityState.Detached;
            return Result<ChatSession>.Ok(renamed);
        });

    public Task<Result<ChatSession>> SetStrategyAsync(
        string id,
        OrchestrationStrategy strategy,
        string? preferredProviderId,
        string? preferredModel)
        => WithDbAsync(async () =>
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
            var updated = Detach(session);
            _db.Entry(session).State = EntityState.Detached;
            return Result<ChatSession>.Ok(updated);
        });

    public Task<Result<ChatSession>> TogglePinAsync(string id)
        => WithDbAsync(async () =>
        {
            var session = await _db.Sessions.FirstOrDefaultAsync(s => s.Id == id);
            if (session is null)
            {
                return Result<ChatSession>.Fail($"session '{id}' not found");
            }

            session.IsPinned = !session.IsPinned;
            session.UpdatedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            var toggled = Detach(session);
            _db.Entry(session).State = EntityState.Detached;
            return Result<ChatSession>.Ok(toggled);
        });

    public Task<Result<bool>> DeleteSessionAsync(string id)
        => WithDbAsync(async () =>
        {
            var session = await _db.Sessions.FirstOrDefaultAsync(s => s.Id == id);
            if (session is null)
            {
                return Result<bool>.Fail($"session '{id}' not found");
            }

            session.IsDeleted = true;
            session.UpdatedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            _db.Entry(session).State = EntityState.Detached;
            return Result<bool>.Ok(true);
        });

    public Task<Result<bool>> RestoreSessionAsync(string id)
        => WithDbAsync(async () =>
        {
            var session = await _db.Sessions.FirstOrDefaultAsync(s => s.Id == id);
            if (session is null)
            {
                return Result<bool>.Fail($"session '{id}' not found");
            }

            session.IsDeleted = false;
            session.UpdatedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            _db.Entry(session).State = EntityState.Detached;
            return Result<bool>.Ok(true);
        });

    public Task<Result<IReadOnlyList<ChatMessage>>> GetMessagesAsync(string sessionId)
        => WithDbAsync(async () =>
        {
            var exists = await _db.Sessions.AsNoTracking().AnyAsync(s => s.Id == sessionId);
            if (!exists)
            {
                return Result<IReadOnlyList<ChatMessage>>.Fail($"session '{sessionId}' not found");
            }

            var messages = await _db.Messages.AsNoTracking()
                .Where(m => m.SessionId == sessionId)
                // 平序键：CreatedAtUtc 毫秒级，工具循环连续追加可能同戳；
                // SQLite 对等值键不保证顺序，次级键 Id 保证跨加载排序稳定。
                .OrderBy(m => m.CreatedAtUtc)
                .ThenBy(m => m.Id)
                .ToListAsync();
            return Result<IReadOnlyList<ChatMessage>>.Ok(
                messages.Select(Detach).ToList());
        });

    public Task<Result<ChatMessage>> AppendMessageAsync(ChatMessage message)
        => WithDbAsync(async () =>
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
            var detached = Detach(message);
            _db.Entry(message).State = EntityState.Detached; // 不留跟踪实体给后续并发保存
            _db.Entry(session).State = EntityState.Detached;
            return Result<ChatMessage>.Ok(detached);
        });

    public Task<Result<ChatMessage>> UpdateMessageAsync(ChatMessage message)
        => WithDbAsync(async () =>
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
            var detached = Detach(existing);
            _db.Entry(existing).State = EntityState.Detached;
            return Result<ChatMessage>.Ok(detached);
        });
}
