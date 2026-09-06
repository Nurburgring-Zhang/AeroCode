// Copyright (c) AeroCode
// C3 compare 语义钉子：全绿通过、单指标回退→非 0、PENDING 不参与回退判定、
// 解析失败/字段缺失→fail-closed 非 0；Program 子命令退出码 0/2/5/6；
// dry-run 端到端（两份真实采集管线报告 → compare 退出码 0）。
using System.Text;
using AeroCode.Eval;
using Xunit;

namespace AeroCode.Tests.EvalTests;

public sealed class EvalCompareTests : IDisposable
{
    private readonly string _dir = EvalTestHost.CreateTempDir();

    public void Dispose() => EvalTestHost.DeleteTempDir(_dir);

    /// <summary>写一份最小结构但字段完整的三指标报告；占位符 PENDING / 数值文本。</summary>
    private string WriteReport(string name, string hallValue, string ckptValue, string costValue)
    {
        var path = Path.Combine(_dir, name);
        var builder = new StringBuilder();
        builder.AppendLine("# AeroCode Eval · 三指标基线报告（fixture v0）");
        builder.AppendLine();
        builder.AppendLine("## 1. 多轮幻觉率 (multi_turn_hallucination_rate)");
        builder.AppendLine();
        builder.AppendLine($"- 值: **{hallValue}**");
        builder.AppendLine();
        builder.AppendLine("## 2. 困难题 checkpoint 通过率 (checkpoint_pass_rate)");
        builder.AppendLine();
        builder.AppendLine($"- 值: **{ckptValue}**");
        builder.AppendLine();
        builder.AppendLine("## 3. 单位成本完成率 (unit_cost_completion)");
        builder.AppendLine();
        builder.AppendLine($"- 值: **{costValue}**");
        builder.AppendLine();
        builder.AppendLine("## 附录");
        builder.AppendLine();
        builder.AppendLine("- 采集模式说明: 测试合成报告。");
        File.WriteAllText(path, builder.ToString());
        return path;
    }

    private const string Pending = "PENDING";
    private const string HallGood = "33.33%（1/3 任务判定幻觉）";
    private const string HallBad = "66.67%（2/3 任务判定幻觉）";
    private const string CkptGood = "75.00%（9/12 checkpoint）";
    private const string CkptBad = "50.00%（6/12 checkpoint）";
    private const string CostGood = "Σcost/Σquality = 0.001234 USD/分；Σtokens/Σquality = 456 tok/分";
    private const string CostBad = "Σcost/Σquality = 0.004321 USD/分；Σtokens/Σquality = 456 tok/分";

    private CompareRunResult Compare((string Hall, string Ckpt, string Cost) before, (string Hall, string Ckpt, string Cost) after)
    {
        var log = new StringWriter();
        return new CompareRunner().Compare(new CompareRunOptions
        {
            BeforePath = WriteReport("before.md", before.Hall, before.Ckpt, before.Cost),
            AfterPath = WriteReport("after.md", after.Hall, after.Ckpt, after.Cost),
            Log = log,
        });
    }

    // ---------- 通过 / 回退语义 ----------

    [Fact]
    public void BothPending_Passes_NoRegressionJudged()
    {
        // dry-run 环境两份都 PENDING → 通过（PENDING 口径不参与回退判定）。
        var result = Compare((Pending, Pending, Pending), (Pending, Pending, Pending));

        Assert.True(result.Passed);
        Assert.Empty(result.Errors);
        Assert.All(result.Comparisons, c => Assert.Equal(CompareRunner.OutcomeSkipped, c.Outcome));
    }

    [Fact]
    public void AllMeasured_NoRegressions_Passes()
    {
        var result = Compare((HallGood, CkptGood, CostGood), (HallGood, CkptGood, CostGood));

        Assert.True(result.Passed);
        Assert.All(result.Comparisons, c => Assert.Equal(CompareRunner.OutcomePass, c.Outcome));
    }

