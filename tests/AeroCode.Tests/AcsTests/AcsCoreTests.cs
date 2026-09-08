// Copyright (c) AeroCode
// ACS-1 纪律运行时核心测试：每个组件正/负样本成对（闸门触发侧 + 不触发侧）。
using AeroCode.Harness.Acs;
using Xunit;

namespace AeroCode.Tests.AcsTests;

public sealed class AcsThresholdsTests
{
    [Fact]
    public void Default_LoadsFromEmbeddedResource_AllSectionsPresent()
    {
        var t = AcsThresholds.Default;

        Assert.False(string.IsNullOrEmpty(t.SpecVersion));
        Assert.Equal(4, t.Tiers.Count); // T0-T3
        Assert.True(t.Loop.MaxSummaryChars > 0);
        Assert.True(t.Retry.MaxRetries > 0);
        Assert.True(t.Handoff.MaxCharsHard > 0);
        Assert.True(t.VerifyRank.PivotK > 0);
        Assert.False(string.IsNullOrEmpty(t.RealityScan.AllowMarker));
        Assert.True(t.VaguePhrases.Acceptance.Count > 0);
        Assert.True(t.VaguePhrases.Evidence.Count > 0);
    }

    [Fact]
    public void Tier_ReturnsSpec_KnownAndUnknownFallback()
    {
        var t = AcsThresholds.Default;

        var t3 = t.Tier("T3");
        Assert.True(t3.N >= 3); // T3 候选数 ≥3
        Assert.True(t3.MaxMinutes >= 120);

        // 未知分级回退 T1（保守）
        var unknown = t.Tier("T9");
        Assert.Equal(t.Tier("T1").MaxRounds, unknown.MaxRounds);
    }

    [Fact]
    public void ExitCodes_MatchAcsContract()
    {
        Assert.Equal(0, AcsExitCodes.Pass);
        Assert.Equal(1, AcsExitCodes.Block);
        Assert.Equal(2, AcsExitCodes.UsageError);
    }
}

public sealed class TaskGraderTests
{
    private readonly TaskGrader _grader = new();

    [Fact]
    public void ReadOnly_GradesT0()
    {
        var r = _grader.Grade(new AcsTaskProfile { IsReadOnly = true });
        Assert.Equal(AcsTier.T0, r.Tier);
    }

    [Fact]
    public void SingleFileReversibleShort_GradesT1()
    {
        var r = _grader.Grade(new AcsTaskProfile
        {
            FileCount = 1,
            EstimatedMinutes = 10,
        });
        Assert.Equal(AcsTier.T1, r.Tier);
    }

    [Theory]
    [InlineData(3, false, false, false)]   // 多文件
    [InlineData(1, true, false, false)]    // 持久状态
    [InlineData(1, false, true, false)]    // 需测试
    [InlineData(1, false, false, true)]    // 涉及构建
    public void T2Triggers_GradeT2(int files, bool persistent, bool tests, bool build)
    {
        var r = _grader.Grade(new AcsTaskProfile
        {
            FileCount = files,
            PersistentState = persistent,
            RequiresTests = tests,
            InvolvesBuild = build,
            EstimatedMinutes = 10,
        });
        Assert.Equal(AcsTier.T2, r.Tier);
    }

    [Theory]
    [InlineData(true, false, false)]   // 不可逆
    [InlineData(false, true, false)]   // 架构级
    [InlineData(false, false, true)]   // 安全相关
    public void T3Triggers_GradeT3(bool irreversible, bool architectural, bool security)
    {
        var r = _grader.Grade(new AcsTaskProfile
        {
            Irreversible = irreversible,
            Architectural = architectural,
            SecurityRelevant = security,
            FileCount = 1,
        });
        Assert.Equal(AcsTier.T3, r.Tier);
    }

    [Fact]
    public void FiveFilesCrossModule_GradesT3()
    {
        var r = _grader.Grade(new AcsTaskProfile { FileCount = 5, CrossModule = true });
        Assert.Equal(AcsTier.T3, r.Tier);
    }

    [Fact]
    public void AmbiguousSignals_FallUpToT2()
    {
        // 单文件但耗时长且有外部副作用——信号不典型，拿不准取高一级
        var r = _grader.Grade(new AcsTaskProfile
        {
            FileCount = 1,
            EstimatedMinutes = 60,
            ExternalSideEffects = true,
        });
        Assert.Equal(AcsTier.T2, r.Tier);
        Assert.Contains("拿不准", r.Reason);
    }

    [Fact]
    public void GradeResult_CarriesTierParams()
    {
        var r = _grader.Grade(new AcsTaskProfile { Irreversible = true });
        Assert.True(r.CandidateCount >= 3);
        Assert.True(r.MaxMinutes >= 120);
        Assert.False(string.IsNullOrEmpty(r.Reason));
    }
}

