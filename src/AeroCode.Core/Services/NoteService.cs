using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AeroCode.Core.Common;
using AeroCode.Core.Data;
using AeroCode.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace AeroCode.Core.Services;

public class NoteService : INoteService
{
    private readonly AeroCodeDbContext _db;
    private readonly ITagService _tags;

    public NoteService(AeroCodeDbContext db, ITagService tags)
    {
        _db = db;
        _tags = tags;
    }

    public async Task<Result<Note>> CreateAsync(string title, string content, long? notebookId = null, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(title))
                return Result<Note>.Fail("标题不能为空");

            var note = new Note
            {
                Title = title.Trim(),
                Content = content ?? string.Empty,
                NotebookId = notebookId,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            _db.Notes.Add(note);
            await _db.SaveChangesAsync(ct);
            return Result<Note>.Ok(note);
        }
        catch (Exception ex)
        {
            return Result<Note>.Fail($"创建笔记失败: {ex.Message}", ex);
        }
    }

    public async Task<Result<Note>> GetByIdAsync(long id, CancellationToken ct = default)
    {
        try
        {
            var note = await _db.Notes
                .Include(n => n.Notebook)
                .Include(n => n.NoteTags).ThenInclude(nt => nt.Tag)
                .FirstOrDefaultAsync(n => n.Id == id, ct);
            return note is null
                ? Result<Note>.Fail($"未找到笔记 #{id}")
                : Result<Note>.Ok(note);
        }
        catch (Exception ex)
        {
            return Result<Note>.Fail($"查询失败: {ex.Message}", ex);
        }
    }

    public async Task<Result<IReadOnlyList<Note>>> GetAllAsync(bool includeDeleted = false, CancellationToken ct = default)
    {
        try
        {
            IQueryable<Note> q = _db.Notes
                .Include(n => n.Notebook)
                .Include(n => n.NoteTags).ThenInclude(nt => nt.Tag);
            if (!includeDeleted) q = q.Where(n => !n.IsDeleted);
            var list = await q.OrderByDescending(n => n.IsPinned)
                              .ThenByDescending(n => n.UpdatedAt)
                              .ToListAsync(ct);
            return Result<IReadOnlyList<Note>>.Ok(list);
        }
        catch (Exception ex)
        {
            return Result<IReadOnlyList<Note>>.Fail($"列表失败: {ex.Message}", ex);
        }
    }

    public async Task<Result<IReadOnlyList<Note>>> GetByNotebookAsync(long notebookId, bool recursive = false, CancellationToken ct = default)
    {
        try
        {
            var ids = new List<long> { notebookId };
            if (recursive)
            {
                var queue = new Queue<long>();
                queue.Enqueue(notebookId);
                while (queue.Count > 0)
                {
                    var current = queue.Dequeue();
                    var children = await _db.Notebooks
                        .Where(nb => nb.ParentId == current)
                        .Select(nb => nb.Id)
                        .ToListAsync(ct);
                    foreach (var c in children)
                    {
                        if (!ids.Contains(c))
                        {
                            ids.Add(c);
                            queue.Enqueue(c);
                        }
                    }
                }
            }
            var notes = await _db.Notes
                .Where(n => ids.Contains(n.NotebookId ?? -1) && !n.IsDeleted)
                .OrderByDescending(n => n.UpdatedAt)
                .ToListAsync(ct);
            return Result<IReadOnlyList<Note>>.Ok(notes);
        }
        catch (Exception ex)
        {
            return Result<IReadOnlyList<Note>>.Fail($"按笔记本查询失败: {ex.Message}", ex);
        }
    }

    public async Task<Result<Note>> UpdateAsync(long id, string? title, string? content, long? notebookId, bool? isPinned, CancellationToken ct = default)
    {
        try
        {
            var note = await _db.Notes.FindAsync(new object?[] { id }, ct);
            if (note is null) return Result<Note>.Fail($"未找到笔记 #{id}");
            if (title is not null) note.Title = title.Trim();
            if (content is not null) note.Content = content;
            if (notebookId.HasValue) note.NotebookId = notebookId.Value == 0 ? null : notebookId;
            if (isPinned.HasValue) note.IsPinned = isPinned.Value;
            note.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            return Result<Note>.Ok(note);
        }
        catch (Exception ex)
        {
            return Result<Note>.Fail($"更新失败: {ex.Message}", ex);
        }
    }

    public async Task<Result<bool>> SoftDeleteAsync(long id, CancellationToken ct = default)
    {
        try
        {
            var note = await _db.Notes.FindAsync(new object?[] { id }, ct);
            if (note is null) return Result<bool>.Fail($"未找到笔记 #{id}");
            note.IsDeleted = true;
            note.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail($"删除失败: {ex.Message}", ex);
        }
    }

    public async Task<Result<bool>> RestoreAsync(long id, CancellationToken ct = default)
    {
        try
        {
            var note = await _db.Notes.IgnoreQueryFilters()
                .FirstOrDefaultAsync(n => n.Id == id, ct);
            if (note is null) return Result<bool>.Fail($"未找到笔记 #{id}");
            note.IsDeleted = false;
            note.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail($"恢复失败: {ex.Message}", ex);
        }
    }

    public async Task<Result<bool>> HardDeleteAsync(long id, CancellationToken ct = default)
    {
        try
        {
            var note = await _db.Notes.FindAsync(new object?[] { id }, ct);
            if (note is null) return Result<bool>.Fail($"未找到笔记 #{id}");
            _db.Notes.Remove(note);
            await _db.SaveChangesAsync(ct);
            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail($"永久删除失败: {ex.Message}", ex);
        }
    }

    public async Task<Result<bool>> TogglePinAsync(long id, CancellationToken ct = default)
    {
        try
        {
            var note = await _db.Notes.FindAsync(new object?[] { id }, ct);
            if (note is null) return Result<bool>.Fail($"未找到笔记 #{id}");
            note.IsPinned = !note.IsPinned;
            note.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            return Result<bool>.Ok(note.IsPinned);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail($"切换置顶失败: {ex.Message}", ex);
        }
    }

    public async Task<Result<bool>> SetTagsAsync(long noteId, IEnumerable<string> tagNames, CancellationToken ct = default)
    {
        try
        {
            var note = await _db.Notes
                .Include(n => n.NoteTags)
                .FirstOrDefaultAsync(n => n.Id == noteId, ct);
            if (note is null) return Result<bool>.Fail($"未找到笔记 #{noteId}");

            var names = (tagNames ?? Array.Empty<string>())
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n.Trim())
                .Distinct()
                .ToList();

            note.NoteTags.Clear();

            foreach (var name in names)
            {
                var tagRes = await _tags.CreateOrGetAsync(name, null, ct);
                if (!tagRes.IsSuccess) return Result<bool>.Fail(tagRes.Error!);
                _db.NoteTags.Add(new NoteTag { NoteId = noteId, TagId = tagRes.Value!.Id });
            }

            note.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail($"设置标签失败: {ex.Message}", ex);
        }
    }
}
