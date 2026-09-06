using System.Text.RegularExpressions;
using AeroCode.Eval;
using Xunit;

namespace AeroCode.Tests.EvalTests;

/// <summary>
/// dry-run 硬验收：无网关环境跑 baseline → 报告生成、结构完整（三指标段都在）、
/// 每个指标值位置为 PENDING、全程无密钥形态文本。
/// </summary>
public sealed class EvalDryRunTests
{
    [SkippableFact]
    public async Task Baseline_WithoutGateway_ProducesCompletePendingReport()
    {
        var root = EvalTestHost.FindRepoRoot();
        Skip.If(root is null, "测试程序集目录向上未找到 AeroCode.sln——无源码树环境跳过");
        var outDir = EvalTestHost.CreateTempDir();
        try
        {
            var log = new StringWriter();
            var result = await new BaselineRunner().RunAsync(new BaselineRunOptions
            {
                RepoRoot = root!,
                OutPath = Path.Combine(outDir, "baseline-dry-run.md"),
                AdditionalAllowedOutputRoots = [outDir],
                Log = log,
            });

            Assert.Equal("dry-run", result.Mode);
            Assert.True(File.Exists(result.ReportPath), "报告未生成");

            var markdown = File.ReadAllText(result.ReportPath);
            Assert.Contains("# AeroCode Eval", markdown, StringComparison.Ordinal);
            Assert.Contains("多轮幻觉率 (multi_turn_hallucination_rate)", markdown, StringComparison.Ordinal);
            Assert.Contains("困难题 checkpoint 通过率 (checkpoint_pass_rate)", markdown, StringComparison.Ordinal);
            Assert.Contains("单位成本完成率 (unit_cost_completion)", markdown, StringComparison.Ordinal);
            Assert.Contains("## 数据集清单", markdown, StringComparison.Ordinal);
            Assert.Contains("## 附录", markdown, StringComparison.Ordinal);
            Assert.True(
                Regex.Matches(markdown, "PENDING").Count >= 3,
                "每个指标值位置都应标 PENDING");
            Assert.Contains("v0", markdown, StringComparison.Ordinal);
            Assert.False(
                Regex.IsMatch(markdown, @"sk-[A-Za-z0-9_\-]{8,}"),
                "报告中不得出现 sk- 形态密钥原文");

            var logText = log.ToString();
            Assert.Contains("dry-run", logText, StringComparison.Ordinal);
            Assert.False(
                Regex.IsMatch(logText, @"sk-[A-Za-z0-9_\-]{8,}"),
                "日志中不得出现 sk- 形态密钥原文");
        }
        finally
        {
            EvalTestHost.DeleteTempDir(outDir);
        }
    }

    [SkippableFact]
    public async Task Baseline_WithoutGateway_AllThreeMetricsAndAllTaskOutcomesPending()
    {
        var root = EvalTestHost.FindRepoRoot();
        Skip.If(root is null, "测试程序集目录向上未找到 AeroCode.sln——无源码树环境跳过");
        var outDir = EvalTestHost.CreateTempDir();
        try
        {
            var result = await new BaselineRunner().RunAsync(new BaselineRunOptions
            {
                RepoRoot = root!,
                OutPath = Path.Combine(outDir, "baseline-dry-run.md"),
                AdditionalAllowedOutputRoots = [outDir],
            });

            Assert.Equal(3, result.Metrics.Count);
            Assert.All(result.Metrics, metric =>
            {
                Assert.Equal("PENDING", metric.Status);
                Assert.Equal("PENDING", metric.Value);
                Assert.NotEmpty(metric.Tasks);
                Assert.All(metric.Tasks, task =>
                {
                    Assert.Equal("PENDING", task.Status);
                    Assert.Null(task.Value);
                });
            });
        }
        finally
        {
            EvalTestHost.DeleteTempDir(outDir);
        }
    }

    [SkippableFact]
    public async Task Baseline_WithGatewayKeyEnvSet_ProbeFailureNeverLeaksKeyIntoReportOrLog()
    {
        var root = EvalTestHost.FindRepoRoot();
        Skip.If(root is null, "测试程序集目录向上未找到 AeroCode.sln——无源码树环境跳过");
        const string probeKey = "sk-leakprobe-0123456789abcdef";
        var outDir = EvalTestHost.CreateTempDir();
        try
        {
            Environment.SetEnvironmentVariable(EvalSecrets.GatewayKeyVariable, probeKey);
            Environment.SetEnvironmentVariable(EvalSecrets.GatewayUrlVariable, "http://127.0.0.1:9");
            var log = new StringWriter();
            var gateway = EvalGatewaySettings.FromEnvironment();
            Assert.NotNull(gateway);
            Assert.Equal(new Uri("http://127.0.0.1:9"), gateway!.BaseUrl);

            var result = await new BaselineRunner().RunAsync(new BaselineRunOptions
            {
                RepoRoot = root!,
                OutPath = Path.Combine(outDir, "baseline-leak-probe.md"),
                AdditionalAllowedOutputRoots = [outDir],
                Gateway = gateway,
                Log = log,
            });

            // 探活必败（127.0.0.1:9 discard 端口）→ 诚实降级 PENDING，不伪造数值。
            Assert.Equal("dry-run", result.Mode);
            Assert.All(result.Metrics, metric => Assert.Equal("PENDING", metric.Status));

            var markdown = File.ReadAllText(result.ReportPath);
            Assert.DoesNotContain(probeKey, markdown, StringComparison.Ordinal);
            Assert.DoesNotContain("sk-leakprobe", markdown, StringComparison.Ordinal);
            Assert.DoesNotContain(probeKey, log.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(EvalSecrets.GatewayKeyVariable, null);
            Environment.SetEnvironmentVariable(EvalSecrets.GatewayUrlVariable, null);
            EvalTestHost.DeleteTempDir(outDir);
        }
    }

    [Fact]
    public void Program_UnknownSubcommand_ReturnsUsageErrorAndHintsBaseline()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = Program.Run(["nonsense"], stdout, stderr);

        Assert.Equal(2, exitCode);
        Assert.Contains("baseline", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Program_OutWithoutValue_ReturnsUsageError()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = Program.Run(["baseline", "--out"], stdout, stderr);

        Assert.Equal(2, exitCode);
        Assert.Contains("--out", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Program_Help_ReturnsZeroWithUsage()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = Program.Run(["--help"], stdout, stderr);

        Assert.Equal(0, exitCode);
        Assert.Contains("--out", stdout.ToString(), StringComparison.Ordinal);
    }
}