public sealed class CostGovernorTests
{
    private static AcsStepReport OkStep(bool evidence = true) => new()
    {
        OutputsCount = 1,
        BudgetMinUsed = 10,
        ContextBytes = 5000,
        FilesRead = 3,
        SummaryChars = 300,
        ThinkRatio = 0.2,
        HasEvidenceDelta = evidence,
    };

    [Fact]
    public void CompliantStep_Passes()
    {
        var gov = new CostGovernor();
        var r = gov.CheckStep(OkStep());
        Assert.True(r.Passed);
        Assert.Equal(AcsExitCodes.Pass, r.ExitCode);
    }

    [Fact]
    public void TooManyOutputs_Blocks()
    {
        var gov = new CostGovernor();
        var r = gov.CheckStep(OkStep() with { OutputsCount = 3 });
        Assert.False(r.Passed);
        Assert.Contains(r.Violations, v => v.Gate == "narrow-step" && !v.IsWarningOnly);
    }

    [Fact]
    public void OverBudget_Blocks()
    {
        var gov = new CostGovernor();
        var r = gov.CheckStep(OkStep() with { BudgetMinUsed = 45 });
        Assert.False(r.Passed);
        Assert.Contains(r.Violations, v => v.Gate == "narrow-step");
    }

    [Fact]
    public void SummaryOverHardLimit_Blocks()
    {
        var gov = new CostGovernor();
        var r = gov.CheckStep(OkStep() with { SummaryChars = 1200 });
        Assert.False(r.Passed);
        Assert.Contains(r.Violations, v => v.Gate == "refeed-ban" && !v.IsWarningOnly);
    }

    [Fact]
    public void SummaryInTolerance_WarnsOnly()
    {
        var gov = new CostGovernor();
        // 1000 < x ≤ 1020：超目标但在容差内，仅告警
        var r = gov.CheckStep(OkStep() with { SummaryChars = 1010 });
        Assert.True(r.Passed);
        Assert.Contains(r.Violations, v => v.Gate == "refeed-ban" && v.IsWarningOnly);
    }

    [Fact]
    public void ContextOverLimit_Blocks()
    {
        var gov = new CostGovernor();
        var r = gov.CheckStep(OkStep() with { ContextBytes = 25000 });
        Assert.False(r.Passed);
    }

    [Fact]
    public void TooManyFilesRead_Blocks()
    {
        var gov = new CostGovernor();
        var r = gov.CheckStep(OkStep() with { FilesRead = 8 });
        Assert.False(r.Passed);
    }

    [Fact]
    public void ThinkRatioOver_Blocks()
    {
        var gov = new CostGovernor();
        var r = gov.CheckStep(OkStep() with { ThinkRatio = 0.6 });
        Assert.False(r.Passed);
        Assert.Contains(r.Violations, v => v.Gate == "think-budget");
    }

    [Fact]
    public void SpinGuard_TwoStrikes_Blocks_FirstStrikeWarns()
    {
        var gov = new CostGovernor();

        // 第 1 次无证据：告警不阻断
        var first = gov.CheckStep(OkStep(evidence: false));
        Assert.True(first.Passed);
        Assert.Contains(first.Violations, v => v.Gate == "spin-guard" && v.IsWarningOnly);

        // 第 2 次无证据：two-strike 阻断
        var second = gov.CheckStep(OkStep(evidence: false));
        Assert.False(second.Passed);
        Assert.Contains(second.Violations, v => v.Gate == "spin-guard" && !v.IsWarningOnly);
    }

    [Fact]
    public void SpinGuard_EvidenceResetsStrikes()
    {
        var gov = new CostGovernor();
        gov.CheckStep(OkStep(evidence: false)); // strike 1
        gov.CheckStep(OkStep(evidence: true));  // 清零
        var r = gov.CheckStep(OkStep(evidence: false)); // 又是 strike 1，不阻断
        Assert.True(r.Passed);
        Assert.Equal(1, gov.SpinStrikes);
    }
}

public sealed class BoundedRetryTests
{
    private readonly BoundedRetry _retry = new();

    [Fact]
    public void ValidRetry_Allowed()
    {
        var v = _retry.Evaluate(new AcsRetryRequest(1, "依赖缺失，补装后重试", "上次缺依赖，本次先安装"));
        Assert.True(v.Allowed);
    }

    [Fact]
    public void OverLimit_Denied()
    {
        var v = _retry.Evaluate(new AcsRetryRequest(3, "理由充分", "差异充分"));
        Assert.False(v.Allowed);
        Assert.Contains("上限", v.Reason);
    }

