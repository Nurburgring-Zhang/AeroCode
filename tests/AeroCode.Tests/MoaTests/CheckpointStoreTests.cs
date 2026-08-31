// Copyright (c) AeroCode
// CheckpointStore 真实文件系统验证：快照目录/manifest/Restore/Prune 全部落盘可回读，零 mock。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using AeroAgent.Moa.Tools.Workspace;
using Xunit;

namespace AeroCode.Tests.MoaTests;

/// <summary>
/// 检查点存储钉子：Track 落真实 .bak 与 manifest.json，Restore 回读快照还原/删除，
/// 超大文件跳过不伪造恢复，Prune 按数量真实删除目录。
/// </summary>
public sealed class CheckpointStoreTests : IDisposable
{
    private readonly string _dir;

    public CheckpointStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"cpstore_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // 临时目录清理失败不影响测试结论
        }
    }

    private JsonDocument ReadManifest(long seq)
    {
        var manifestPath = Path.Combine(_dir, seq.ToString("D6"), "manifest.json");
        Assert.True(File.Exists(manifestPath), $"manifest 应存在：{manifestPath}");
        return JsonDocument.Parse(File.ReadAllText(manifestPath));
    }

    [Fact]
    public void Track_NewFile_ManifestRecordsNotExisted()
    {
        var store = new CheckpointStore(_dir);
        var target = Path.Combine(_dir, "new.txt");

        var seq = store.Track("write_file", new[] { target });

        Assert.Equal(1, seq);
        using var manifest = ReadManifest(seq);
        var files = manifest.RootElement.GetProperty("files");
        Assert.Equal(1, files.GetArrayLength());
        Assert.False(files[0].GetProperty("Existed").GetBoolean());
        Assert.Equal(target, files[0].GetProperty("Path").GetString());
        Assert.Equal("write_file", manifest.RootElement.GetProperty("toolName").GetString());
        // 新文件没有 .bak（无可捕获内容）
        Assert.Empty(Directory.GetFiles(Path.Combine(_dir, "000001"), "*.bak"));
    }

    [Fact]
    public void Track_ExistingFile_BackupCapturesCurrentContent()
    {
        var store = new CheckpointStore(_dir);
        var target = Path.Combine(_dir, "f.txt");
        File.WriteAllText(target, "v1");

        var seq = store.Track("edit_file", new[] { target });
        var bak = Path.Combine(_dir, "000001", "0.bak");

        Assert.True(File.Exists(bak));
        Assert.Equal("v1", File.ReadAllText(bak));
    }

    [Fact]
    public void Track_MultiplePaths_CapturesAllInOrder()
    {
        var store = new CheckpointStore(_dir);
        var a = Path.Combine(_dir, "a.txt");
        var b = Path.Combine(_dir, "b.txt");
        File.WriteAllText(a, "A");
        File.WriteAllText(b, "B");

        var seq = store.Track("write_file", new[] { a, b });
        var cpDir = Path.Combine(_dir, "000001");

        Assert.Equal(2, Directory.GetFiles(cpDir, "*.bak").Length);
        Assert.Equal("A", File.ReadAllText(Path.Combine(cpDir, "0.bak")));
        Assert.Equal("B", File.ReadAllText(Path.Combine(cpDir, "1.bak")));
        using var manifest = ReadManifest(seq);
        Assert.Equal(2, manifest.RootElement.GetProperty("files").GetArrayLength());
    }

    [Fact]
    public void Track_SequenceIncrements_AcrossCalls()
    {
        var store = new CheckpointStore(_dir);

        var s1 = store.Track("write_file", new[] { Path.Combine(_dir, "1.txt") });
        var s2 = store.Track("edit_file", new[] { Path.Combine(_dir, "2.txt") });

        Assert.Equal(1, s1);
        Assert.Equal(2, s2);
        Assert.True(Directory.Exists(Path.Combine(_dir, "000001")));
        Assert.True(Directory.Exists(Path.Combine(_dir, "000002")));
    }

    [Fact]
    public void Restore_RollbacksContent()
    {
        var store = new CheckpointStore(_dir);
        var target = Path.Combine(_dir, "f.txt");
        File.WriteAllText(target, "v1");
        var seq = store.Track("edit_file", new[] { target });

        File.WriteAllText(target, "v2-修改后");
        var restored = store.Restore(seq);

        Assert.Equal(1, restored);
        Assert.Equal("v1", File.ReadAllText(target));
    }

    [Fact]
    public void Restore_DeletesFileCreatedAfterCheckpoint()
    {
        var store = new CheckpointStore(_dir);
        var target = Path.Combine(_dir, "created.txt");
        var seq = store.Track("write_file", new[] { target }); // 捕获时不存在

        File.WriteAllText(target, "after");
        var restored = store.Restore(seq);

        Assert.Equal(1, restored);
        Assert.False(File.Exists(target));
    }

    [Fact]
    public void Restore_MultiPath_RestoresBoth()
    {
        var store = new CheckpointStore(_dir);
        var a = Path.Combine(_dir, "a.txt");
        var b = Path.Combine(_dir, "b.txt");
        File.WriteAllText(a, "A1");
        File.WriteAllText(b, "B1");
        var seq = store.Track("write_file", new[] { a, b });

        File.WriteAllText(a, "A2");
        File.WriteAllText(b, "B2");
        var restored = store.Restore(seq);

        Assert.Equal(2, restored);
        Assert.Equal("A1", File.ReadAllText(a));
        Assert.Equal("B1", File.ReadAllText(b));
    }

    [Fact]
    public void Restore_OversizeFile_SkippedHonest()
    {
        // >16MB 文件不入快照：Restore 如实跳过（不覆盖、不删除、不计入恢复数）。
        var store = new CheckpointStore(_dir);
        var big = Path.Combine(_dir, "big.bin");
        using (var fs = File.Create(big))
        {
            fs.SetLength(CheckpointStore.MaxCapturedBytes + 1);
        }

        var seq = store.Track("write_file", new[] { big });
        Assert.Empty(Directory.GetFiles(Path.Combine(_dir, "000001"), "*.bak"));

        File.WriteAllText(big, "changed");
        var restored = store.Restore(seq);

        Assert.Equal(0, restored);
        Assert.Equal("changed", File.ReadAllText(big));
    }

    [Fact]
    public void Restore_MissingSeq_ThrowsFileNotFound()
    {
        var store = new CheckpointStore(_dir);

        var ex = Assert.Throws<FileNotFoundException>(() => store.Restore(999999));
        Assert.Contains("999999", ex.Message);
    }

    [Fact]
    public void List_NewestFirst_WithToolAndPaths()
    {
        var store = new CheckpointStore(_dir);
        var a = Path.Combine(_dir, "a.txt");
        var b = Path.Combine(_dir, "b.txt");
        store.Track("write_file", new[] { a });
        store.Track("edit_file", new[] { b });

        var list = store.List();

        Assert.Equal(2, list.Count);
        Assert.Equal(2, list[0].Seq);
        Assert.Equal("edit_file", list[0].ToolName);
        Assert.Equal(1, list[1].Seq);
        Assert.Equal("write_file", list[1].ToolName);
        Assert.Equal(a, list[1].Paths.Single());
    }

    [Fact]
    public void Prune_ByCount_KeepsNewestThree()
    {
        var store = new CheckpointStore(_dir, maxCount: 3);
        for (var i = 1; i <= 5; i++)
        {
            store.Track("write_file", new[] { Path.Combine(_dir, $"f{i}.txt") });
        }

        var dirs = Directory.GetDirectories(_dir).Select(Path.GetFileName).ToList();
        Assert.Equal(3, dirs.Count);
        Assert.Contains("000003", dirs);
        Assert.Contains("000004", dirs);
        Assert.Contains("000005", dirs);
        Assert.DoesNotContain("000001", dirs);
        Assert.DoesNotContain("000002", dirs);

        var seqs = store.List().Select(c => c.Seq).ToList();
        Assert.Equal(new[] { 5L, 4L, 3L }, seqs);
    }

    [Fact]
    public void Constructor_EmptyRoot_Throws()
    {
        Assert.Throws<ArgumentException>(() => new CheckpointStore(""));
    }

    [Fact]
    public void Track_EmptyPathList_Throws()
    {
        var store = new CheckpointStore(_dir);

        Assert.Throws<ArgumentException>(
            () => store.Track("write_file", new List<string>()));
    }

    [Fact]
    public void Store_RehydratesSeqFromExistingDirectories()
    {
        // 重建实例后序号接续，不覆盖已有检查点（App 重启场景）。
        var first = new CheckpointStore(_dir);
        first.Track("write_file", new[] { Path.Combine(_dir, "x.txt") });

        var second = new CheckpointStore(_dir);
        var seq = second.Track("edit_file", new[] { Path.Combine(_dir, "y.txt") });

        Assert.Equal(2, seq);
        Assert.Equal(2, second.List().Count);
    }
}
