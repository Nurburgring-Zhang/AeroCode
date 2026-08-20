using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AeroAgent.Autonomy.Data;

namespace AeroAgent.Autonomy.Experience;

/// <summary>
/// 经验注入结果：组装好的 system prompt + 实际注入的经验条数。
/// </summary>
public sealed record ExperienceInjection(string SystemPrompt, int InjectedLessonCount);

/// <summary>
/// 经验注入器（G10 差距项的真实实现）：构建任务 system prompt 时，从 lessons 表
/// 真实读取最近 N 条经验并注入——上一次任务学到的教训在下一次任务生效，
/// 形成"执行→复盘→经验→下次执行"的真实闭环。无经验时如实不注入（不编造）。
/// </summary>
public sealed class ExperienceInjector
{
    private readonly MissionStore _store;

    public ExperienceInjector(MissionStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <summary>
    /// 组装 system prompt：角色前缀 + 最近经验（真实 DB 读取）+ 任务上下文。
    /// </summary>
    /// <param name="maxLessons">最多注入的经验条数（&lt;=0 时不注入）。</param>
    /// <param name="ct">取消令牌。</param>
    public async Task<ExperienceInjection> BuildSystemPromptAsync(int maxLessons, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        sb.AppendLine("你是 AeroCode 自主任务执行器。依据任务计划真实执行，产出可检验的成果；");
        sb.AppendLine("遵守零虚假原则：做不到的如实说明，禁止编造结果。");

        var injected = 0;
        if (maxLessons > 0)
        {
            var lessons = await _store.GetRecentLessonsAsync(maxLessons, ct);
            if (lessons.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("## 来自以往任务的经验教训（务必在本次执行中规避/应用）");
                // 按时间正序呈现（最近的最重要，放最后）。
                for (var i = lessons.Count - 1; i >= 0; i--)
                {
                    var lesson = lessons[i];
                    sb.AppendLine($"- [{lesson.Severity}] ({lesson.Phase}) {lesson.Gap}");
                    if (!string.IsNullOrWhiteSpace(lesson.Suggestion))
                    {
                        sb.AppendLine($"  → 建议: {lesson.Suggestion}");
                    }

                    injected++;
                }
            }
        }

        return new ExperienceInjection(sb.ToString(), injected);
    }
}
