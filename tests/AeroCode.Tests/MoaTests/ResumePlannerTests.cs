// Copyright (c) AeroCode
// ResumePlanner 钉子：旧 manifest 兼容（无增量字段照常可读/可恢复）、最近有效定位、
// 幂等去重（重复调用结果一致 + 同路径只规划一次）、损坏 manifest 跳过取次新、
// 作用域 fail-closed（只解析显式 root，越界/错位即拒）。全部真实文件系统，零 mock。
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using AeroAgent.Moa.Tools.Workspace;
using Xunit;

namespace AeroCode.Tests.MoaTests;

public sealed class ResumePlannerTests : IDisposable
{
    private readonly string _dir;

    public ResumePlannerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"resumeplanner_{Guid.NewGuid():N}");
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

    /// <summary>手写一份旧格式 manifest（不含 A4 增量字段），并按需落 .bak。</summary>
    private string WriteLegacyManifest(
        long seq, string tool, params (string Path, bool Existed, string? BackupContent)[] files)
    {
        var cpDir = Path.Combine(_dir, seq.ToString("D6"));
        Directory.CreateDirectory(cpDir);
        var manifest = new
        {
            seq,
            toolName = tool,
            createdUtc = DateTime.UtcNow,
            files = files.Select(f => new { f.Path, f.Existed }).ToArray(),
        };
        File.WriteAllText(Path.Combine(cpDir, "manifest.json"), JsonSerializer.Serialize(manifest));
        for (var i = 0; i < files.Length; i++)
        {
            if (files[i].Existed && files[i].BackupContent is not null)
            {
                File.WriteAllText(Path.Combine(cpDir, $"{i}.bak"), files[i].BackupContent);
            }
        }

        return cpDir;
    }

    private static void AssertPlansEqual(ResumePlan a, ResumePlan b)
    {
        Assert.Equal(a.CheckpointSeq, b.CheckpointSeq);
        Assert.Equal(a.CheckpointDir, b.CheckpointDir);
        Assert.Equal(a.ToolName, b.ToolName);
        Assert.Equal(a.ToolResult, b.ToolResult);
        Assert.Equal(a.ContextSnapshotRef, b.ContextSnapshotRef);
        Assert.Equal(a.TaskStatus, b.TaskStatus);
        Assert.Equal(a.Actions.Count, b.Actions.Count);
        for (var i = 0; i < a.Actions.Count; i++)
        {
            Assert.Equal(a.Actions[i].Kind, b.Actions[i].Kind);
            Assert.Equal(a.Actions[i].FilePath, b.Actions[i].FilePath);
            Assert.Equal(a.Actions[i].BackupPath, b.Actions[i].BackupPath);
        }
    }

    // ---------- 旧 manifest 兼容（硬验收） ----------

    [Fact]
    public void LegacyManifest_BuildsPlan_WithoutIncrementalFields()
    {
        var f1 = Path.Combine(_dir, "a.txt");
        var f2 = Path.Combine(_dir, "new.txt");
        WriteLegacyManifest(1, "edit_file", (f1, true, "v1"), (f2, false, null));

        var plan = new ResumePlanner(_dir).TryBuildResumePlan();

        Assert.NotNull(plan);
        Assert.Equal(1, plan!.CheckpointSeq);
        Assert.Equal("edit_file", plan.ToolName);
        Assert.Null(plan.ToolResult); // 旧 manifest 无增量字段 → 如实为 null
        Assert.Null(plan.ContextSnapshotRef);
        Assert.Null(plan.TaskStatus);
        Assert.Equal(2, plan.Actions.Count);

        // LINQ Single（非 Assert.Single）：消除 xUnit2031（断言对可枚举形态的误判警告），语义等价。
        var restore = plan.Actions.Single(a => a.Kind == ResumeActionKind.RestoreContent);
        Assert.Equal(f1, restore.FilePath);
        Assert.Equal(Path.Combine(_dir, "000001", "0.bak"), restore.BackupPath);

        var delete = plan.Actions.Single(a => a.Kind == ResumeActionKind.DeleteCreated);
        Assert.Equal(f2, delete.FilePath);
    }

    [Fact]
    public void LegacyManifest_StoreListAndRestore_StillWork()
    {
        // 向后兼容硬验收：旧 manifest 照常可读、照常可恢复（CheckpointStore 既有行为不破坏）。
        var f1 = Path.Combine(_dir, "a.txt");
        var f2 = Path.Combine(_dir, "created.txt");
        WriteLegacyManifest(1, "write_file", (f1, true, "v1"), (f2, false, null));

        var store = new CheckpointStore(_dir);
        var list = store.List();
        Assert.Single(list);
        Assert.Equal("write_file", list[0].ToolName);
        Assert.Equal(f1, list[0].Paths[0]);

        File.WriteAllText(f1, "v2-修改后");
        File.WriteAllText(f2, "after");
        var restored = store.Restore(1);

        Assert.Equal(2, restored);
        Assert.Equal("v1", File.ReadAllText(f1));
        Assert.False(File.Exists(f2));
    }

    // ---------- 增量可选字段（新 Track 重载） ----------

    [Fact]
    public void Track_WithMetadata_ManifestCarriesOptionalFields()
    {
        var store = new CheckpointStore(_dir);
        var target = Path.Combine(_dir, "f.txt");
        File.WriteAllText(target, "v1");
        store.Track("edit_file", new[] { target },
            new CheckpointMetadata(ToolResult: "ok(3 chars)", ContextSnapshotRef: "ctx-42", TaskStatus: "running"));

        var plan = new ResumePlanner(_dir).TryBuildResumePlan();

        Assert.NotNull(plan);
        Assert.Equal("ok(3 chars)", plan!.ToolResult);
        Assert.Equal("ctx-42", plan.ContextSnapshotRef);
        Assert.Equal("running", plan.TaskStatus);
    }

    [Fact]
    public void Track_WithoutMetadata_ManifestStaysLegacyFormat()
    {
        // 字节级兼容：不带元数据时不落任何增量字段（旧格式逐字节一致）。
        var store = new CheckpointStore(_dir);
        store.Track("write_file", new[] { Path.Combine(_dir, "x.txt") });

        var raw = File.ReadAllText(Path.Combine(_dir, "000001", "manifest.json"));
        Assert.DoesNotContain("toolResult", raw);
        Assert.DoesNotContain("contextSnapshotRef", raw);
        Assert.DoesNotContain("taskStatus", raw);
    }

    // ---------- 最近有效定位 + 幂等去重 ----------

    [Fact]
    public void LatestValidCheckpoint_Selected_AndRepeatCallsIdentical()
    {
        var store = new CheckpointStore(_dir);
        store.Track("write_file", new[] { Path.Combine(_dir, "1.txt") });
        store.Track("edit_file", new[] { Path.Combine(_dir, "2.txt") });
        store.Track("write_file", new[] { Path.Combine(_dir, "3.txt") },
            new CheckpointMetadata(TaskStatus: "running"));

        var planner = new ResumePlanner(_dir);
        var first = planner.TryBuildResumePlan();
        var second = planner.TryBuildResumePlan();

        Assert.NotNull(first);
        Assert.Equal(3, first!.CheckpointSeq); // 最近有效 = 最大序号
        AssertPlansEqual(first, second!);      // 幂等：同一落盘状态重复调用逐项一致
    }

    [Fact]
    public void DuplicatePathEntries_DedupedToSingleAction()
    {
        // 幂等去重：manifest 里同一路径重复登记 → 只规划一次（首个条目为准）。
        var f1 = Path.Combine(_dir, "dup.txt");
        var cpDir = WriteLegacyManifest(1, "write_file", (f1, true, "v1"), (f1, true, null));

        var plan = new ResumePlanner(_dir).TryBuildResumePlan();

        Assert.NotNull(plan);
        var action = Assert.Single(plan!.Actions);
        Assert.Equal(ResumeActionKind.RestoreContent, action.Kind);
        Assert.Equal(f1, action.FilePath);
        Assert.Equal(Path.Combine(cpDir, "0.bak"), action.BackupPath);
    }

    // ---------- 损坏 manifest 跳过取次新 ----------

    [Fact]
    public void CorruptNewestManifest_SkippedToNextNewer()
    {
        WriteLegacyManifest(1, "write_file", (Path.Combine(_dir, "1.txt"), true, "v1"));
        WriteLegacyManifest(2, "write_file", (Path.Combine(_dir, "2.txt"), true, "v2"));
        var newestDir = WriteLegacyManifest(3, "write_file", (Path.Combine(_dir, "3.txt"), true, "v3"));
        File.WriteAllText(Path.Combine(newestDir, "manifest.json"), "{ not json {{{");

        var planner = new ResumePlanner(_dir);
        var plan = planner.TryBuildResumePlan();

        Assert.NotNull(plan);
        Assert.Equal(2, plan!.CheckpointSeq); // 最新损坏 → 跳过取次新
    }

    [Fact]
    public void ExplicitSeq_OnCorruptManifest_ReturnsNull_NoFallback()
    {
        // fail-closed：显式指定的 checkpoint 损坏 → 如实 null，不静默回退到其他检查点。
        var newestDir = WriteLegacyManifest(3, "write_file", (Path.Combine(_dir, "3.txt"), true, "v3"));
        File.WriteAllText(Path.Combine(newestDir, "manifest.json"), "{ not json {{{");
        WriteLegacyManifest(1, "write_file", (Path.Combine(_dir, "1.txt"), true, "v1"));

        var planner = new ResumePlanner(_dir);

        Assert.Null(planner.TryBuildResumePlan(3));
        Assert.NotNull(planner.TryBuildResumePlan(1)); // 有效的显式 seq 照常可解析
    }

    // ---------- 作用域 fail-closed ----------

    [Fact]
    public void Constructor_EmptyRoot_Throws()
    {
        Assert.Throws<ArgumentException>(() => new ResumePlanner(""));
        Assert.Throws<ArgumentException>(() => new ResumePlanner("   "));
    }

    [Fact]
    public void NonExistentRoot_ReturnsNull()
    {
        var planner = new ResumePlanner(Path.Combine(_dir, "no-such-root"));
        Assert.Null(planner.TryBuildResumePlan());
        Assert.Null(planner.TryBuildResumePlan(1));
    }

    [Fact]
    public void MissingExplicitSeq_ReturnsNull()
    {
        WriteLegacyManifest(1, "write_file", (Path.Combine(_dir, "1.txt"), true, "v1"));
        var planner = new ResumePlanner(_dir);

        Assert.Null(planner.TryBuildResumePlan(42)); // root 内不存在的序号：不越界、不回退
        Assert.Equal(1, planner.TryBuildResumePlan()!.CheckpointSeq);
    }

    [Fact]
    public void ManifestSeqDirMismatch_Rejected()
    {
        // 完整性校验：目录序号与 manifest seq 不一致（错位/拼接）→ 拒绝，不产出计划。
        var cpDir = WriteLegacyManifest(999999, "write_file", (Path.Combine(_dir, "x.txt"), true, "v1"));
        Directory.Move(cpDir, Path.Combine(_dir, "000005")); // 目录叫 000005，manifest 声称 seq=999999

        var planner = new ResumePlanner(_dir);

        Assert.Null(planner.TryBuildResumePlan());
        Assert.Null(planner.TryBuildResumePlan(5));
        Assert.Null(planner.TryBuildResumePlan(999999));
    }

    [Fact]
    public void NonNumericDirectoryNames_Ignored()
    {
        // 只解析纯数字序号目录：其他目录（含伪装 manifest）一律不碰（作用域 fail-closed）。
        var stray = Path.Combine(_dir, "garbage");
        Directory.CreateDirectory(stray);
        File.WriteAllText(Path.Combine(stray, "manifest.json"),
            JsonSerializer.Serialize(new { seq = 1L, toolName = "write_file", createdUtc = DateTime.UtcNow,
                files = new[] { new { Path = Path.Combine(_dir, "x.txt"), Existed = true } } }));
        File.WriteAllText(Path.Combine(stray, "0.bak"), "v1");

        var planner = new ResumePlanner(_dir);

        Assert.Null(planner.TryBuildResumePlan());
        Assert.Equal(planner.Root, _dir); // 解析边界 = 显式传入 root
    }
}
