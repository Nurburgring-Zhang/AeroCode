// Copyright (c) AeroCode
// A2 目标锚（契约 C-LOOP）：任务目标的不可变载体 + 锚定段渲染。
// WorkerRunner 工具轮每轮首持有目标时注入/滚动更新锚定段（原始目标 + 最近 checkpoint 摘要 + 剩余判据）。
namespace AeroAgent.Moa.LoopGuard;

/// <summary>
/// 任务目标锚（不可变）：原始目标 + 剩余判据。经 <see cref="Create"/> 构造——
/// 目标为空白时返回 null（「若持有任务目标」的未持有语义，循环行为保持原样）。
/// </summary>
public sealed record GoalAnchor(string Goal, IReadOnlyList<string> RemainingCriteria)
{
    /// <summary>构造锚；goal 空白 → null（未持有目标）。判据逐条去空白并过滤空项。</summary>
    public static GoalAnchor? Create(string? goal, IEnumerable<string>? remainingCriteria = null)
    {
        if (string.IsNullOrWhiteSpace(goal))
        {
            return null;
        }

        var criteria = (remainingCriteria ?? Array.Empty<string>())
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c.Trim())
            .ToList();
        return new GoalAnchor(goal.Trim(), criteria);
    }

    /// <summary>
    /// 渲染每轮首注入的锚定段文本。turn 为 0 起的工具轮序号；
    /// latestCheckpointSummary 为最近 checkpoint 摘要（由 WorkerRunner 从 CheckpointStore 提取，可 null）。
    /// </summary>
    public string Render(int turn, string? latestCheckpointSummary)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("[目标锚定 · 第 ").Append(turn + 1).Append(" 轮]");
        sb.AppendLine();
        sb.Append("原始目标：").AppendLine(Goal);
        if (!string.IsNullOrWhiteSpace(latestCheckpointSummary))
        {
            sb.Append("最近 checkpoint：").AppendLine(latestCheckpointSummary);
        }

        if (RemainingCriteria.Count > 0)
        {
            sb.AppendLine("剩余判据：");
            foreach (var criterion in RemainingCriteria)
            {
                sb.Append("- ").AppendLine(criterion);
            }
        }

        sb.AppendLine("若当前路径与上述目标偏离，请立即回到目标；达成后请给出针对剩余判据的最终答复。");
        return sb.ToString();
    }
}
