// Copyright (c) AeroCode V3.0
// FileSnapshotStore — captures file contents before a fix so a failed fix can be rolled back.
namespace AeroCode.Harness.Loop;

/// <summary>
/// A captured snapshot of a set of files. <see cref="Rollback"/> restores every file to
/// its exact pre-fix content (and deletes files that did not exist before the fix);
/// <see cref="Commit"/> finalizes the snapshot (no-op, the originals are released).
/// </summary>
public sealed class FileSnapshot
{
    // null value = the file did not exist at capture time.
    private readonly Dictionary<string, string?> _originals;
    private bool _settled;

    internal FileSnapshot(Dictionary<string, string?> originals)
    {
        _originals = originals;
    }

    /// <summary>Absolute paths captured by this snapshot.</summary>
    public IReadOnlyCollection<string> CapturedPaths => _originals.Keys;

    /// <summary>True until Commit/Rollback has been called.</summary>
    public bool IsActive => !_settled;

    /// <summary>
    /// Restore every captured file to its original content. Files created after the
    /// snapshot (not present at capture time) are deleted. Idempotent.
    /// </summary>
    public void Rollback()
    {
        if (_settled) return;
        _settled = true;
        foreach (var (path, original) in _originals)
        {
            if (original is null)
            {
                if (File.Exists(path)) File.Delete(path);
            }
            else
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path, original);
            }
        }
    }

    /// <summary>Keep the current file state; releases the snapshot. Idempotent.</summary>
    public void Commit() => _settled = true;
}

/// <summary>Creates <see cref="FileSnapshot"/> instances for a set of file paths.</summary>
public static class FileSnapshotStore
{
    /// <summary>
    /// Capture the current content of every path (absolute paths). Missing files are
    /// recorded as "did not exist" so rollback can delete them if the fix creates them.
    /// </summary>
    public static FileSnapshot Capture(IEnumerable<string> absolutePaths)
    {
        ArgumentNullException.ThrowIfNull(absolutePaths);
        var originals = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in absolutePaths)
        {
            var path = Path.GetFullPath(raw);
            if (originals.ContainsKey(path)) continue;
            originals[path] = File.Exists(path) ? File.ReadAllText(path) : null;
        }
        return new FileSnapshot(originals);
    }
}
