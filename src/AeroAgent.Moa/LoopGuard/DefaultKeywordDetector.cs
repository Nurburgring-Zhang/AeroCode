// Copyright (c) AeroCode
// A2 默认偏离检测器（契约 C-LOOP）：关键词启发式纯函数。
// v0 口径：助手本轮文本输出命中偏离关键词（大小写不敏感）即 OffGoal；只检测不处置。
namespace AeroAgent.Moa.LoopGuard;

/// <summary>
/// 关键词启发式 <see cref="IDeviationDetector"/>（确定性、零依赖、可重入）。
/// 默认词表覆盖中英文常见放弃/偏离表述；可经构造参数替换词表（测试与组合根用）。
/// </summary>
public sealed class DefaultKeywordDetector : IDeviationDetector
{
    /// <summary>默认偏离关键词表（对助手文本做 OrdinalIgnoreCase 包含匹配）。</summary>
    public static readonly IReadOnlyList<string> DefaultOffGoalKeywords = new[]
    {
        "无法完成", "无法继续", "做不到", "放弃", "偏离目标", "任务失败",
        "cannot proceed", "cannot complete", "unable to proceed", "giving up", "off track",
    };

    private readonly IReadOnlyList<string> _keywords;

    /// <param name="offGoalKeywords">自定义偏离词表；null = 默认词表。空白项被过滤。</param>
    public DefaultKeywordDetector(IReadOnlyList<string>? offGoalKeywords = null)
    {
        _keywords = (offGoalKeywords ?? DefaultOffGoalKeywords)
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .ToList();
    }

    /// <inheritdoc/>
    public DeviationReport Evaluate(DeviationInput input)
    {
        var text = input.AssistantContent ?? string.Empty;
        foreach (var keyword in _keywords)
        {
            if (text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return new DeviationReport(
                    DeviationVerdict.OffGoal,
                    $"assistant output matched off-goal keyword '{keyword}'");
            }
        }

        return new DeviationReport(DeviationVerdict.OnGoal, "no off-goal signal in assistant output");
    }
}
