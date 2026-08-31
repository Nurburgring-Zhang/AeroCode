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
/// 会话 Todo 持久化服务（批次 B G1）。真实读写 todo_items 表，按 SessionId 会话隔离。
/// </summary>
public interface ITodoStore
{
    /// <summary>追加一条待办；position 缺省取会话内当前最大序号 +1。内容为空时 Fail。</summary>
    Task<Result<TodoItem>> AddAsync(string sessionId, string content, int? position = null, CancellationToken ct = default);

    /// <summary>会话内全部待办（按 Position 升序、Id 次序稳定）。会话不存在时 Fail。</summary>
    Task<Result<IReadOnlyList<TodoItem>>> ListAsync(string sessionId, CancellationToken ct = default);

    /// <summary>更新待办内容/完成态（null = 不改）。项不存在时 Fail；更新后内容不得为空白。</summary>
    Task<Result<TodoItem>> UpdateAsync(string todoId, string? content = null, bool? isCompleted = null, CancellationToken ct = default);

    /// <summary>删除一条待办。项不存在返回 Fail。</summary>
    Task<Result<bool>> DeleteAsync(string todoId, CancellationToken ct = default);

    /// <summary>清空会话内全部待办，返回删除数。</summary>
    Task<Result<int>> ClearAsync(string sessionId, CancellationToken ct = default);
}

/// <summary>
/// <see cref="ITodoStore"/> 的 EF Core 实现。与 <see cref="SessionService"/> 的单实例
/// 上下文 + 互斥锁模式不同，这里按操作创建短生命周期 <see cref="ConversationDbContext"/>
/// （工厂注入）——TodoStore 常与 SessionService 并发使用（工具循环内 todo_* 与编排
/// 持久化交错），两个服务的锁互不可见，共享一个 DbContext 会引入并发变更跟踪竞争；
/// 短生命周期上下文天然隔离，单次 SaveChanges 原子落库。
/// 读取一律物化为脱离跟踪的实体副本（AsNoTracking / 手工拷贝）。
/// </summary>
public sealed class TodoStore : ITodoStore
{
    private readonly Func<ConversationDbContext> _dbFactory;

    public TodoStore(Func<ConversationDbContext> dbFactory)
    {
        _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
    }

    public async Task<Result<TodoItem>> AddAsync(string sessionId, string content, int? position = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return Result<TodoItem>.Fail("sessionId must not be empty");
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            return Result<TodoItem>.Fail("todo content must not be empty");
        }

        using var db = _dbFactory();
        var sessionExists = await db.Sessions.AsNoTracking()
            .AnyAsync(s => s.Id == sessionId, ct).ConfigureAwait(false);
        if (!sessionExists)
        {
            return Result<TodoItem>.Fail($"session '{sessionId}' not found");
        }

        int pos;
        if (position is { } explicitPos)
        {
            pos = explicitPos;
        }
        else
        {
            var maxPos = await db.Todos.AsNoTracking()
                .Where(t => t.SessionId == sessionId)
                .OrderByDescending(t => t.Position)
                .Select(t => (int?)t.Position)
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            pos = (maxPos ?? -1) + 1;
        }

        var item = new TodoItem
        {
            SessionId = sessionId,
            Content = content.Trim(),
            Position = pos,
        };
        db.Todos.Add(item);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        db.Entry(item).State = EntityState.Detached;
        return Result<TodoItem>.Ok(item);
    }

    public async Task<Result<IReadOnlyList<TodoItem>>> ListAsync(string sessionId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return Result<IReadOnlyList<TodoItem>>.Fail("sessionId must not be empty");
        }

        using var db = _dbFactory();
        var sessionExists = await db.Sessions.AsNoTracking()
            .AnyAsync(s => s.Id == sessionId, ct).ConfigureAwait(false);
        if (!sessionExists)
        {
            return Result<IReadOnlyList<TodoItem>>.Fail($"session '{sessionId}' not found");
        }

        var items = await db.Todos.AsNoTracking()
            .Where(t => t.SessionId == sessionId)
            .OrderBy(t => t.Position)
            .ThenBy(t => t.Id)
            .ToListAsync(ct).ConfigureAwait(false);
        return Result<IReadOnlyList<TodoItem>>.Ok(items);
    }

    public async Task<Result<TodoItem>> UpdateAsync(string todoId, string? content = null, bool? isCompleted = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(todoId))
        {
            return Result<TodoItem>.Fail("todoId must not be empty");
        }

        if (content is not null && string.IsNullOrWhiteSpace(content))
        {
            return Result<TodoItem>.Fail("todo content must not be empty");
        }

        if (content is null && isCompleted is null)
        {
            return Result<TodoItem>.Fail("nothing to update: provide content or isCompleted");
        }

        using var db = _dbFactory();
        var item = await db.Todos.FirstOrDefaultAsync(t => t.Id == todoId, ct).ConfigureAwait(false);
        if (item is null)
        {
            return Result<TodoItem>.Fail($"todo '{todoId}' not found");
        }

        if (content is not null)
        {
            item.Content = content.Trim();
        }

        if (isCompleted is { } done)
        {
            item.IsCompleted = done;
        }

        item.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        var detached = new TodoItem
        {
            Id = item.Id,
            SessionId = item.SessionId,
            Content = item.Content,
            IsCompleted = item.IsCompleted,
            Position = item.Position,
            CreatedAtUtc = item.CreatedAtUtc,
            UpdatedAtUtc = item.UpdatedAtUtc,
        };
        db.Entry(item).State = EntityState.Detached;
        return Result<TodoItem>.Ok(detached);
    }

    public async Task<Result<bool>> DeleteAsync(string todoId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(todoId))
        {
            return Result<bool>.Fail("todoId must not be empty");
        }

        using var db = _dbFactory();
        var deleted = await db.Todos
            .Where(t => t.Id == todoId)
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);
        return deleted > 0
            ? Result<bool>.Ok(true)
            : Result<bool>.Fail($"todo '{todoId}' not found");
    }

    public async Task<Result<int>> ClearAsync(string sessionId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return Result<int>.Fail("sessionId must not be empty");
        }

        using var db = _dbFactory();
        var deleted = await db.Todos
            .Where(t => t.SessionId == sessionId)
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);
        return Result<int>.Ok(deleted);
    }
}