    [Fact]
    public void HallucinationRateRises_IsRegression()
    {
        var result = Compare((HallGood, CkptGood, CostGood), (HallBad, CkptGood, CostGood));

        Assert.False(result.Passed);
        var regression = Assert.Single(result.Comparisons, c => c.Outcome == CompareRunner.OutcomeRegression);
        Assert.Equal("multi_turn_hallucination_rate", regression.MetricKey);
        Assert.NotNull(regression.RegressionDelta);
    }

    [Fact]
    public void CheckpointPassRateDrops_IsRegression()
    {
        var result = Compare((HallGood, CkptGood, CostGood), (HallGood, CkptBad, CostGood));

        Assert.False(result.Passed);
        var regression = Assert.Single(result.Comparisons, c => c.Outcome == CompareRunner.OutcomeRegression);
        Assert.Equal("checkpoint_pass_rate", regression.MetricKey);
    }

    [Fact]
    public void UnitCostRises_IsRegression()
    {
        var result = Compare((HallGood, CkptGood, CostGood), (HallGood, CkptGood, CostBad));

        Assert.False(result.Passed);
        var regression = Assert.Single(result.Comparisons, c => c.Outcome == CompareRunner.OutcomeRegression);
        Assert.Equal("unit_cost_completion", regression.MetricKey);
    }

    [Fact]
    public void ImprovementOnAllMetrics_Passes()
    {
        var result = Compare((HallBad, CkptBad, CostBad), (HallGood, CkptGood, CostGood));

        Assert.True(result.Passed);
        Assert.All(result.Comparisons, c => Assert.Equal(CompareRunner.OutcomePass, c.Outcome));
    }

    // ---------- PENDING 不参与判定 ----------

    [Fact]
    public void PendingOnOneSide_SkippedNotJudged()
    {
        // before 已实测、after PENDING → 该指标跳过；其余持平 → 整体通过。
        var result = Compare((Pending, CkptGood, CostGood), (HallBad, CkptGood, CostGood));

        Assert.True(result.Passed);
        var skipped = Assert.Single(result.Comparisons, c => c.Outcome == CompareRunner.OutcomeSkipped);
        Assert.Equal("multi_turn_hallucination_rate", skipped.MetricKey);
    }

    // ---------- fail-closed：解析失败 / 字段缺失 ----------

    [Fact]
    public void MissingMetricSection_FailClosed()
    {
        var before = WriteReport("before-missing.md", Pending, Pending, Pending);
        var after = Path.Combine(_dir, "after-missing.md");
        File.WriteAllText(after,
            "# AeroCode Eval\n\n## 1. 多轮幻觉率 (multi_turn_hallucination_rate)\n\n- 值: **PENDING**\n");

        var result = new CompareRunner().Compare(new CompareRunOptions
        {
            BeforePath = before,
            AfterPath = after,
        });

        Assert.False(result.Passed); // 绝不默认通过
        Assert.Contains(result.Errors, e => e.Contains("checkpoint_pass_rate"));
        Assert.Contains(result.Errors, e => e.Contains("unit_cost_completion"));
    }

    [Fact]
    public void MissingValueLine_FailClosed()
    {
        var before = WriteReport("before-noval.md", Pending, Pending, Pending);
        var after = Path.Combine(_dir, "after-noval.md");
        File.WriteAllText(after,
            """
            # AeroCode Eval

            ## 1. 多轮幻觉率 (multi_turn_hallucination_rate)

            - 值: **PENDING**

            ## 2. 困难题 checkpoint 通过率 (checkpoint_pass_rate)

            - 口径: 有段无值行。

            ## 3. 单位成本完成率 (unit_cost_completion)

            - 值: **PENDING**
            """);

        var result = new CompareRunner().Compare(new CompareRunOptions
        {
            BeforePath = before,
            AfterPath = after,
        });

        Assert.False(result.Passed);
        Assert.Contains(result.Errors, e => e.Contains("checkpoint_pass_rate") && e.Contains("值"));
    }

