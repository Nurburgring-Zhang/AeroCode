using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AeroCode.Core.Common;
using AeroCode.Core.Models;

namespace AeroCode.Core.Services;

public interface ISearchService
{
    /// <summary>
    /// 全文搜索：对 Title 和 Content 用 LIKE 查询（生产环境可替换为 SQLite FTS5 虚表）。
    /// </summary>
    Task<Result<IReadOnlyList<Note>>> SearchAsync(string query, int limit = 50, CancellationToken ct = default);
}
