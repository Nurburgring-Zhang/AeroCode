using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AeroCode.Skills.Loader;
using AeroCode.Skills.Registry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AeroAgent.Autonomy.Learning;

/// <summary>单个技能的使用统计（真实来自 <see cref="SkillRegistry.GetStats"/>）。</summary>
public sealed record SkillUsage(string SkillId, string Name, int Invocations, double SuccessRate);

/// <summary>技能归档结果（真实文件操作的留痕）。</summary>
public sealed record SkillArchiveResult(
    bool Success,
    string SkillId,
    string? BackupDirectory,
    string? ArchivedSkillFile,
    bool Unregistered,
    string? Error)
{
    internal static SkillArchiveResult Fail(string skillId, string error) =>
        new(false, skillId, null, null, false, error);
}

/// <summary>技能回滚结果（从备份真实恢复的留痕）。</summary>
public sealed record SkillRollbackResult(
    bool Success,
    string SkillId,
    string? RestoredDirectory,
    int RestoredFileCount,
    string? Error)
{
    internal static SkillRollbackResult Fail(string skillId, string error) =>
        new(false, skillId, null, 0, error);
}

/// <summary>
/// 技能治理器（P6-T3）：技能使用频率统计（真实读取 <see cref="SkillRegistry.GetStats"/>）、
/// 低成功率技能降级标记（落库 skill_flags）、归档（SKILL.md 真实移入 archive 目录 +
/// 注册表注销）、备份与回滚（归档前整目录真实复制到 backup 目录，回滚从备份恢复）。
/// 全部文件操作真实发生；失败如实返回失败结果并记 [DEGRADED]，不静默。
/// </summary>
public sealed class SkillCurator : IDisposable
{
    /// <summary>降级标记的标记类型常量。</summary>
    public const string DegradedFlag = "degraded";

    private const string BackupMetaFileName = "backup-meta.json";

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly SkillRegistry _registry;
    private readonly SkillLoader _loader;
    private readonly LearningDbContext _db;
    private readonly LearningDataPaths _paths;
    private readonly ILogger<SkillCurator> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <param name="registry">技能注册表（频率统计与注销的真实来源）。</param>
    /// <param name="loader">技能加载器（SKILL.md 磁盘路径的真实来源）。</param>
    /// <param name="db">学习库上下文（该实例归本组件独占，勿与其他组件共享同一实例）。</param>
    /// <param name="paths">学习数据路径（archive/backup 目录）。</param>
    /// <param name="logger">日志；null 时用空日志。</param>
    public SkillCurator(
        SkillRegistry registry,
        SkillLoader loader,
        LearningDbContext db,
        LearningDataPaths paths,
        ILogger<SkillCurator>? logger = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _loader = loader ?? throw new ArgumentNullException(nameof(loader));
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _logger = logger ?? NullLogger<SkillCurator>.Instance;
    }

    public void Dispose() => _gate.Dispose();

    /// <summary>
    /// 收集全部注册技能的使用统计（真实读取 SkillRegistry.GetStats；
    /// 未调用过的技能 invocations=0、successRate=1.0——注册表的既有语义）。
    /// </summary>
    public IReadOnlyList<SkillUsage> CollectUsageReport()
    {
        var report = new List<SkillUsage>();
        foreach (var skill in _registry.List())
        {
            var (invocations, successRate) = _registry.GetStats(skill.Id);
            report.Add(new SkillUsage(skill.Id, skill.Name, invocations, successRate));
        }

        return report.OrderByDescending(u => u.Invocations).ThenBy(u => u.SkillId).ToList();
    }

    /// <summary>
    /// 扫描使用统计，把"调用 ≥ <paramref name="minInvocations"/> 且成功率
    /// ≤ <paramref name="maxSuccessRate"/>"的技能标记降级（落库，可查询可清除）。
    /// 返回本次被标记的技能 Id 列表。
    /// </summary>
    public Task<IReadOnlyList<string>> MarkDegradedAsync(
        int minInvocations = 3, double maxSuccessRate = 0.5, CancellationToken ct = default) =>
        WithGateAsync(async () =>
        {
            await _db.Database.EnsureCreatedAsync(ct);
            var flagged = new List<string>();
            var now = DateTime.UtcNow;

            foreach (var usage in CollectUsageReport())
            {
                if (usage.Invocations < Math.Max(1, minInvocations) || usage.SuccessRate > maxSuccessRate)
                {
                    continue;
                }

                var reason = $"调用 {usage.Invocations} 次，成功率 {usage.SuccessRate:P0} ≤ {maxSuccessRate:P0}（阈值：至少 {minInvocations} 次调用）";
                var existing = await _db.SkillFlags.FirstOrDefaultAsync(f => f.SkillId == usage.SkillId, ct);
                if (existing is null)
                {
                    _db.SkillFlags.Add(new SkillFlagEntity
                    {
                        SkillId = usage.SkillId,
                        Flag = DegradedFlag,
                        Reason = reason,
                        CreatedAtUtc = now,
                        UpdatedAtUtc = now,
                    });
                }
                else
                {
                    existing.Flag = DegradedFlag;
                    existing.Reason = reason;
                    existing.UpdatedAtUtc = now;
                }

                flagged.Add(usage.SkillId);
                _logger.LogWarning("[DEGRADED] 技能 {SkillId} 成功率过低（{Reason}），已标记降级。", usage.SkillId, reason);
            }

            if (flagged.Count > 0)
            {
                await _db.SaveChangesAsync(ct);
            }

            return (IReadOnlyList<string>)flagged;
        });

