// Copyright (c) AeroCode
// ResumePlanner — checkpoint 幂等续跑规划器（A4）。只读组件：定位最近有效 checkpoint、
// 产出重放动作列表；不执行恢复（执行仍归 CheckpointStore.Restore，MissionController
// 的接线归编排者 #13）。manifest 解析全程宽容：旧 manifest（无增量字段）照常可读、
// 照常可恢复；损坏 manifest 跳过、取次新。恢复作用域 fail-closed：只解析显式传入的
// checkpoint root 下的纯数字序号目录，越界即拒。
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;

namespace AeroAgent.Moa.Tools.Workspace;

/// <summary>单个文件的重放动作（与 <see cref="CheckpointStore.Restore"/> 语义一一对应）。</summary>
public enum ResumeActionKind
{
    /// <summary>检查点时文件已存在且有 .bak → 恢复时用备份内容覆盖。</summary>
    RestoreContent,

    /// <summary>检查点时文件不存在 → 恢复时删除该文件（新建回滚语义）。</summary>
    DeleteCreated,

    /// <summary>检查点时文件存在但未被捕获（超大文件，无 .bak）——如实跳过，不伪造恢复。</summary>
    SkipUntracked,

    /// <summary>
    /// manifest 条目缺 Existed 字段（旧/异构 manifest，文件预史不明）——按「已存在」保守处理：
    /// 跳过重放（不删除、不覆盖）。L3 修复：不再误按 DeleteCreated 计划（误删用户既有文件风险）。
    /// </summary>
    SkipUnknownState,
}

/// <summary>一条重放动作。<paramref name="BackupPath"/> 仅 RestoreContent 有值，恒在 checkpoint root 内。</summary>
public sealed record ResumeAction(
    ResumeActionKind Kind,
    string FilePath,
    string? BackupPath);

/// <summary>
/// 续跑计划：锚定单个有效 checkpoint + 去重后的重放动作列表。
/// 字段为只读快照；消费方（#13 接线）据此调用 CheckpointStore.Restore(seq)。
/// </summary>
/// <param name="CheckpointSeq">锚定的 checkpoint 序号（manifest 目录的纯数字序号）。</param>
/// <param name="CheckpointDir">该 checkpoint 目录绝对路径（恒在显式传入的 checkpoint root 内）。</param>
/// <param name="ToolName">产生该 checkpoint 的工具名。</param>
/// <param name="CreatedUtc">checkpoint 创建时间（UTC）。</param>
/// <param name="ToolResult">manifest 增量字段：工具结果登记（旧 manifest 为 null）。</param>
/// <param name="ContextSnapshotRef">manifest 增量字段：上下文快照引用（旧 manifest 为 null）。</param>
/// <param name="TaskStatus">manifest 增量字段：任务状态（旧 manifest 为 null）。</param>
/// <param name="Actions">去重后的重放动作列表（同路径只规划一次）。</param>
public sealed record ResumePlan(
    long CheckpointSeq,
    string CheckpointDir,
    string ToolName,
    DateTime CreatedUtc,
    string? ToolResult,
    string? ContextSnapshotRef,
    string? TaskStatus,
    IReadOnlyList<ResumeAction> Actions);

/// <summary>
/// 续跑规划器。<see cref="TryBuildResumePlan"/> 无副作用、结果确定：checkpoint 落盘状态
/// 不变时，同一 checkpoint 重复调用产出逐项一致（幂等去重：同路径只规划一次；
/// 重复调用不产生新检查点、不改动任何文件）。
/// </summary>
public sealed class ResumePlanner
{
    private readonly string _root;

    /// <param name="checkpointRoot">显式传入的 checkpoint root（fail-closed 边界；空即拒）。</param>
    public ResumePlanner(string checkpointRoot)
    {
        if (string.IsNullOrWhiteSpace(checkpointRoot))
        {
            throw new ArgumentException("checkpoint root must not be empty", nameof(checkpointRoot));
        }

        _root = Path.GetFullPath(checkpointRoot);
    }

    /// <summary>显式传入的 checkpoint root（归一化后的绝对路径；诊断/测试用）。</summary>
    public string Root => _root;

