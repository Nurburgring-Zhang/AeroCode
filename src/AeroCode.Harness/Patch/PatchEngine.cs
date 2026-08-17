// Copyright (c) AeroCode V3.0
// PatchEngine — OpenCode + Cursor-style code patching.
using System.Text;
using System.Text.RegularExpressions;

namespace AeroCode.Harness.Patch;

/// <summary>Type of patch operation.</summary>
public enum PatchKind
{
    /// <summary>Replace an exact (or fuzzy) match.</summary>
    Replace,
    /// <summary>Insert at a specific line.</summary>
    Insert,
    /// <summary>Delete a line range.</summary>
    Delete,
}

/// <summary>A single patch operation.</summary>
public sealed class Patch
{
    public required string FilePath { get; init; }
    public required PatchKind Kind { get; init; }
    public string? OldText { get; init; }
    public string? NewText { get; init; }
    public int? LineNumber { get; init; }
    public bool Fuzzy { get; init; } = true;
    public string? Description { get; init; }
}

/// <summary>Result of applying patches.</summary>
public sealed record PatchResult(
    int Applied,
    int Failed,
    int Skipped,
    IReadOnlyList<string> Errors,
    string? NewContent);

/// <summary>
/// Patch engine (OpenCode + Cursor Fast Apply + Hermes self-patch).
/// Supports search/replace (exact + fuzzy) and line-based operations.
/// </summary>
public sealed class PatchEngine
{
    public const int MaxLinesPerPatch = 200;  // Google Small CLs
    public const int MaxFilesPerPatch = 10;

    /// <summary>Apply a single patch to file content. Returns the new content (or null on failure).</summary>
    public (bool ok, string? newContent, string? error) Apply(string originalContent, Patch patch)
    {
        return patch.Kind switch
        {
            PatchKind.Replace => ApplyReplace(originalContent, patch),
            PatchKind.Insert => ApplyInsert(originalContent, patch),
            PatchKind.Delete => ApplyDelete(originalContent, patch),
            _ => (false, null, $"Unknown patch kind: {patch.Kind}"),
        };
    }

    private static (bool, string?, string?) ApplyReplace(string content, Patch patch)
    {
        if (string.IsNullOrEmpty(patch.OldText))
            return (false, null, "OldText is required for Replace");

        if (content.Contains(patch.OldText))
        {
            return (true, content.Replace(patch.OldText, patch.NewText ?? string.Empty), null);
        }

        if (patch.Fuzzy)
        {
            var fuzzyMatch = FuzzyFind(content, patch.OldText);
            if (fuzzyMatch is not null)
            {
                return (true, content.Replace(fuzzyMatch, patch.NewText ?? string.Empty), "Fuzzy match applied");
            }
        }

        return (false, null, "OldText not found in content");
    }

    private static (bool, string?, string?) ApplyInsert(string content, Patch patch)
    {
        if (patch.LineNumber is null)
            return (false, null, "LineNumber is required for Insert");
        var lines = content.Split('\n').ToList();
        var idx = Math.Clamp(patch.LineNumber.Value, 0, lines.Count);
        lines.Insert(idx, patch.NewText ?? string.Empty);
        return (true, string.Join('\n', lines), null);
    }

    private static (bool, string?, string?) ApplyDelete(string content, Patch patch)
    {
        if (patch.LineNumber is null)
            return (false, null, "LineNumber is required for Delete");
        var lines = content.Split('\n').ToList();
        if (patch.LineNumber.Value < 0 || patch.LineNumber.Value >= lines.Count)
            return (false, null, "LineNumber out of range");
        lines.RemoveAt(patch.LineNumber.Value);
        return (true, string.Join('\n', lines), null);
    }