    /// <summary>查询某技能当前是否被标记降级。</summary>
    public Task<bool> IsDegradedAsync(string skillId, CancellationToken ct = default) =>
        WithGateAsync(async () =>
        {
            await _db.Database.EnsureCreatedAsync(ct);
            return await _db.SkillFlags.AnyAsync(
                f => f.SkillId == skillId && f.Flag == DegradedFlag, ct);
        });

    /// <summary>列出全部被标记降级的技能 Id 与理由。</summary>
    public Task<IReadOnlyList<(string SkillId, string Reason)>> ListDegradedAsync(CancellationToken ct = default) =>
        WithGateAsync(async () =>
        {
            await _db.Database.EnsureCreatedAsync(ct);
            var rows = await _db.SkillFlags.AsNoTracking()
                .Where(f => f.Flag == DegradedFlag)
                .OrderBy(f => f.SkillId)
                .ToListAsync(ct);
            return (IReadOnlyList<(string, string)>)rows.Select(f => (f.SkillId, f.Reason)).ToList();
        });

    /// <summary>清除某技能的降级标记（技能恢复后治理复位）。</summary>
    public Task<bool> ClearDegradedAsync(string skillId, CancellationToken ct = default) =>
        WithGateAsync(async () =>
        {
            await _db.Database.EnsureCreatedAsync(ct);
            var row = await _db.SkillFlags.FirstOrDefaultAsync(
                f => f.SkillId == skillId && f.Flag == DegradedFlag, ct);
            if (row is null)
            {
                return false;
            }

            _db.SkillFlags.Remove(row);
            await _db.SaveChangesAsync(ct);
            return true;
        });