    [Fact]
    public void MissingReason_Denied()
    {
        var v = _retry.Evaluate(new AcsRetryRequest(1, "", "差异充分"));
        Assert.False(v.Allowed);
        Assert.Contains("retry_reason", v.Reason);
    }

    [Fact]
    public void MissingDelta_Denied()
    {
        var v = _retry.Evaluate(new AcsRetryRequest(1, "理由充分", ""));
        Assert.False(v.Allowed);
        Assert.Contains("delta_from_last", v.Reason);
    }

    [Fact]
    public void ZeroAttemptIndex_Denied()
    {
        var v = _retry.Evaluate(new AcsRetryRequest(0, "理由充分", "差异充分"));
        Assert.False(v.Allowed);
    }
}

public sealed class HandoffCompressorTests
{
    private readonly HandoffCompressor _compressor = new();

    private const string ValidHandoff =
        "目标：完成 ACS-1 纪律运行时核心\n" +
        "已完成：AcsThresholds/TaskGrader/CostGovernor 落地\n" +
        "下一步：写 BoundedRetry 与 HonestyLedger 并测试\n" +
        "drift_checked=true";

    [Fact]
    public void ValidHandoff_Passes()
    {
        var r = _compressor.Validate(ValidHandoff);
        Assert.True(r.Passed);
    }

    [Fact]
    public void EmptyHandoff_UsageError()
    {
        var r = _compressor.Validate("");
        Assert.Equal(AcsExitCodes.UsageError, r.ExitCode);
    }

    [Fact]
    public void OverHardLimit_Blocks()
    {
        var r = _compressor.Validate(ValidHandoff + new string('x', 1200));
        Assert.False(r.Passed);
        Assert.Contains(r.Issues, i => i.Contains("硬上限"));
    }

    [Fact]
    public void MissingGoalAnchor_Blocks()
    {
        var r = _compressor.Validate("已完成：x\n下一步：做下一步的事\n" + "drift_checked=true");
        Assert.False(r.Passed);
        Assert.Contains(r.Issues, i => i.Contains("目标锚点"));
    }

    [Fact]
    public void MissingNextStep_Blocks()
    {
        var r = _compressor.Validate("目标：完成核心组件\n已完成：x\n" + "drift_checked=true");
        Assert.False(r.Passed);
        Assert.Contains(r.Issues, i => i.Contains("下一步"));
    }

    [Fact]
    public void MissingDriftMarker_Blocks()
    {
        var r = _compressor.Validate("目标：完成核心组件开发\n已完成：x\n下一步：做下一步的事");
        Assert.False(r.Passed);
        Assert.Contains(r.Issues, i => i.Contains("偏离自检"));
    }
}

public sealed class HonestyLedgerTests
{
    [Fact]
    public void ValidEntry_Accepted()
    {
        var ledger = new HonestyLedger();
        var r = ledger.Record(HonestyEntryKind.Degraded,
            "子代理派发", "基础设施拒绝模板", "审查降级为编排者自审", "缺独立第二视角");
        Assert.True(r.Accepted);
        Assert.NotNull(r.Entry);
        Assert.Single(ledger.Entries);
    }

    [Fact]
    public void MissingField_Rejected()
    {
        var ledger = new HonestyLedger();
        var r = ledger.Record(HonestyEntryKind.Degraded, "主题内容说明", "原因详细说明", "", "差距详细说明");
        Assert.False(r.Accepted);
        Assert.Contains("影响面", r.Error);
        Assert.Empty(ledger.Entries);
    }

    [Fact]
    public void ShortField_Rejected()
    {
        var ledger = new HonestyLedger();
        var r = ledger.Record(HonestyEntryKind.ReducedDimension, "主题内容说明", "ab", "影响面说明", "差距详细说明");
        Assert.False(r.Accepted);
        Assert.Contains("原因", r.Error);
    }

    [Fact]
    public void ExportMarkdown_ContainsEntries()
    {
        var ledger = new HonestyLedger();
        ledger.Record(HonestyEntryKind.Degraded, "子代理派发", "基础设施拒绝", "降级为自审", "缺独立视角");
        var md = ledger.ExportMarkdown();
        Assert.Contains("[DEGRADED]", md);
        Assert.Contains("子代理派发", md);
    }

    [Fact]
    public void EmptyLedger_ExportMarkdownSaysSo()
    {
        var ledger = new HonestyLedger();
        Assert.Contains("无降级", ledger.ExportMarkdown());
    }

    [Fact]
    public void Clear_EmptiesLedger()
    {
        var ledger = new HonestyLedger();
        ledger.Record(HonestyEntryKind.Degraded, "主题说明", "原因说明", "影响面说明", "差距说明");
        ledger.Clear();
        Assert.Empty(ledger.Entries);
    }
}
