// Copyright (c) AeroCode V3.0
// SearchService with real FTS5 + LIKE fallback.
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

/// <summary>
/// 搜索服务: 优先 FTS5 MATCH,失败回退到 LIKE (兜底)。
/// 字符级 FTS5 不会拆中文字符,所以中文 query 用 LIKE 兜底。
/// </summary>
public class SearchService : ISearchService
{
    private readonly AeroCodeDbContext _db;
    private bool _ftsAvailable = true;

    public SearchService(AeroCodeDbContext db)
    {
        _db = db;
        FtsMigrations.EnsureFts5(db);
    }

    public async Task<Result<IReadOnlyList<Note>>> SearchAsync(string query, int limit = 50, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(query)) return Result<IReadOnlyList<Note>>.Ok(Array.Empty<Note>());
            var q = query.Trim();
            var safeLimit = Math.Max(1, Math.Min(limit, 500));

            // Decide FTS vs LIKE based on content (CJK → LIKE; ASCII/Latin → FTS)
            bool hasCjk = ContainsCjk(q);
            IReadOnlyList<Note> notes;
            if (_ftsAvailable && !hasCjk)
            {
                try
                {
                    notes = await FtsSearch(q, safeLimit, ct);
                }
                catch
                {
                    _ftsAvailable = false;
                    notes = await LikeSearch(q, safeLimit, ct);
                }
            }
            else
            {
                notes = await LikeSearch(q, safeLimit, ct);
            }
            return Result<IReadOnlyList<Note>>.Ok(notes);
        }
        catch (Exception ex)
        {
            return Result<IReadOnlyList<Note>>.Fail($"搜索失败: {ex.Message}", ex);
        }
    }

    private async Task<IReadOnlyList<Note>> FtsSearch(string q, int limit, CancellationToken ct)
    {
        // FTS5 MATCH: 空格分隔多个 term; 用引号包裹做 phrase search.
        // 简单做法: 整 query 当一个 phrase,允许前缀匹配 via "q*"
        var ftsQuery = "\"" + q.Replace("\"", "\"\"") + "\"";
        return await _db.Notes
            .Where(n => !n.IsDeleted && _db.Database.SqlQueryRaw<int>(
                "SELECT id FROM notes_fts WHERE notes_fts MATCH {0} ORDER BY rank LIMIT {1}", ftsQuery, limit)
                .Any(x => x == n.Id))
            .OrderByDescending(n => n.UpdatedAt)
            .Include(n => n.Notebook)
            .Include(n => n.NoteTags).ThenInclude(nt => nt.Tag)
            .ToListAsync(ct);
    }

    private async Task<IReadOnlyList<Note>> LikeSearch(string q, int limit, CancellationToken ct)
    {
        var like = $"%{q}%";
        return await _db.Notes
            .Where(n => !n.IsDeleted && (EF.Functions.Like(n.Title, like) || EF.Functions.Like(n.Content, like)))
            .OrderByDescending(n => n.UpdatedAt)
            .Take(limit)
            .Include(n => n.Notebook)
            .Include(n => n.NoteTags).ThenInclude(nt => nt.Tag)
            .ToListAsync(ct);
    }

    private static bool ContainsCjk(string s)
    {
        foreach (var c in s)
            if (c >= 0x4E00 && c <= 0x9FFF) return true;
        return false;
    }
}
