using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AeroCode.Core.Common;
using AeroCode.Core.Models;

namespace AeroCode.Core.Services;

public interface INotebookService
{
    Task<Result<Notebook>> CreateAsync(string name, string? description = null, long? parentId = null, CancellationToken ct = default);
    Task<Result<Notebook>> GetByIdAsync(long id, CancellationToken ct = default);
    Task<Result<IReadOnlyList<Notebook>>> GetRootsAsync(CancellationToken ct = default);
    Task<Result<IReadOnlyList<Notebook>>> GetChildrenAsync(long parentId, CancellationToken ct = default);
    Task<Result<Notebook>> UpdateAsync(long id, string? name, string? description, int? sortOrder, CancellationToken ct = default);
    Task<Result<bool>> DeleteAsync(long id, bool cascade = false, CancellationToken ct = default);
}