    /// <summary>
    /// 归档一个技能（真实文件操作）：整目录复制到 backup → SKILL.md 移入 archive →
    /// 从注册表注销。备份先于移动发生，任何时刻都可回滚。
    /// </summary>
    public SkillArchiveResult ArchiveSkill(string skillId)
    {
        if (string.IsNullOrWhiteSpace(skillId))
        {
            throw new ArgumentException("skillId 不能为空。", nameof(skillId));
        }

        var skill = _loader.GetFull(skillId);
        if (skill is null)
        {
            return SkillArchiveResult.Fail(skillId, $"技能 {skillId} 不在加载器缓存中（无法定位 SKILL.md）");
        }

        var sourceFile = skill.SourcePath;
        if (string.IsNullOrWhiteSpace(sourceFile) || !File.Exists(sourceFile))
        {
            return SkillArchiveResult.Fail(skillId, $"技能 {skillId} 的 SKILL.md 不存在于磁盘: {sourceFile}");
        }

        try
        {
            _paths.EnsureDirectories();
            var sourceDirectory = Path.GetDirectoryName(sourceFile)!;
            var backupDirectory = AllocateBackupDirectory(skillId);

            CopyDirectory(sourceDirectory, backupDirectory);
            WriteBackupMeta(backupDirectory, skillId, sourceDirectory, sourceFile);

            var archiveDirectory = Path.Combine(_paths.SkillArchiveDirectory, SanitizeId(skillId));
            Directory.CreateDirectory(archiveDirectory);
            var archivedFile = Path.Combine(archiveDirectory, "SKILL.md");
            if (File.Exists(archivedFile))
            {
                archivedFile = Path.Combine(archiveDirectory, $"SKILL-{Path.GetFileName(backupDirectory)}.md");
            }

            File.Move(sourceFile, archivedFile);
            var unregistered = _registry.Unregister(skillId);

            _logger.LogInformation(
                "技能 {SkillId} 已归档：SKILL.md → {Archive}；备份 {Backup}；注册表注销={Unregistered}。",
                skillId, archivedFile, backupDirectory, unregistered);

            return new SkillArchiveResult(true, skillId, backupDirectory, archivedFile, unregistered, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[DEGRADED] 技能 {SkillId} 归档失败（文件操作异常，保持原状）: {Error}", skillId, ex.Message);
            return SkillArchiveResult.Fail(skillId, $"归档文件操作失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 回滚一次归档（真实文件操作）：找到该技能最近一次备份，把备份内容复制回原目录。
    /// 归档目录中的 SKILL.md 副本保留作为审计留痕（不在技能扫描路径内，不会被重新加载）。
    /// 回滚不自动重新注册——由组合根重新调用 SkillLoader 加载（职责分离，如实说明）。
    /// </summary>
    public SkillRollbackResult RollbackSkill(string skillId)
    {
        if (string.IsNullOrWhiteSpace(skillId))
        {
            throw new ArgumentException("skillId 不能为空。", nameof(skillId));
        }

        try
        {
            var backupDirectory = FindLatestBackupDirectory(skillId);
            if (backupDirectory is null)
            {
                _logger.LogWarning("[DEGRADED] 技能 {SkillId} 回滚请求无可用备份，如实拒绝。", skillId);
                return SkillRollbackResult.Fail(skillId, "没有找到该技能的备份目录（从未归档或备份已被移除）");
            }

            var meta = ReadBackupMeta(backupDirectory);
            if (meta is null || string.IsNullOrWhiteSpace(meta.OriginalDirectory))
            {
                return SkillRollbackResult.Fail(skillId, $"备份 {backupDirectory} 缺少元数据，无法确定恢复目标目录");
            }

            Directory.CreateDirectory(meta.OriginalDirectory);
            var restored = 0;
            foreach (var file in Directory.EnumerateFiles(backupDirectory, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(backupDirectory, file);
                if (relative == BackupMetaFileName)
                {
                    continue; // 元数据只属于备份本身。
                }

                var target = Path.Combine(meta.OriginalDirectory, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: true);
                restored++;
            }

            _logger.LogInformation("技能 {SkillId} 已从备份 {Backup} 恢复 {Count} 个文件到 {Target}。",
                skillId, backupDirectory, restored, meta.OriginalDirectory);

            return new SkillRollbackResult(true, skillId, meta.OriginalDirectory, restored, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[DEGRADED] 技能 {SkillId} 回滚失败（文件操作异常）: {Error}", skillId, ex.Message);
            return SkillRollbackResult.Fail(skillId, $"回滚文件操作失败: {ex.Message}");
        }
    }

    // ============ 内部实现 ============

    private async Task<T> WithGateAsync<T>(Func<Task<T>> operation)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await operation().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 分配一个备份目录名：固定宽度 ticks 前缀保证字典序 = 时间序（供"最近备份"查找），
    /// 极端同 ticks 冲突时追加序号，绝不复用既有目录。
    /// </summary>
    private string AllocateBackupDirectory(string skillId)
    {
        var baseName = $"{SanitizeId(skillId)}-{DateTime.UtcNow.Ticks:D20}";
        var candidate = Path.Combine(_paths.SkillBackupDirectory, baseName);
        var suffix = 2;
        while (Directory.Exists(candidate))
        {
            candidate = Path.Combine(_paths.SkillBackupDirectory, $"{baseName}-{suffix}");
            suffix++;
        }

        return candidate;
    }

    private string? FindLatestBackupDirectory(string skillId)
    {
        if (!Directory.Exists(_paths.SkillBackupDirectory))
        {
            return null;
        }

        var prefix = SanitizeId(skillId) + "-";
        var candidates = Directory.EnumerateDirectories(_paths.SkillBackupDirectory)
            .Where(d => Path.GetFileName(d).StartsWith(prefix, StringComparison.Ordinal))
            .OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal)
            .ToList();
        return candidates.Count == 0 ? null : candidates[0];
    }

    private static void CopyDirectory(string sourceDirectory, string targetDirectory)
    {
        Directory.CreateDirectory(targetDirectory);
        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDirectory, file);
            var target = Path.Combine(targetDirectory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static void WriteBackupMeta(string backupDirectory, string skillId, string originalDirectory, string originalSkillFile)
    {
        var meta = new BackupMeta(skillId, originalDirectory, originalSkillFile, DateTime.UtcNow);
        File.WriteAllText(
            Path.Combine(backupDirectory, BackupMetaFileName),
            JsonSerializer.Serialize(meta, JsonOpts));
    }

    private static BackupMeta? ReadBackupMeta(string backupDirectory)
    {
        var metaFile = Path.Combine(backupDirectory, BackupMetaFileName);
        if (!File.Exists(metaFile))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<BackupMeta>(File.ReadAllText(metaFile));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>技能 Id → 文件系统安全名（'/'、'\' 等替换为 '_'）。</summary>
    internal static string SanitizeId(string skillId)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = skillId.Select(c => invalid.Contains(c) || c == '/' || c == '\\' ? '_' : c).ToArray();
        var sanitized = new string(chars).Trim();
        return sanitized.Length == 0 ? "unnamed-skill" : sanitized;
    }

    /// <summary>备份元数据（回滚时确定恢复目标）。</summary>
    internal sealed record BackupMeta(
        string SkillId,
        string OriginalDirectory,
        string OriginalSkillFile,
        DateTime BackedUpAtUtc);
}
