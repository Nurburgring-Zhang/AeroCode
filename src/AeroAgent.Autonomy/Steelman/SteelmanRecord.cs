using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AeroAgent.Autonomy.Common;

namespace AeroAgent.Autonomy.Steelman;

/// <summary>钢人论证运行模式。</summary>
public enum SteelmanMode
{
    /// <summary>交互模式：向应答方抛出关键问题，等待应答后继续。</summary>
    Interactive = 0,

    /// <summary>自动批准模式：记录所做假设与理由后直接继续（无人值守场景）。</summary>
    AutoApprove = 1,
}

/// <summary>
/// 关键问题的应答方抽象。Interactive 模式下由 UI/远端实现；
/// 无人应答方时协议如实降级为 AutoApprove（记录 [DEGRADED]）。
/// </summary>
public interface ISteelmanResponder
{
    /// <summary>回答钢人论证的关键问题。返回 null/空 = 放弃回答（记录为未应答）。</summary>
    Task<string?> AnswerAsync(string taskText, string keyQuestion, CancellationToken ct);
}

/// <summary>
/// 执行前双向钢人论证记录：最完整重述 / 支持最强论证 / 反对最强论证 /
/// 真正分歧与关键变量 / 只问一个最关键问题。五个字段全部非空——
/// 无 LLM 时由结构化模板从任务文本真实提取要素填充，禁止空串占位。
/// </summary>
public sealed record SteelmanRecord
{
    /// <summary>对任务的最完整重述（把隐含目标显式化）。</summary>
    public required string Restatement { get; init; }

    /// <summary>支持执行该任务的最强论证。</summary>
    public required string ProArgument { get; init; }

    /// <summary>反对/质疑执行该任务的最强论证。</summary>
    public required string ConArgument { get; init; }

    /// <summary>真正的分歧点 + 最可能改变结论的关键变量。</summary>
    public required string Divergence { get; init; }

    /// <summary>只问一个最关键问题。</summary>
    public required string OneKeyQuestion { get; init; }

    /// <summary>Interactive 模式下关键问题的应答（未应答/自动批准为 null）。</summary>
    public string? KeyQuestionAnswer { get; init; }

    /// <summary>实际运行模式（Interactive 无应答方时降级为 AutoApprove）。</summary>
    public required SteelmanMode Mode { get; init; }

    /// <summary>true = 请求 Interactive 但无应答方，如实降级为自动批准。</summary>
    public bool DegradedToAutoApprove { get; init; }

    /// <summary>AutoApprove 模式下记录的假设清单（含理由，非空）。</summary>
    public IReadOnlyList<string> Assumptions { get; init; } = Array.Empty<string>();

    /// <summary>产出来源（真实 LLM / 结构化模板启发式）。</summary>
    public AnalysisSource Source { get; init; } = AnalysisSource.Heuristic;

    public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;
}