    /// <summary>
    /// 构建续跑计划；无有效 checkpoint 时返回 null（诚实失败，不伪造）。
    /// <paramref name="checkpointSeq"/> 为 null 时定位 root 下**最近有效** checkpoint：
    /// 损坏/缺失 manifest 的目录跳过、取次新；显式指定 seq 时只解析该目录，
    /// 无效即返回 null——不静默回退到其他检查点（fail-closed）。
    /// </summary>
    public ResumePlan? TryBuildResumePlan(long? checkpointSeq = null)
    {
        if (!Directory.Exists(_root))
        {
            return null;
        }

        foreach (var dir in EnumerateCheckpointDirs(checkpointSeq))
        {
            if (TryBuildFromDir(dir, out var plan))
            {
                return plan;
            }

            // 损坏/缺失 manifest：定位"最近有效"时跳过取次新；显式指定 seq 时不回退。
            if (checkpointSeq is not null)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>候选 checkpoint 目录（新→旧）。只接受 root 直下的纯数字序号目录。</summary>
    private IEnumerable<string> EnumerateCheckpointDirs(long? checkpointSeq)
    {
        if (checkpointSeq is { } seq)
        {
            var explicitDir = Path.Combine(_root, seq.ToString("D6", CultureInfo.InvariantCulture));
            if (Directory.Exists(explicitDir))
            {
                yield return explicitDir;
            }

            yield break;
        }

        var seqDirs = Directory.EnumerateDirectories(_root)
            .Select(d => (Dir: d, Seq: ParseSeqDir(d)))
            .Where(x => x.Seq >= 0 && IsUnderRoot(x.Dir))
            .OrderByDescending(x => x.Seq)
            .Select(x => x.Dir);
        foreach (var dir in seqDirs)
        {
            yield return dir;
        }
    }

    private bool IsUnderRoot(string dir)
    {
        var full = Path.GetFullPath(dir);
        return full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(_root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static long ParseSeqDir(string dir)
        => long.TryParse(Path.GetFileName(dir), out var seq) ? seq : -1;

    /// <summary>宽容解析单个 checkpoint 目录；任何缺字段/类型不符/损坏都视为无效（返回 false）。</summary>
    private static bool TryBuildFromDir(string dir, out ResumePlan? plan)
    {
        plan = null;
        var manifestPath = Path.Combine(dir, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            return false;
        }

        string json;
        try
        {
            json = File.ReadAllText(manifestPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false; // 读取被占用/无权限——视为无效，不向上抛（定位流程要能跳过继续）。
        }

        using var doc = TryParseJson(json);
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var root = doc.RootElement;
        if (!TryGetInt64(root, "seq", out var seq) ||
            !root.TryGetProperty("files", out var filesEl) ||
            filesEl.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        // 目录序号与 manifest seq 必须一致：防错位目录/拼接越界（fail-closed 完整性校验）。
        if (!long.TryParse(Path.GetFileName(dir), out var dirSeq) || dirSeq != seq)
        {
            return false;
        }

        var tool = TryGetString(root, "toolName", out var t) ? t : "?";
        var created = root.TryGetProperty("createdUtc", out var createdEl) &&
                      createdEl.ValueKind == JsonValueKind.String &&
                      createdEl.TryGetDateTime(out var c)
            ? c
            : DateTime.MinValue;

        TryGetString(root, "toolResult", out var toolResult);
        TryGetString(root, "contextSnapshotRef", out var contextSnapshotRef);
        TryGetString(root, "taskStatus", out var taskStatus);

        var actions = new List<ResumeAction>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < filesEl.GetArrayLength(); i++)
        {
            var entry = filesEl[i];
            if (entry.ValueKind != JsonValueKind.Object ||
                !TryGetString(entry, "Path", out var path) ||
                string.IsNullOrEmpty(path))
            {
                continue; // 残缺条目如实跳过，不让单条脏数据废掉整个计划。
            }

            // 幂等去重：同一路径只规划一次（首个条目为准），与 Track 的 Distinct 口径一致。
            if (!seen.Add(path))
            {
                continue;
            }

            // L3 语义修复：Existed 字段缺失（旧/异构 manifest，预史不明）→ 按「已存在」保守处理，
            // 跳过重放（不删除、不覆盖）——绝不把预史不明的文件计划成 DeleteCreated（误删风险）。
            // 显式 Existed=false（CheckpointStore 捕获时确不存在）→ DeleteCreated 语义不变；
            // 显式 Existed=true → RestoreContent/SkipUntracked 语义不变（与 CheckpointStore.Restore 一一对应）。
            if (!entry.TryGetProperty("Existed", out var existedEl))
            {
                actions.Add(new ResumeAction(ResumeActionKind.SkipUnknownState, path, BackupPath: null));
                continue;
            }

            if (existedEl.ValueKind != JsonValueKind.True)
            {
                actions.Add(new ResumeAction(ResumeActionKind.DeleteCreated, path, BackupPath: null));
                continue;
            }

            var backup = Path.Combine(dir, $"{i}.bak");
            actions.Add(File.Exists(backup)
                ? new ResumeAction(ResumeActionKind.RestoreContent, path, backup)
                : new ResumeAction(ResumeActionKind.SkipUntracked, path, BackupPath: null));
        }

        plan = new ResumePlan(
            seq, dir, tool, created, toolResult, contextSnapshotRef, taskStatus, actions);
        return true;
    }

    private static JsonDocument? TryParseJson(string json)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryGetInt64(JsonElement obj, string name, out long value)
    {
        value = 0;
        if (!obj.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        try
        {
            value = el.GetInt64();
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool TryGetString(JsonElement obj, string name, [NotNullWhen(true)] out string? value)
    {
        value = null;
        if (obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String)
        {
            value = el.GetString();
            return value is not null;
        }

        return false;
    }
}