    private static string? FuzzyFind(string content, string search)
    {
        // Simple fuzzy: collapse all whitespace and try to find a substring.
        var normalizedContent = Regex.Replace(content, @"\s+", " ");
        var normalizedSearch = Regex.Replace(search, @"\s+", " ");
        var idx = normalizedContent.IndexOf(normalizedSearch, StringComparison.Ordinal);
        if (idx < 0) return null;

        // Map back to the original content (find the matching span).
        var before = normalizedContent.Substring(0, idx);
        var originalStart = MapNormalizedToOriginal(content, before.Length);
        return content.Substring(originalStart, search.Length + SlackFor(originalContent: content, originalStart, search.Length));
    }

    private static int SlackFor(string originalContent, int originalStart, int searchLength)
    {
        // Allow a small slack to account for whitespace differences.
        return Math.Min(50, originalContent.Length - originalStart - searchLength);
    }

    private static int MapNormalizedToOriginal(string original, int normalizedPos)
    {
        var origIdx = 0;
        var normIdx = 0;
        var inWhitespace = false;
        while (origIdx < original.Length && normIdx < normalizedPos)
        {
            if (char.IsWhiteSpace(original[origIdx]))
            {
                if (!inWhitespace)
                {
                    normIdx++;
                    inWhitespace = true;
                }
            }
            else
            {
                normIdx++;
                inWhitespace = false;
            }
            origIdx++;
        }
        return Math.Min(origIdx, original.Length);
    }

    /// <summary>Validate a patch (Google Small CLs).</summary>
    public static (bool ok, string? reason) ValidateSize(string filePath, int lineCount, int fileCount)
    {
        if (lineCount > MaxLinesPerPatch)
            return (false, $"Patch too large: {lineCount} lines > {MaxLinesPerPatch}. Consider splitting (Google Small CLs).");
        if (fileCount > MaxFilesPerPatch)
            return (false, $"Too many files: {fileCount} > {MaxFilesPerPatch}. Consider splitting.");
        return (true, null);
    }

    /// <summary>Apply a batch of patches to multiple files atomically (with backup).</summary>
    public PatchResult ApplyBatch(IReadOnlyList<(string path, Patch patch)> patches, string rootDir)
    {
        var errors = new List<string>();
        var applied = 0;
        var failed = 0;
        var skipped = 0;
        string? lastNewContent = null;

        // Validate batch size (Google Small CLs).
        var totalLines = patches.Sum(p =>
        {
            var content = ReadFile(Path.Combine(rootDir, p.path));
            return content?.Split('\n').Length ?? 0;
        });
        var (sizeOk, sizeReason) = ValidateSize("batch", totalLines, patches.Count);
        if (!sizeOk) return new PatchResult(0, 0, patches.Count, new[] { sizeReason ?? "Size validation failed" }, null);

        var backups = new Dictionary<string, string?>();
        try
        {
            foreach (var (relPath, patch) in patches)
            {
                var absPath = Path.Combine(rootDir, relPath);
                var original = ReadFile(absPath);
                if (original is null)
                {
                    errors.Add($"File not found: {relPath}");
                    failed++;
                    continue;
                }

                backups[absPath] = original;
                var (ok, newContent, error) = Apply(original, patch);
                if (ok && newContent is not null)
                {
                    WriteFile(absPath, newContent);
                    applied++;
                    lastNewContent = newContent;
                }
                else
                {
                    errors.Add($"{relPath}: {error}");
                    failed++;
                }
            }
        }
        catch (Exception ex)
        {
            // Rollback all applied patches
            foreach (var (absPath, original) in backups)
            {
                if (original is not null)
                {
                    try { WriteFile(absPath, original); } catch { }
                }
            }
            var rollbackErrors = new List<string> { $"Batch failed, rolled back: {ex.Message}" };
            rollbackErrors.AddRange(errors);
            return new PatchResult(0, applied, skipped, rollbackErrors, null);
        }

        return new PatchResult(applied, failed, skipped, errors, lastNewContent);
    }

    private static string? ReadFile(string path)
    {
        try { return File.ReadAllText(path); } catch { return null; }
    }

    private static void WriteFile(string path, string content)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(path, content);
    }
}
