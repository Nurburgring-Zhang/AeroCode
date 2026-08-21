using System;
using AeroAgent.Autonomy.Data;

namespace AeroAgent.Autonomy.Learning;

/// <summary>
/// 经验分类器：把 PHASE 5 的复盘教训（<see cref="LessonRecord"/>）确定性地归类到三分存储。
/// 规则公开、可测试、无随机性（RSI 不碰分类，分类是稳定契约）：
/// <list type="number">
/// <item>事实（Fact）：缺口或建议命中环境/配置类关键词（配置/环境/版本/路径/依赖/证书/端口…）——
/// 这类知识长期稳定，属于"世界是什么样"；</item>
/// <item>方法（Method）：带有非空建议（下次怎么做的可执行指引）——属于"怎么做有效"；</item>
/// <item>轨迹（Trajectory）：只有缺口描述没有建议——纯粹"发生了什么"的执行轨迹记录。</item>
/// </list>
/// 判定优先级：事实 &gt; 方法 &gt; 轨迹（环境配置类知识即使带建议也按事实归档，因为它描述的是稳定外部条件）。
/// </summary>
public static class ExperienceClassifier
{
    /// <summary>环境/配置类关键词（命中任一 → 事实）。中英文覆盖常见表述。</summary>
    private static readonly string[] FactKeywords =
    {
        "配置", "环境", "版本", "路径", "依赖", "证书", "密钥", "端口", "权限", "网络",
        "config", "configuration", "environment", "version", "path", "dependency",
        "certificate", "key", "port", "permission",
    };

    /// <summary>把一条复盘教训分类为经验种类。</summary>
    public static ExperienceKind Classify(LessonRecord lesson)
    {
        ArgumentNullException.ThrowIfNull(lesson);

        var combined = string.Concat(lesson.Gap ?? string.Empty, " ", lesson.Suggestion ?? string.Empty);
        if (ContainsAnyFactKeyword(combined))
        {
            return ExperienceKind.Fact;
        }

        if (!string.IsNullOrWhiteSpace(lesson.Suggestion))
        {
            return ExperienceKind.Method;
        }

        return ExperienceKind.Trajectory;
    }

    /// <summary>为同步的经验生成标题（阶段 + 缺口摘要，一行可读）。</summary>
    public static string BuildTitle(LessonRecord lesson)
    {
        ArgumentNullException.ThrowIfNull(lesson);
        var gap = (lesson.Gap ?? string.Empty).Trim();
        if (gap.Length > 60)
        {
            gap = gap[..60] + "…";
        }

        return $"[{lesson.Phase}] {gap}";
    }

    /// <summary>为同步的经验生成正文（缺口 + 建议，真实对应复盘原文）。</summary>
    public static string BuildContent(LessonRecord lesson)
    {
        ArgumentNullException.ThrowIfNull(lesson);
        var content = $"缺口（{lesson.Severity}）: {lesson.Gap}";
        if (!string.IsNullOrWhiteSpace(lesson.Suggestion))
        {
            content += Environment.NewLine + $"做法: {lesson.Suggestion}";
        }

        return content;
    }

    private static bool ContainsAnyFactKeyword(string text)
    {
        foreach (var keyword in FactKeywords)
        {
            if (text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
