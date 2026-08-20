using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace AeroAgent.Autonomy.Data;

/// <summary>
/// 任务与经验的持久化仓储：<see cref="AutonomyDbContext"/> 的唯一读写入口。
/// 并发模型与 Conversation 的 SessionService 一致——DbContext 非线程安全，
/// 用互斥锁把每个操作单元串行化（锁只覆盖持久化瞬间，不包 LLM/编排调用）。
/// 读取一律返回脱离跟踪的副本。
/// </summary>
public sealed class MissionStore : IDisposable
{
    private readonly AutonomyDbContext _db;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public MissionStore(AutonomyDbContext db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    public void Dispose() => _gate.Dispose();

    private async Task<T> WithDbAsync<T>(Func<Task<T>> operation)
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

    /// <summary>建库建表（幂等）。应用启动时调用一次。</summary>
    public Task EnsureCreatedAsync(CancellationToken ct = default) =>
        WithDbAsync(async () =>
        {
            await _db.Database.EnsureCreatedAsync(ct);
            return true;
        });

    /// <summary>新增或更新任务记录（按 Id upsert），并刷新 UpdatedAtUtc。</summary>
    public Task<MissionRecord> UpsertMissionAsync(MissionRecord mission, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mission);
        return WithDbAsync(async () =>
        {
            mission.UpdatedAtUtc = DateTime.UtcNow;
            var existing = await _db.Missions.FirstOrDefaultAsync(m => m.Id == mission.Id, ct);
            if (existing is null)
            {
                _db.Missions.Add(mission);
            }
            else
            {
                _db.Entry(existing).CurrentValues.SetValues(mission);
            }

            await _db.SaveChangesAsync(ct);
            return Detach(mission);
        });
    }

    /// <summary>按 Id 读取任务（脱离跟踪副本）；不存在返回 null。</summary>
    public Task<MissionRecord?> GetMissionAsync(string id, CancellationToken ct = default) =>
        WithDbAsync(async () =>
        {
            var mission = await _db.Missions.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id, ct);
            return mission is null ? null : Detach(mission);
        });

    /// <summary>按创建时间倒序列出任务（供 UI/审计）。</summary>
    public Task<IReadOnlyList<MissionRecord>> ListMissionsAsync(int limit = 50, CancellationToken ct = default) =>
        WithDbAsync(async () =>
        {
            var missions = await _db.Missions.AsNoTracking()
                .OrderByDescending(m => m.CreatedAtUtc)
                .Take(Math.Max(1, limit))
                .ToListAsync(ct);
            IReadOnlyList<MissionRecord> detachedMissions = missions.Select(Detach).ToList();
            return detachedMissions;
        });

    /// <summary>批量写入经验教训。</summary>
    public Task<int> AddLessonsAsync(IEnumerable<LessonRecord> lessons, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lessons);
        return WithDbAsync(async () =>
        {
            var list = lessons.ToList();
            if (list.Count == 0)
            {
                return 0;
            }

            _db.Lessons.AddRange(list);
            await _db.SaveChangesAsync(ct);
            return list.Count;
        });
    }

    /// <summary>读取最近 N 条经验（按时间倒序）。ExperienceInjector 注入用。</summary>
    public Task<IReadOnlyList<LessonRecord>> GetRecentLessonsAsync(int max, CancellationToken ct = default) =>
        WithDbAsync(async () =>
        {
            var lessons = await _db.Lessons.AsNoTracking()
                .OrderByDescending(l => l.CreatedAtUtc)
                .Take(Math.Max(0, max))
                .ToListAsync(ct);
            IReadOnlyList<LessonRecord> detachedRecent = lessons.Select(Detach).ToList();
            return detachedRecent;
        });

    /// <summary>读取某任务的全部经验。</summary>
    public Task<IReadOnlyList<LessonRecord>> GetLessonsByMissionAsync(string missionId, CancellationToken ct = default) =>
        WithDbAsync(async () =>
        {
            var lessons = await _db.Lessons.AsNoTracking()
                .Where(l => l.MissionId == missionId)
                .OrderBy(l => l.CreatedAtUtc)
                .ToListAsync(ct);
            IReadOnlyList<LessonRecord> detachedByMission = lessons.Select(Detach).ToList();
            return detachedByMission;
        });

    private static MissionRecord Detach(MissionRecord m) => new()
    {
        Id = m.Id,
        TaskText = m.TaskText,
        State = m.State,
        Outcome = m.Outcome,
        AnalysisJson = m.AnalysisJson,
        Strategy = m.Strategy,
        StrategyRationale = m.StrategyRationale,
        ClarificationJson = m.ClarificationJson,
        SteelmanJson = m.SteelmanJson,
        PlanJson = m.PlanJson,
        SessionId = m.SessionId,
        ExecutionJson = m.ExecutionJson,
        VerificationJson = m.VerificationJson,
        RetrospectiveJson = m.RetrospectiveJson,
        TransitionsJson = m.TransitionsJson,
        Error = m.Error,
        CreatedAtUtc = m.CreatedAtUtc,
        UpdatedAtUtc = m.UpdatedAtUtc,
    };

    private static LessonRecord Detach(LessonRecord l) => new()
    {
        Id = l.Id,
        MissionId = l.MissionId,
        Phase = l.Phase,
        Gap = l.Gap,
        Suggestion = l.Suggestion,
        Severity = l.Severity,
        CreatedAtUtc = l.CreatedAtUtc,
    };
}
