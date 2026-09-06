// Copyright (c) AeroCode
// L3 Existed 语义钉子：缺 Existed 字段条目 →「已存在→跳过重放」（SkipUnknownState，不再误按
// DeleteCreated）；显式 false（CheckpointStore 捕获时确不存在）→ DeleteCreated 不变；
// 显式 true → RestoreContent/SkipUntracked 不变。真实 CheckpointStore + 真实文件系统，零 mock。
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using AeroAgent.Moa.Tools.Workspace;
using Xunit;

namespace AeroCode.Tests.MoaTests;

public sealed class ResumePlannerExistedTests : IDisposable
{
    private readonly string _dir;

    public ResumePlannerExistedTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"resumeexisted_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort 清理 */ }
    }

    /// <summary>手写 manifest：files 条目只含 Path（缺 Existed 字段），可选落 .bak。</summary>
    private string WriteManifestMissingExisted(
        long seq, params (string Path, string? BackupContent)[] files)
    {
        var cpDir = Path.Combine(_dir, seq.ToString("D6"));
        Directory.CreateDirectory(cpDir);
        var manifest = new
        {
            seq,
            toolName = "edit_file",
            createdUtc = DateTime.UtcNow,
            files = files.Select(f => new { Path = f.Path }).ToArray(),
        };
        File.WriteAllText(Path.Combine(cpDir, "manifest.json"), JsonSerializer.Serialize(manifest));
        for (var i = 0; i < files.Length; i++)
        {
            if (files[i].BackupContent is not null)
            {
                File.WriteAllText(Path.Combine(cpDir, $"{i}.bak"), files[i].BackupContent);
            }
        }

        return cpDir;
    }

    [Fact]
    public void MissingExistedField_PlannedAsSkipUnknownState_NotDeleteCreated()
    {
        // L3 核心语义：预史不明的文件绝不计划成 DeleteCreated（误删用户既有文件风险）。
        var f1 = Path.Combine(_dir, "unknown.txt");
        WriteManifestMissingExisted(1, (f1, null));

        var plan = new ResumePlanner(_dir).TryBuildResumePlan();

        Assert.NotNull(plan);
        var action = Assert.Single(plan!.Actions);
        Assert.Equal(ResumeActionKind.SkipUnknownState, action.Kind);
        Assert.Equal(f1, action.FilePath);
        Assert.Null(action.BackupPath);
        Assert.DoesNotContain(plan.Actions, a => a.Kind == ResumeActionKind.DeleteCreated);
    }

    [Fact]
    public void MissingExistedField_WithBackupAvailable_StillSkipsReplay()
    {
        // 「已存在→跳过重放」是无条件的：即使恰有 .bak，也不对预史不明文件计划覆盖/删除。
        var f1 = Path.Combine(_dir, "mystery.txt");
        var cpDir = WriteManifestMissingExisted(2, (f1, "old-content"));
        Assert.True(File.Exists(Path.Combine(cpDir, "0.bak"))); // 前置：备份确实存在

        var plan = new ResumePlanner(_dir).TryBuildResumePlan();

        Assert.NotNull(plan);
        Assert.Equal(ResumeActionKind.SkipUnknownState, Assert.Single(plan!.Actions).Kind);
    }

    [Fact]
    public void RealStore_CreatedFile_StillPlannedAsDeleteCreated()
    {
        // 向后兼容：真实 CheckpointStore 写出的显式 Existed=false（捕获时确不存在）语义不变。
        var store = new CheckpointStore(_dir);
        var created = Path.Combine(_dir, "created-by-tool.txt");
        store.Track("write_file", new[] { created }); // Track 时文件不存在 → Existed=false

        var plan = new ResumePlanner(_dir).TryBuildResumePlan();

        Assert.NotNull(plan);
        var action = Assert.Single(plan!.Actions);
        Assert.Equal(ResumeActionKind.DeleteCreated, action.Kind);
        Assert.Equal(created, action.FilePath);
    }

    [Fact]
    public void RealStore_ExistingFile_StillPlannedAsRestoreContent()
    {
        // 向后兼容：真实 CheckpointStore 写出的显式 Existed=true + .bak → RestoreContent 语义不变。
        var store = new CheckpointStore(_dir);
        var existing = Path.Combine(_dir, "pre-existing.txt");
        File.WriteAllText(existing, "checkpoint-version");
        store.Track("edit_file", new[] { existing });

        var plan = new ResumePlanner(_dir).TryBuildResumePlan();

        Assert.NotNull(plan);
        var action = Assert.Single(plan!.Actions);
        Assert.Equal(ResumeActionKind.RestoreContent, action.Kind);
        Assert.Equal(existing, action.FilePath);
        Assert.NotNull(action.BackupPath);
        Assert.True(File.Exists(action.BackupPath!));
    }

    [Fact]
    public void MixedManifest_MissingAndExplicitEntries_KeepRespectiveSemantics()
    {
        // 混合 manifest：缺 Existed 条目与显式条目各按各的语义，互不串扰。
        var missing = Path.Combine(_dir, "missing.txt");
        var explicitTrue = Path.Combine(_dir, "tracked.txt");
        var explicitFalse = Path.Combine(_dir, "created.txt");
        var cpDir = Path.Combine(_dir, "000009");
        Directory.CreateDirectory(cpDir);
        var manifest = new
        {
            seq = 9L,
            toolName = "write_file",
            createdUtc = DateTime.UtcNow,
            files = new object[]
            {
                new { Path = missing },
                new { Path = explicitTrue, Existed = true },
                new { Path = explicitFalse, Existed = false },
            },
        };
        File.WriteAllText(Path.Combine(cpDir, "manifest.json"), JsonSerializer.Serialize(manifest));
        File.WriteAllText(Path.Combine(cpDir, "1.bak"), "v1");

        var plan = new ResumePlanner(_dir).TryBuildResumePlan();

        Assert.NotNull(plan);
        Assert.Equal(9, plan!.CheckpointSeq);
        Assert.Equal(3, plan.Actions.Count);
        Assert.Equal(ResumeActionKind.SkipUnknownState, plan.Actions[0].Kind);
        Assert.Equal(ResumeActionKind.RestoreContent, plan.Actions[1].Kind);
        Assert.Equal(ResumeActionKind.DeleteCreated, plan.Actions[2].Kind);
    }
}
