// Copyright (c) AeroCode
// CostGovernor — ACS v2.3.0 四道成本闸门的统一运行时治理。
// 闸门：① 窄步闸（单步单产出+预算）② 回灌禁令（总结/上下文/读文件上限）
// ③ 空转闸（two-strike evidence_delta=0）④ 思考预算闸（think_ratio 警戒线）。
// 违规返回退出码语义（0=PASS / 1=BLOCK / 2=USAGE_ERROR），只判定不执行（ACS 定位）。
namespace AeroCode.Harness.Acs;

/// <summary>单步度量报告（成本闸门输入；必填字段缺失 = USAGE_ERROR）。</summary>
public sealed record AcsStepReport
{
    /// <summary>本步可验证产出数。</summary>
    public int OutputsCount { get; init; }

    /// <summary>本步已用预算（分钟）。</summary>
    public double BudgetMinUsed { get; init; }

    /// <summary>本步引用上文字节数（上下文回灌代理量）。</summary>
    public int ContextBytes { get; init; }

    /// <summary>本步读取文件数。</summary>
    public int FilesRead { get; init; }

    /// <summary>跨步总结字符数。</summary>
    public int SummaryChars { get; init; }

    /// <summary>思考占比（0-1）。</summary>
    public double ThinkRatio { get; init; }

    /// <summary>本步是否有新证据（文件变更/新通过断言/新外部事实）。</summary>
    public bool HasEvidenceDelta { get; init; }
}

/// <summary>单条闸门违规。</summary>
public sealed record AcsGateViolation(string Gate, string Message, bool IsWarningOnly);

/// <summary>闸门检查结果：退出码 + 违规清单。</summary>
public sealed record AcsGateResult(int ExitCode, IReadOnlyList<AcsGateViolation> Violations)
{
    /// <summary>是否通过（退出码 0 且无阻断违规）。</summary>
    public bool Passed => ExitCode == AcsExitCodes.Pass;
}

/// <summary>
/// 四道成本闸门治理器。无状态判定（除空转 strike 计数外），线程安全。
/// </summary>
public sealed class CostGovernor
{
    private readonly AcsThresholds _thresholds;
    private readonly object _spinLock = new();
    private int _spinStrikes;

    /// <summary>构造（默认内嵌阈值）。</summary>
    public CostGovernor(AcsThresholds? thresholds = null)
    {
        _thresholds = thresholds ?? AcsThresholds.Default;
    }

    /// <summary>当前空转 strike 数（诊断用）。</summary>
    public int SpinStrikes
    {
        get { lock (_spinLock) return _spinStrikes; }
    }

    /// <summary>
    /// 检查单步度量。返回退出码：0=PASS / 1=BLOCK / 2=USAGE_ERROR。
    /// 空转 strike 在检查中累计（有新证据即清零）。
    /// </summary>
    public AcsGateResult CheckStep(AcsStepReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var loop = _thresholds.Loop;
        var violations = new List<AcsGateViolation>();
        var block = false;

        // ① 窄步闸：单步单产出 + 预算
        if (report.OutputsCount > loop.MaxOutputsPerStep)
        {
            violations.Add(new AcsGateViolation(
                "narrow-step",
                $"单步产出 {report.OutputsCount} > {loop.MaxOutputsPerStep}（窄步闸：单步 1 个可验证产出，先拆步再开工）",
                IsWarningOnly: false));
            block = true;
        }

        if (report.BudgetMinUsed > loop.MaxBudgetMin)
        {
            violations.Add(new AcsGateViolation(
                "narrow-step",
                $"单步预算 {report.BudgetMinUsed:F1}min > {loop.MaxBudgetMin}min（窄步闸预算上限）",
                IsWarningOnly: false));
            block = true;
        }

        // ② 回灌禁令：总结字符 / 上下文字节 / 读文件数
        if (report.SummaryChars > loop.MaxSummaryChars + loop.SummaryCharsTolerance)
        {
            violations.Add(new AcsGateViolation(
                "refeed-ban",
                $"总结 {report.SummaryChars} 字 > {loop.MaxSummaryChars}+{loop.SummaryCharsTolerance} 容差（回灌禁令：改读状态文件+摘要）",
                IsWarningOnly: false));
            block = true;
        }
        else if (report.SummaryChars > loop.MaxSummaryChars)
        {
            violations.Add(new AcsGateViolation(
                "refeed-ban",
                $"总结 {report.SummaryChars} 字超出目标 {loop.MaxSummaryChars}（容差内，告警）",
                IsWarningOnly: true));
        }

        if (report.ContextBytes > loop.MaxContextBytes)
        {
            violations.Add(new AcsGateViolation(
                "refeed-ban",
                $"单步引用上文 {report.ContextBytes}B > {loop.MaxContextBytes}B（回灌禁令）",
                IsWarningOnly: false));
            block = true;
        }

        if (report.FilesRead > loop.MaxFilesRead)
        {
            violations.Add(new AcsGateViolation(
                "refeed-ban",
                $"单步读文件 {report.FilesRead} > {loop.MaxFilesRead}（回灌禁令：定点读取）",
                IsWarningOnly: false));
            block = true;
        }

        // ④ 思考预算闸
        if (report.ThinkRatio > loop.MaxThinkRatio)
        {
            violations.Add(new AcsGateViolation(
                "think-budget",
                $"思考占比 {report.ThinkRatio:P0} > {loop.MaxThinkRatio:P0}（思考预算闸：转为写最小可执行验证）",
                IsWarningOnly: false));
            block = true;
        }

        // ③ 空转闸：two-strike
        lock (_spinLock)
        {
            if (report.HasEvidenceDelta)
            {
                _spinStrikes = 0;
            }
            else
            {
                _spinStrikes++;
                if (_spinStrikes >= loop.SpinStrikes)
                {
                    violations.Add(new AcsGateViolation(
                        "spin-guard",
                        $"连续 {_spinStrikes} 步无新证据（空转闸 two-strike：立即停止并升级，禁止同法重试）",
                        IsWarningOnly: false));
                    block = true;
                }
                else
                {
                    violations.Add(new AcsGateViolation(
                        "spin-guard",
                        $"本步无新证据（strike {_spinStrikes}/{loop.SpinStrikes}，再犯即阻断）",
                        IsWarningOnly: true));
                }
            }
        }

        return new AcsGateResult(block ? AcsExitCodes.Block : AcsExitCodes.Pass, violations);
    }

    /// <summary>重置空转计数（新任务/新阶段开始时）。</summary>
    public void ResetSpin()
    {
        lock (_spinLock) _spinStrikes = 0;
    }
}
