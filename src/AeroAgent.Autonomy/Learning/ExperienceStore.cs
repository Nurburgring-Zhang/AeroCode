using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AeroAgent.Autonomy.Learning;

/// <summary>经验写入结果：条目本身 + 是否新建（false = SourceKey 已存在，幂等命中）。</summary>
public sealed record ExperienceAddResult(ExperienceEntry Entry, bool CreatedNew);

/// <summary>
/// 经验三分存储（P6-T3 / G10 的真实实现）：事实（facts）/轨迹（trajectories）/方法（methods）
/// 三分入库，SQLite 落库（独立库文件）+ 人类可读 md 落盘（experience-log.md 追加）。
///
/// 写入与生效分离（零虚假语义）：
/// <list type="number">
/// <item><see cref="AddAsync"/> 写入的经验一律标记 <see cref="ExperienceStatus.Pending"/>——本次会话不可见；</item>
/// <item>下次会话构建 prompt 时调用 <see cref="ActivatePendingAsync"/> 将 Pending 提升为 Effective（生效）；</item>
/// <item><see cref="GetEffectiveExperiencesAsync"/> 只返回 Effective/Applied（Pending 绝不混入）；</item>
/// <item><see cref="MarkAppliedAsync"/> 把被 prompt 真实消费的经验标记 Applied（留痕，持续有效）。</item>
/// </list>
///
/// 并发模型与 <see cref="Data.MissionStore"/> 一致：DbContext 非线程安全，
/// 互斥锁串行化每个操作单元；读取返回脱离跟踪的副本。
/// md 日志是人类可读副本——写 md 失败只记 [DEGRADED]，不影响数据库事实源。
/// </summary>
public sealed class ExperienceStore : IDisposable
{
    private readonly LearningDbContext _db;
    private readonly LearningDataPaths _paths;
    private readonly ILogger<ExperienceStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <param name="db">学习库上下文（该实例归本组件独占，勿与其他组件共享同一实例）。</param>
    /// <param name="paths">学习数据路径（md 日志位置）。</param>
    /// <param name="logger">日志；null 时用空日志。</param>
    public ExperienceStore(LearningDbContext db, LearningDataPaths paths, ILogger<ExperienceStore>? logger = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _logger = logger ?? NullLogger<ExperienceStore>.Instance;
    }

    public void Dispose() => _gate.Dispose();

    /// <summary>建库建表（幂等）。组合根启动时调用一次。</summary>
    public Task EnsureCreatedAsync(CancellationToken ct = default) =>
        WithGateAsync(async () =>
        {
            _paths.EnsureDirectories();
            await _db.Database.EnsureCreatedAsync(ct);
            return true;
        });

    /// <summary>
    /// 写入一条经验（状态恒为 Pending——写入与生效分离）。
    /// SourceKey 命中已有条目时幂等返回已有条目（CreatedNew=false），不重复写 md。
    /// </summary>
    public Task<ExperienceAddResult> AddAsync(
        ExperienceKind kind,
        string title,
        string content,
        string? sourceKey = null,
        string? sourceMissionId = null,
        string? sourcePhase = null,
        IReadOnlyList<string>? tags = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("经验标题不能为空。", nameof(title));
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            throw new ArgumentException("经验内容不能为空。", nameof(content));
        }

        var key = string.IsNullOrWhiteSpace(sourceKey)
            ? $"manual:{Guid.NewGuid():N}"
            : sourceKey!.Trim();

