using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AeroCode.Core.Common;
using AeroCode.Core.Models;

namespace AeroCode.Core.Services;

public interface INoteService
{
    Task<Result<Note>> CreateAsync(string title, string content, long? notebookId = null, CancellationToken ct = default);
    Task<Result<Note>> GetByIdAsync(long id, CancellationToken ct = default);
    Task<Result<IReadOnlyList<Note>>> GetAllAsync(bool includeDeleted = false, CancellationToken ct = default);
    Task<Result<IReadOnlyList<Note>>> GetByNotebookAsync(long notebookId, bool recursive = false, CancellationToken ct = default);
    Task<Result<Note>> UpdateAsync(long id, string? title, string? content, long? notebookId, bool? isPinned, CancellationToken ct = default);
    Task<Result<bool>> SoftDeleteAsync(long id, CancellationToken ct = default);
    Task<Result<bool>> RestoreAsync(long id, CancellationToken ct = default);
    Task<Result<bool>> HardDeleteAsync(long id, CancellationToken ct = default);
    Task<Result<bool>> TogglePinAsync(long id, CancellationToken ct = default);
    Task<Result<bool>> SetTagsAsync(long noteId, IEnumerable<string> tagNames, CancellationToken ct = default);
}
