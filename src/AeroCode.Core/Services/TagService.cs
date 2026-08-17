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

public class TagService : ITagService
{
    private readonly AeroCodeDbContext _db;
    public TagService(AeroCodeDbContext db) { _db = db; }

    public async Task<Result<Tag>> CreateOrGetAsync(string name, string? color = null, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(name)) return Result<Tag>.Fail("标签名不能为空");
            var normalized = name.Trim().ToLowerInvariant();

            var existing = await _db.Tags.FirstOrDefaultAsync(t => t.Name == normalized, ct);
            if (existing is not null) return Result<Tag>.Ok(existing);

            var tag = new Tag { Name = normalized, Color = color, CreatedAt = DateTime.UtcNow };
            _db.Tags.Add(tag);
            await _db.SaveChangesAsync(ct);
            return Result<Tag>.Ok(tag);
        }
        catch (Exception ex)
        {
            return Result<Tag>.Fail($"创建标签失败: {ex.Message}", ex);
        }
    }

    public async Task<Result<IReadOnlyList<Tag>>> GetAllAsync(CancellationToken ct = default)
    {
        try
        {
            var tags = await _db.Tags.OrderBy(t => t.Name).ToListAsync(ct);
            return Result<IReadOnlyList<Tag>>.Ok(tags);
        }
        catch (Exception ex)
        {
            return Result<IReadOnlyList<Tag>>.Fail($"查询失败: {ex.Message}", ex);
        }
    }

    public async Task<Result<bool>> DeleteAsync(long id, CancellationToken ct = default)
    {
        try
        {
            var tag = await _db.Tags.FindAsync(new object?[] { id }, ct);
            if (tag is null) return Result<bool>.Fail($"未找到标签 #{id}");
            _db.Tags.Remove(tag);
            await _db.SaveChangesAsync(ct);
            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail($"删除失败: {ex.Message}", ex);
        }
    }

    public async Task<Result<IReadOnlyList<Note>>> GetNotesByTagAsync(long tagId, CancellationToken ct = default)
    {
        try
        {
            var notes = await _db.Notes
                .Where(n => !n.IsDeleted && n.NoteTags.Any(nt => nt.TagId == tagId))
                .OrderByDescending(n => n.UpdatedAt)
                .ToListAsync(ct);
            return Result<IReadOnlyList<Note>>.Ok(notes);
        }
        catch (Exception ex)
        {
            return Result<IReadOnlyList<Note>>.Fail($"查询失败: {ex.Message}", ex);
        }
    }
}
