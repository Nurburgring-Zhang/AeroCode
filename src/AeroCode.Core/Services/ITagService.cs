using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AeroCode.Core.Common;
using AeroCode.Core.Models;

namespace AeroCode.Core.Services;

public interface ITagService
{
    Task<Result<Tag>> CreateOrGetAsync(string name, string? color = null, CancellationToken ct = default);
    Task<Result<IReadOnlyList<Tag>>> GetAllAsync(CancellationToken ct = default);
    Task<Result<bool>> DeleteAsync(long id, CancellationToken ct = default);
    Task<Result<IReadOnlyList<Note>>> GetNotesByTagAsync(long tagId, CancellationToken ct = default);
}
