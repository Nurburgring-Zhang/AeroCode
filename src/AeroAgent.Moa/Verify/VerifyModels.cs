// Copyright (c) AeroCode
// C2 校验循环数据模型（R2 波次 γ）。纯数据记录 + 收敛策略枚举，无行为。
namespace AeroAgent.Moa.Verify;

/// <summary>一次完成判定验证请求（独立 critique 通道的入参）。</summary>
public sealed record CompletionVerificationRequest
{
    /// <summary>任务目标（判定基准；来自任务/prompt 文本）。</summary>
    public required string TaskGoal { get; init; }

    /// <summary>被验证的产出（当前最终答复文本）。</summary>
    public required string Output { get; init; }

    /// <summary>独立佐证集合（工具输出等，供 critique 与产出比对——判定不信自报）。</summary>
    public IReadOnlyList<string> Evidence { get; init; } = Array.Empty<string>();

    /// <summary>critique 轮次（0 起：对本任务产出的第几次评估）。</summary>
    public int Round { get; init; }
}

/// <summary>一次完成判定裁决。</summary>
public sealed record CompletionVerdict
{
    /// <summary>是否接受完成。</summary>
    public bool Accepted { get; init; }

    /// <summary>接受说明/拒绝理由（脱敏责任在实现，不得含密钥等敏感形态）。</summary>
    public string? Reason { get; init; }

    /// <summary>给重试产出的修复反馈（拒绝时提供；脱敏责任在实现）。</summary>
    public string? Feedback { get; init; }

    public static CompletionVerdict Accept(string? reason = null) => new() { Accepted = true, Reason = reason };

    public static CompletionVerdict Reject(string reason, string? feedback = null)
        => new() { Accepted = false, Reason = reason, Feedback = feedback };
}

/// <summary>
/// critique 轮耗尽仍有未决拒绝时的收敛策略（「2 轮后按策略收敛」）。
/// </summary>
public enum CritiqueConvergencePolicy
{
    /// <summary>
    /// 默认：接受最后一次被 critique 的产出并如实标注未决发现（不静默、不冒充已验证完成）。
    /// 保活语义：有界重试耗尽不把任务打成失败，但验证未通过的事实必须可见。
    /// </summary>
    AcceptWithFindings = 0,

    /// <summary>诚实失败：任务按失败收场（不得冒充完成）。</summary>
    Fail = 1,
}

/// <summary>CritiqueLoop 配置。默认值即安全值：≤2 轮 + AcceptWithFindings。</summary>
public sealed record CritiqueLoopOptions
{
    /// <summary>
    /// critique 评估轮上限（「critique 循环 ≤2 轮」硬语义）。默认 2；
    /// CritiqueLoop 构造时硬钳制到 [1, 2]，配置者无法突破上限。
    /// </summary>
    public int MaxCritiqueRounds { get; init; } = 2;

    /// <summary>轮耗尽仍有未决拒绝时的收敛策略（默认 AcceptWithFindings）。</summary>
    public CritiqueConvergencePolicy ConvergencePolicy { get; init; } = CritiqueConvergencePolicy.AcceptWithFindings;
}

/// <summary>一次 CritiqueLoop 运行的结果。</summary>
public sealed record CritiqueOutcome
{
    /// <summary>最终产出（初始产出或某次重试产出——每次产出都经 critique）。</summary>
    public required string Output { get; init; }

    /// <summary>
    /// 最终是否按「完成」收场：verification 通过 = true；按 AcceptWithFindings 策略收敛 = true；
    /// 按 Fail 策略收敛 = false。
    /// </summary>
    public required bool Accepted { get; init; }

    /// <summary>最后一次 critique 是否通过。false = 按策略收敛（验证未实际通过，发现未决）。</summary>
    public required bool VerificationPassed { get; init; }

    /// <summary>实际消耗的 critique 轮数（1..MaxCritiqueRounds）。</summary>
    public required int CritiqueRoundsUsed { get; init; }

    /// <summary>实际发生的有界重试次数（≤ MaxCritiqueRounds - 1）。</summary>
    public required int RetriesUsed { get; init; }

    /// <summary>全部裁决（按 critique 顺序；审计用）。</summary>
    public required IReadOnlyList<CompletionVerdict> Verdicts { get; init; }

    /// <summary>收敛/未决说明（AcceptWithFindings 收敛或 Fail 时非空；脱敏责任在实现）。</summary>
    public string? Summary { get; init; }
}
