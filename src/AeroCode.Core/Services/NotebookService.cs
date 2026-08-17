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

public class NotebookService : INotebookService
{
    private readonly AeroCodeDbContext _db;

    public NotebookService(AeroCodeDbContext db) { _db = db; }

    public async Task<Result<Notebook>> CreateAsync(string name, string? description, long? parentId, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(name)) return Result<Notebook>.Fail("笔记本名不能为空");
            if (parentId.HasValue && parentId.Value > 0)
            {
                var parent = await _db.Notebooks.FindAsync(new object?[] { parentId.Value }, ct);
                if (parent is null) return Result<Notebook>.Fail($"父笔记本 #{parentId} 不存在");
            }

            var maxOrder = await _db.Notebooks
                .Where(nb => nb.ParentId == (parentId == 0 ? null : parentId))
                .MaxAsync(nb => (int?)nb.SortOrder, ct) ?? -1;

            var nb = new Notebook
            {
                Name = name.Trim(),
                Description = description?.Trim(),
                ParentId = parentId == 0 ? null : parentId,
                SortOrder = maxOrder + 1,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            _db.Notebooks.Add(nb);
            await _db.SaveChangesAsync(ct);
            return Result<Notebook>.Ok(nb);
        }
        catch (Exception ex)
        {
            return Result<Notebook>.Fail($"创建笔记本失败: {ex.Message}", ex);
        }
    }

    public async Task<Result<Notebook>> GetByIdAsync(long id, CancellationToken ct = default)
    {
        try
        {
            var nb = await _db.Notebooks
                .Include(n => n.Children)
                .FirstOrDefaultAsync(n => n.Id == id, ct);
            return nb is null
                ? Result<Notebook>.Fail($"未找到笔记本 #{id}")
                : Result<Notebook>.Ok(nb);
        }
        catch (Exception ex)
        {
            return Result<Notebook>.Fail($"查询失败: {ex.Message}", ex);
        }
    }

    public async Task<Result<IReadOnlyList<Notebook>>> GetRootsAsync(CancellationToken ct = default)
    {
        try
        {
            var roots = await _db.Notebooks
                .Where(nb => nb.ParentId == null)
                .OrderBy(nb => nb.SortOrder)
                .ToListAsync(ct);
            return Result<IReadOnlyList<Notebook>>.Ok(roots);
        }
        catch (Exception ex)
        {
            return Result<IReadOnlyList<Notebook>>.Fail($"查询根失败: {ex.Message}", ex);
        }
    }

    public async Task<Result<IReadOnlyList<Notebook>>> GetChildrenAsync(long parentId, CancellationToken ct = default)
    {
        try
        {
            var children = await _db.Notebooks
                .Where(nb => nb.ParentId == parentId)
                .OrderBy(nb => nb.SortOrder)
                .ToListAsync(ct);
            return Result<IReadOnlyList<Notebook>>.Ok(children);
        }
        catch (Exception ex)
        {
            return Result<IReadOnlyList<Notebook>>.Fail($"查询子失败: {ex.Message}", ex);
        }
    }

    public async Task<Result<Notebook>> UpdateAsync(long id, string? name, string? description, int? sortOrder, CancellationToken ct = default)
    {
        try
        {
            var nb = await _db.Notebooks.FindAsync(new object?[] { id }, ct);
            if (nb is null) return Result<Notebook>.Fail($"未找到笔记本 #{id}");
            if (name is not null) nb.Name = name.Trim();
            if (description is not null) nb.Description = description.Trim();
            if (sortOrder.HasValue) nb.SortOrder = sortOrder.Value;
            nb.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            return Result<Notebook>.Ok(nb);
        }
        catch (Exception ex)
        {
            return Result<Notebook>.Fail($"更新失败: {ex.Message}", ex);
        }
    }

    public async Task<Result<bool>> DeleteAsync(long id, bool cascade = false, CancellationToken ct = default)
    {
        try
        {
            var nb = await _db.Notebooks.FindAsync(new object?[] { id }, ct);
            if (nb is null) return Result<bool>.Fail($"未找到笔记本 #{id}");

            var hasChildren = await _db.Notebooks.AnyAsync(n => n.ParentId == id, ct);
            var hasNotes = await _db.Notes.AnyAsync(n => n.NotebookId == id, ct);

            if ((hasChildren || hasNotes) && !cascade)
                return Result<bool>.Fail("笔记本非空,需 cascade=true 强制删除");

            if (cascade)
            {
                if (hasNotes)
                {
                    var notes = await _db.Notes.Where(n => n.NotebookId == id).ToListAsync(ct);
                    _db.Notes.RemoveRange(notes);
                }
                if (hasChildren)
                {
                    var children = await _db.Notebooks.Where(n => n.ParentId == id).ToListAsync(ct);
                    foreach (var c in children)
                    {
                        var r = await DeleteAsync(c.Id, true, ct);
                        if (!r.IsSuccess) return r;
                    }
                }
            }
            _db.Notebooks.Remove(nb);
            await _db.SaveChangesAsync(ct);
            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail($"删除失败: {ex.Message}", ex);
        }
    }
}
