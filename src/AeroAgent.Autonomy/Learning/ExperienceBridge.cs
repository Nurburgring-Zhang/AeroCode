using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AeroAgent.Autonomy.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AeroAgent.Autonomy.Learning;

/// <summary>一次 lessons→经验同步的结果统计。</summary>
public sealed record LessonSyncResult(
    int LessonsScanned,
    int NewlySynced,
    int AlreadySynced,
    int FactCount,
    int TrajectoryCount,
    int MethodCount);

/// <summary>
/// 经验桥（P6-T3 / G10 闭环的同步半区）：把 PHASE 5 既有的复盘教训
/// （<see cref="MissionStore.GetRecentLessonsAsync"/> 真实读取）分类同步进
/// <see cref="ExperienceStore"/>（三分存储，写入即 Pending）。
/// 幂等：每条 lesson 以 "lesson:{Id}" 为来源键，重复同步不产生重复经验。
/// 生效不在这里发生——激活由下次会话的 <see cref="SystemPromptBuilder"/> 负责。
/// </summary>
public sealed class ExperienceBridge
{
    private readonly MissionStore _missions;
    private readonly ExperienceStore _experiences;
    private readonly ILogger<ExperienceBridge> _logger;

    public ExperienceBridge(
        MissionStore missions,
        ExperienceStore experiences,
        ILogger<ExperienceBridge>? logger = null)
    {
        _missions = missions ?? throw new ArgumentNullException(nameof(missions));
        _experiences = experiences ?? throw new ArgumentNullException(nameof(experiences));
        _logger = logger ?? NullLogger<ExperienceBridge>.Instance;
    }

    /// <summary>目标经验库（组合式 SystemPromptBuilder 与钩子共用同一实例）。</summary>
    public ExperienceStore Experiences => _experiences;

    /// <summary>
    /// 把最近 <paramref name="maxLessons"/> 条复盘教训同步进经验存储。
    /// 已同步过的（来源键命中）如实跳过。返回真实统计。
    /// </summary>
    public async Task<LessonSyncResult> SyncLessonsAsync(int maxLessons = 200, CancellationToken ct = default)
    {
        var lessons = await _missions.GetRecentLessonsAsync(Math.Max(1, maxLessons), ct);
        var newly = 0;
        var skipped = 0;
        var byKind = new Dictionary<ExperienceKind, int>
        {
            [ExperienceKind.Fact] = 0,
            [ExperienceKind.Trajectory] = 0,
            [ExperienceKind.Method] = 0,
        };

        foreach (var lesson in lessons)
        {
            ct.ThrowIfCancellationRequested();
            var kind = ExperienceClassifier.Classify(lesson);
            var result = await _experiences.AddAsync(
                kind,
                ExperienceClassifier.BuildTitle(lesson),
                ExperienceClassifier.BuildContent(lesson),
                sourceKey: $"lesson:{lesson.Id}",
                sourceMissionId: lesson.MissionId,
                sourcePhase: lesson.Phase,
                tags: new[] { lesson.Severity },
                ct: ct);

            if (result.CreatedNew)
            {
                newly++;
                byKind[kind]++;
            }
            else
            {
                skipped++;
            }
        }

        _logger.LogInformation(
            "经验桥同步完成：扫描 {Scanned} 条 lessons，新入库 {New} 条（事实 {Fact} / 轨迹 {Trajectory} / 方法 {Method}），幂等跳过 {Skipped} 条。",
            lessons.Count, newly, byKind[ExperienceKind.Fact], byKind[ExperienceKind.Trajectory], byKind[ExperienceKind.Method], skipped);

        return new LessonSyncResult(
            lessons.Count, newly, skipped,
            byKind[ExperienceKind.Fact], byKind[ExperienceKind.Trajectory], byKind[ExperienceKind.Method]);
    }
}
