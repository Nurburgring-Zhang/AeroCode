using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AeroAgent.Autonomy.Experience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AeroAgent.Autonomy.Learning;

/// <summary>组合式 system prompt 构建结果。</summary>
public sealed record SystemPromptComposition(
    string SystemPrompt,
    int InjectedLessonCount,
    int InjectedExperienceCount,
    int ActivatedPendingCount,
    int MarkedAppliedCount);

/// <summary>
/// 组合式 system prompt 构建器（P6-T3 / G10 闭环的注入半区）。
/// <see cref="Experience.ExperienceInjector"/> 不可修改，本构建器把它作为"lessons 通道"
/// 真实复用（构造注入，真实调用），再叠加"长期经验通道"（<see cref="ExperienceStore"/>
/// 的生效经验），两路数据真实读取后合并成一份 system prompt：
/// <list type="bullet">
/// <item>lessons 通道：ExperienceInjector.BuildSystemPromptAsync（最近复盘教训，PHASE 5 语义不变）；</item>
/// <item>经验通道：GetEffectiveExperiencesAsync（Effective/Applied；Pending 绝不注入）。</item>
/// </list>
/// 默认在构建前执行 <see cref="ExperienceStore.ActivatePendingAsync"/>——
/// 这正是"写入与生效分离：新经验下次会话构建 prompt 时才生效"的真实落点；
/// 构建完成后对被注入的 Effective 经验调用 MarkApplied（真实消费留痕）。
/// </summary>
public sealed class SystemPromptBuilder
{
    private readonly ExperienceInjector _injector;
    private readonly ExperienceStore _experiences;
    private readonly ILogger<SystemPromptBuilder> _logger;

    public SystemPromptBuilder(
        ExperienceInjector injector,
        ExperienceStore experiences,
        ILogger<SystemPromptBuilder>? logger = null)
    {
        _injector = injector ?? throw new ArgumentNullException(nameof(injector));
        _experiences = experiences ?? throw new ArgumentNullException(nameof(experiences));
        _logger = logger ?? NullLogger<SystemPromptBuilder>.Instance;
    }

    /// <summary>
    /// 构建下一次会话的 system prompt。
    /// </summary>
    /// <param name="maxLessons">lessons 通道注入上限（&lt;=0 不注入）。</param>
    /// <param name="maxExperiences">经验通道注入上限（&lt;=0 不注入）。</param>
    /// <param name="activatePending">
    /// true（默认）：构建前把 Pending 经验激活为 Effective（下次会话生效语义）；
    /// false：只读当前已生效经验（用于验证 Pending 隔离语义）。</param>
    /// <param name="ct">取消令牌。</param>
    public async Task<SystemPromptComposition> BuildAsync(
        int maxLessons = 5,
        int maxExperiences = 8,
        bool activatePending = true,
        CancellationToken ct = default)
    {
        var activated = 0;
        if (activatePending)
        {
            activated = await _experiences.ActivatePendingAsync(ct);
        }

        // 通道一：PHASE 5 既有 lessons 注入（真实调用，不复制其逻辑）。
        var injection = await _injector.BuildSystemPromptAsync(maxLessons, ct);

        // 通道二：三分经验存储的生效经验（Pending 被存储层契约排除）。
        var effective = maxExperiences > 0
            ? await _experiences.GetEffectiveExperiencesAsync(maxExperiences, ct)
            : Array.Empty<ExperienceEntry>();

        var prompt = effective.Count == 0
            ? injection.SystemPrompt
            : injection.SystemPrompt + RenderExperienceSection(effective);

        var marked = 0;
        if (effective.Count > 0)
        {
            var effectiveOnlyIds = effective
                .Where(e => e.Status == ExperienceStatus.Effective)
                .Select(e => e.Id)
                .ToList();
            marked = await _experiences.MarkAppliedAsync(effectiveOnlyIds, ct);
        }

        _logger.LogInformation(
            "组合式 system prompt 构建完成：lessons {Lessons} 条，生效经验 {Experiences} 条（本次激活 {Activated}，标记 Applied {Marked}）。",
            injection.InjectedLessonCount, effective.Count, activated, marked);

        return new SystemPromptComposition(
            prompt, injection.InjectedLessonCount, effective.Count, activated, marked);
    }

    private static string RenderExperienceSection(IReadOnlyList<ExperienceEntry> experiences)
    {
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("## 已生效的长期经验（三分存储，来自以往任务的真实沉淀）");

        AppendKindGroup(sb, experiences, ExperienceKind.Fact, "事实（环境/配置类稳定知识）");
        AppendKindGroup(sb, experiences, ExperienceKind.Method, "方法（有效做法）");
        AppendKindGroup(sb, experiences, ExperienceKind.Trajectory, "轨迹（历史任务轨迹摘要）");

        sb.AppendLine("以上经验务必在本次任务中参照执行；与当前任务明显不相关的条目可忽略，但不得违反。");
        return sb.ToString();
    }

    private static void AppendKindGroup(
        StringBuilder sb, IReadOnlyList<ExperienceEntry> experiences, ExperienceKind kind, string heading)
    {
        var group = experiences.Where(e => e.Kind == kind).ToList();
        if (group.Count == 0)
        {
            return;
        }

        sb.AppendLine();
        sb.AppendLine($"### {heading}");
        foreach (var entry in group)
        {
            sb.AppendLine($"- {entry.Title}");
            foreach (var line in entry.Content.Split('\n'))
            {
                var trimmed = line.TrimEnd('\r');
                if (trimmed.Length > 0)
                {
                    sb.AppendLine($"    {trimmed}");
                }
            }
        }
    }
}