    [Fact]
    public void UnparseableValue_FailClosed()
    {
        // 值行存在但既非 PENDING 也非本口径数值形态 → 不可解析（不猜、不默认通过）。
        var result = Compare((Pending, Pending, Pending), (HallGood, "N/A", Pending));

        Assert.False(result.Passed);
        Assert.Contains(result.Errors, e => e.Contains("checkpoint_pass_rate") && e.Contains("不可解析"));
    }

    [Fact]
    public void MissingReportFile_FailClosed()
    {
        var before = WriteReport("before-only.md", Pending, Pending, Pending);

        var result = new CompareRunner().Compare(new CompareRunOptions
        {
            BeforePath = before,
            AfterPath = Path.Combine(_dir, "no-such-after.md"),
        });

        Assert.False(result.Passed);
        Assert.NotEmpty(result.Errors);
    }

    // ---------- Program 子命令退出码 ----------

    [Fact]
    public void Program_Compare_BothPending_ExitsZero()
    {
        var before = WriteReport("p-before.md", Pending, Pending, Pending);
        var after = WriteReport("p-after.md", Pending, Pending, Pending);

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exit = Program.Run(["compare", "--before", before, "--after", after], stdout, stderr);

        Assert.Equal(0, exit);
        Assert.Contains("Skipped", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Program_Compare_SingleRegression_ExitsFive()
    {
        var before = WriteReport("r-before.md", HallGood, CkptGood, CostGood);
        var after = WriteReport("r-after.md", HallBad, CkptGood, CostGood);

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exit = Program.Run(["compare", "--before", before, "--after", after], stdout, stderr);

        Assert.Equal(5, exit);
        Assert.Contains("multi_turn_hallucination_rate", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Program_Compare_ParseFailure_ExitsSix()
    {
        var before = WriteReport("f-before.md", Pending, Pending, Pending);
        var after = Path.Combine(_dir, "f-after.md");
        File.WriteAllText(after, "不是评测报告的内容");

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exit = Program.Run(["compare", "--before", before, "--after", after], stdout, stderr);

        Assert.Equal(6, exit);
        Assert.Contains("fail-closed", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Program_Compare_MissingArguments_UsageError()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        Assert.Equal(2, Program.Run(["compare"], stdout, stderr));
        Assert.Equal(2, Program.Run(["compare", "--before", "x"], stdout, stderr));
    }

    // ---------- dry-run 端到端（真实采集管线 × 2 → compare） ----------

    [SkippableFact]
    public async Task DryRunPipeline_BeforeAndAfter_CompareExitsZero()
    {
        var root = EvalTestHost.FindRepoRoot();
        Skip.If(root is null, "测试程序集目录向上未找到 AeroCode.sln——无源码树环境跳过");

        var beforeDir = EvalTestHost.CreateTempDir();
        var afterDir = EvalTestHost.CreateTempDir();
        try
        {
            var runner = new BaselineRunner();
            var before = await runner.RunAsync(new BaselineRunOptions
            {
                RepoRoot = root!,
                OutPath = Path.Combine(beforeDir, "baseline.md"),
                AdditionalAllowedOutputRoots = [beforeDir],
            });
            var after = await runner.RunAsync(new BaselineRunOptions
            {
                RepoRoot = root!,
                OutPath = Path.Combine(afterDir, "after.md"),
                AdditionalAllowedOutputRoots = [afterDir],
            });
            Assert.Equal("dry-run", before.Mode);
            Assert.Equal("dry-run", after.Mode);

            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            var exit = Program.Run(["compare", "--before", before.ReportPath, "--after", after.ReportPath], stdout, stderr);

            Assert.Equal(0, exit);
            Assert.Empty(stderr.ToString().Trim());
        }
        finally
        {
            EvalTestHost.DeleteTempDir(beforeDir);
            EvalTestHost.DeleteTempDir(afterDir);
        }
    }
}
