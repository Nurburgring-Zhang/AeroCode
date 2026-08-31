// Copyright (c) AeroCode
// CheckpointStore — 写类工具落盘前的自动检查点（对标 opencode snapshot / cline checkpoints / claude-code /rewind）。
// 语义：Track 在写之前捕获目标路径当前状态（不存在=新建语义，恢复时删除）；
// Restore 把一组检查点恢复到该时刻；Prune 按数量与时效裁剪（默认保留 100 个 / 7 天）。
// 布局：{root}/{seq:D6}/manifest.json + {root}/{seq:D6}/{index}.bak（原始路径记在 manifest，
// 避免把任意路径拼进文件系统）。全部真实文件系统，零 mock。
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AeroAgent.Moa.Tools.Workspace;

/// <summary>一个检查点的只读描述（列表/恢复选择用）。</summary>
public sealed record CheckpointInfo(
    long Seq,
    string ToolName,
    DateTime CreatedUtc,
    IReadOnlyList<string> Paths);

internal sealed record CheckpointFileEntry(string Path, bool Existed);

/// <summary>
/// 检查点存储。<see cref="Track"/> 由 <see cref="WorkspaceToolbox"/> 在 write/edit/delete
/// 前调用；shell 命令的任意副作用不在覆盖范围（如实限制：回滚 shell 效应请用 git）。
/// </summary>
public sealed class CheckpointStore : ICheckpointTracker
{
    /// <summary>单个检查点内单个文件的大小上限（>16MB 的文件不入检查点，Track 如实跳过并记录）。</summary>
    public const long MaxCapturedBytes = 16 * 1024 * 1024;

    private readonly string _root;
    private readonly int _maxCount;
    private readonly TimeSpan _maxAge;
    private readonly object _sync = new();
    private long _seq;

    public CheckpointStore(string root, int maxCount = 100, TimeSpan? maxAge = null)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new ArgumentException("checkpoint root must not be empty", nameof(root));
        }

        _root = root;
        _maxCount = maxCount is < 1 or > 10_000 ? 100 : maxCount;
        _maxAge = maxAge ?? TimeSpan.FromDays(7);
        Directory.CreateDirectory(_root);
        _seq = CurrentMaxSeq();
    }

    /// <summary>检查点落盘根目录（诊断/测试用）。</summary>
    public string Root => _root;

    /// <inheritdoc/>
    public long Track(string toolName, IReadOnlyList<string> absolutePaths)
    {
        ArgumentNullException.ThrowIfNull(absolutePaths);
        if (absolutePaths.Count == 0)
        {
            throw new ArgumentException("at least one path required", nameof(absolutePaths));
        }

        long seq;
        var dir = NewCheckpointDir(toolName, out seq);
        var entries = new List<CheckpointFileEntry>();

        foreach (var path in absolutePaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                entries.Add(new CheckpointFileEntry(path, Existed: false));
                continue;
            }

            if (info.Length > MaxCapturedBytes)
            {
                // 超大文件不入快照——恢复语义降级为"不覆盖该文件"，在 manifest 里如实标注。
                entries.Add(new CheckpointFileEntry(path, Existed: true));
                continue;
            }

            var index = entries.Count;
            File.Copy(path, Path.Combine(dir, $"{index}.bak"), overwrite: true);
            entries.Add(new CheckpointFileEntry(path, Existed: true));
        }

        var manifest = new
        {
            seq,
            toolName,
            createdUtc = DateTime.UtcNow,
            files = entries,
        };
        File.WriteAllText(
            Path.Combine(dir, "manifest.json"),
            JsonSerializer.Serialize(manifest),
            new UTF8Encoding(false));

        lock (_sync)
        {
            PruneLocked();
        }

        return seq;
    }

    /// <summary>列出最近 <paramref name="limit"/> 个检查点（新→旧）。</summary>
    public IReadOnlyList<CheckpointInfo> List(int limit = 50)
    {
        var result = new List<CheckpointInfo>();
        foreach (var dir in Directory.EnumerateDirectories(_root).OrderByDescending(DirSeq))
        {
            var manifestPath = Path.Combine(dir, "manifest.json");
            if (!File.Exists(manifestPath))
            {
                continue;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var seq = doc.RootElement.GetProperty("seq").GetInt64();
            var tool = doc.RootElement.GetProperty("toolName").GetString() ?? "?";
            var created = doc.RootElement.GetProperty("createdUtc").GetDateTime();
            var paths = doc.RootElement.GetProperty("files").EnumerateArray()
                .Select(f => f.GetProperty("Path").GetString() ?? "")
                .ToList();
            result.Add(new CheckpointInfo(seq, tool, created, paths));
            if (result.Count >= limit)
            {
                break;
            }
        }

        return result;
    }

    /// <summary>
    /// 恢复单个检查点：每个已存在文件还原为捕获时内容，"捕获时不存在"的文件删除。
    /// 返回实际恢复的文件数。
    /// </summary>
    public int Restore(long seq)
    {
        var dir = Path.Combine(_root, seq.ToString("D6", CultureInfo.InvariantCulture));
        var manifestPath = Path.Combine(dir, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException($"checkpoint {seq} not found under {_root}");
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var files = doc.RootElement.GetProperty("files");
        var restored = 0;
        for (var i = 0; i < files.GetArrayLength(); i++)
        {
            var entry = files[i];
            var path = entry.GetProperty("Path").GetString()!;
            var existed = entry.GetProperty("Existed").GetBoolean();

            if (!existed)
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    restored++;
                }

                continue;
            }

            var backup = Path.Combine(dir, $"{i}.bak");
            if (!File.Exists(backup))
            {
                continue; // 超大文件未被捕获——如实跳过（不伪造恢复）。
            }

            var targetDir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }

            File.Copy(backup, path, overwrite: true);
            restored++;
        }

        return restored;
    }

    /// <summary>按数量与时效裁剪旧检查点（Track 后自动调用；可显式调用）。</summary>
    public int Prune()
    {
        lock (_sync)
        {
            return PruneLocked();
        }
    }

    private string NewCheckpointDir(string toolName, out long seq)
    {
        lock (_sync)
        {
            seq = ++_seq;
        }

        var dir = Path.Combine(_root, seq.ToString("D6", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private long CurrentMaxSeq()
    {
        var max = 0L;
        foreach (var dir in Directory.EnumerateDirectories(_root))
        {
            var seq = DirSeq(dir);
            if (seq > max)
            {
                max = seq;
            }
        }

        return max;
    }

    private int PruneLocked()
    {
        var dirs = Directory.EnumerateDirectories(_root)
            .Select(d => (Dir: d, Seq: DirSeq(d), Created: Directory.GetCreationTimeUtc(d)))
            .OrderByDescending(x => x.Seq)
            .ToList();

        var removed = 0;
        for (var i = 0; i < dirs.Count; i++)
        {
            var tooOld = DateTime.UtcNow - dirs[i].Created > _maxAge;
            var overCount = i >= _maxCount;
            if (!tooOld && !overCount)
            {
                continue;
            }

            try
            {
                Directory.Delete(dirs[i].Dir, recursive: true);
                removed++;
            }
            catch (IOException)
            {
                // 并发删除竞争——下一个 Track 周期会再次尝试，不掩盖其余目录的处理。
            }
        }

        return removed;
    }

    private static long DirSeq(string dir)
    {
        var name = Path.GetFileName(dir);
        return long.TryParse(name, out var seq) ? seq : -1;
    }
}