        return WithGateAsync(async () =>
        {
            await EnsureSchemaAsync(ct);

            var existing = await _db.Experiences.AsNoTracking().FirstOrDefaultAsync(x => x.SourceKey == key, ct);
            if (existing is not null)
            {
                return new ExperienceAddResult(Detach(existing), false);
            }

            var entity = new ExperienceEntity
            {
                Kind = kind,
                Status = ExperienceStatus.Pending,
                Title = title.Trim(),
                Content = content.Trim(),
                SourceKey = key,
                SourceMissionId = sourceMissionId,
                SourcePhase = sourcePhase,
                TagsJson = tags is { Count: > 0 } ? JsonSerializer.Serialize(tags) : null,
            };
            _db.Experiences.Add(entity);
            await _db.SaveChangesAsync(ct);

            AppendMarkdownLog(entity);
            return new ExperienceAddResult(Detach(entity), true);
        });
    }

    /// <summary>读取指定状态的经验（按创建时间正序；最近的最重要，放最后）。</summary>
    public Task<IReadOnlyList<ExperienceEntry>> GetByStatusAsync(
        ExperienceStatus status, int max = 100, CancellationToken ct = default) =>
        WithGateAsync(async () =>
        {
            await EnsureSchemaAsync(ct);
            var rows = await _db.Experiences.AsNoTracking()
                .Where(x => x.Status == status)
                .OrderBy(x => x.CreatedAtUtc)
                .Take(Math.Max(0, max))
                .ToListAsync(ct);
            return (IReadOnlyList<ExperienceEntry>)rows.Select(Detach).ToList();
        });

    /// <summary>
    /// 读取当前生效的经验（Effective + Applied；Pending 绝不返回）。
    /// 这是构建 system prompt 的唯一经验来源——写入与生效分离的读侧契约。
    /// </summary>
    public Task<IReadOnlyList<ExperienceEntry>> GetEffectiveExperiencesAsync(
        int max = 20, CancellationToken ct = default) =>
        WithGateAsync(async () =>
        {
            await EnsureSchemaAsync(ct);
            var rows = await _db.Experiences.AsNoTracking()
                .Where(x => x.Status == ExperienceStatus.Effective || x.Status == ExperienceStatus.Applied)
                .OrderBy(x => x.CreatedAtUtc)
                .Take(Math.Max(0, max))
                .ToListAsync(ct);
            return (IReadOnlyList<ExperienceEntry>)rows.Select(Detach).ToList();
        });

    /// <summary>按种类读取经验（任意状态；供审计/治理）。</summary>
    public Task<IReadOnlyList<ExperienceEntry>> GetByKindAsync(
        ExperienceKind kind, int max = 100, CancellationToken ct = default) =>
        WithGateAsync(async () =>
        {
            await EnsureSchemaAsync(ct);
            var rows = await _db.Experiences.AsNoTracking()
                .Where(x => x.Kind == kind)
                .OrderByDescending(x => x.CreatedAtUtc)
                .Take(Math.Max(0, max))
                .ToListAsync(ct);
            return (IReadOnlyList<ExperienceEntry>)rows.Select(Detach).ToList();
        });

    /// <summary>按 Id 读取单条经验；不存在返回 null。</summary>
    public Task<ExperienceEntry?> GetByIdAsync(string id, CancellationToken ct = default) =>
        WithGateAsync(async () =>
        {
            await EnsureSchemaAsync(ct);
            var row = await _db.Experiences.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            return row is null ? null : Detach(row);
        });

    /// <summary>
    /// 把所有 Pending 经验提升为 Effective（下次会话生效语义的激活动作）。
    /// 返回本次激活的条数（0 = 没有待激活经验）。
    /// </summary>
    public Task<int> ActivatePendingAsync(CancellationToken ct = default) =>
        WithGateAsync(async () =>
        {
            await EnsureSchemaAsync(ct);
            var pending = await _db.Experiences
                .Where(x => x.Status == ExperienceStatus.Pending)
                .ToListAsync(ct);
            if (pending.Count == 0)
            {
                return 0;
            }

            var now = DateTime.UtcNow;
            foreach (var entity in pending)
            {
                entity.Status = ExperienceStatus.Effective;
                entity.ActivatedAtUtc = now;
            }

            await _db.SaveChangesAsync(ct);
            _logger.LogInformation("经验存储激活 {Count} 条 Pending 经验为 Effective。", pending.Count);
            return pending.Count;
        });

    /// <summary>
    /// 把被 prompt 真实消费的经验标记 Applied（只处理当前为 Effective 的 Id；
    /// Pending 不可直接 Applied——必须先激活，语义不被绕过）。返回实际标记条数。
    /// </summary>
    public Task<int> MarkAppliedAsync(IReadOnlyList<string> ids, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
        {
            return Task.FromResult(0);
        }

        return WithGateAsync(async () =>
        {
            await EnsureSchemaAsync(ct);
            var rows = await _db.Experiences
                .Where(x => ids.Contains(x.Id) && x.Status == ExperienceStatus.Effective)
                .ToListAsync(ct);
            if (rows.Count == 0)
            {
                return 0;
            }

            var now = DateTime.UtcNow;
            foreach (var entity in rows)
            {
                entity.Status = ExperienceStatus.Applied;
                entity.AppliedAtUtc = now;
            }

            await _db.SaveChangesAsync(ct);
            return rows.Count;
        });
    }

    /// <summary>统计条数（kind 为 null 时统计全部）。</summary>
    public Task<int> CountAsync(ExperienceKind? kind = null, CancellationToken ct = default) =>
        WithGateAsync(async () =>
        {
            await EnsureSchemaAsync(ct);
            return kind is null
                ? await _db.Experiences.CountAsync(ct)
                : await _db.Experiences.CountAsync(x => x.Kind == kind.Value, ct);
        });

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

    private async Task EnsureSchemaAsync(CancellationToken ct)
    {
        // 组件可能被直接用于未经组合根初始化的路径（如测试），幂等保证可用。
        await _db.Database.EnsureCreatedAsync(ct);
    }

    private void AppendMarkdownLog(ExperienceEntity entity)
    {
        try
        {
            _paths.EnsureDirectories();
            var sb = new StringBuilder();
            if (!File.Exists(_paths.ExperienceLogFile))
            {
                sb.AppendLine("# AeroCode 经验日志（experience-log）");
                sb.AppendLine();
                sb.AppendLine("> 每条经验写入时追加一个条目块。数据库是事实源，本文件是人类可读副本。");
                sb.AppendLine();
            }

            sb.AppendLine($"## [{entity.CreatedAtUtc:O}] {KindLabel(entity.Kind)}：{entity.Title}");
            sb.AppendLine($"- Id: {entity.Id}");
            sb.AppendLine($"- 状态: {entity.Status}（写入与生效分离：下次会话构建 prompt 时激活）");
            sb.AppendLine($"- 来源键: {entity.SourceKey}");
            if (!string.IsNullOrWhiteSpace(entity.SourceMissionId))
            {
                sb.AppendLine($"- 来源任务: {entity.SourceMissionId}{(string.IsNullOrWhiteSpace(entity.SourcePhase) ? string.Empty : $" / 阶段 {entity.SourcePhase}")}");
            }

            var tags = ParseTags(entity.TagsJson);
            if (tags.Count > 0)
            {
                sb.AppendLine($"- 标签: {string.Join(", ", tags)}");
            }

            sb.AppendLine();
            sb.AppendLine(entity.Content);
            sb.AppendLine();

            File.AppendAllText(_paths.ExperienceLogFile, sb.ToString());
        }
        catch (Exception ex)
        {
            // md 是人类可读副本；写失败不阻断数据库事实源，但必须显式降级留痕。
            _logger.LogWarning("[DEGRADED] experience-log.md 追加写入失败（数据库已落库，md 副本缺失）: {Error}", ex.Message);
        }
    }

    internal static string KindLabel(ExperienceKind kind) => kind switch
    {
        ExperienceKind.Fact => "事实",
        ExperienceKind.Trajectory => "轨迹",
        ExperienceKind.Method => "方法",
        _ => kind.ToString(),
    };

    internal static IReadOnlyList<string> ParseTags(string? tagsJson)
    {
        if (string.IsNullOrWhiteSpace(tagsJson))
        {
            return Array.Empty<string>();
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(tagsJson) ?? new List<string>();
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }

    private static ExperienceEntry Detach(ExperienceEntity e) => new()
    {
        Id = e.Id,
        Kind = e.Kind,
        Status = e.Status,
        Title = e.Title,
        Content = e.Content,
        SourceKey = e.SourceKey,
        SourceMissionId = e.SourceMissionId,
        SourcePhase = e.SourcePhase,
        Tags = ParseTags(e.TagsJson),
        CreatedAtUtc = e.CreatedAtUtc,
        ActivatedAtUtc = e.ActivatedAtUtc,
        AppliedAtUtc = e.AppliedAtUtc,
    };
}
