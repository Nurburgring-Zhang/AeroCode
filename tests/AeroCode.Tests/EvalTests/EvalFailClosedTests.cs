using AeroCode.Eval;
using Xunit;

namespace AeroCode.Tests.EvalTests;

/// <summary>
/// 输出路径 fail-closed 硬验收：只允许写 eval/reports/（或显式授权目录），越界即拒且不产生文件。
/// </summary>
public sealed class EvalFailClosedTests
{
    [Fact]
    public void Guard_RejectsPathOutsideAllowedRoots()
    {
        var allowed = EvalTestHost.CreateTempDir();
        var outside = EvalTestHost.CreateTempDir();
        try
        {
            var violation = Assert.Throws<EvalPathPolicyException>(() =>
                OutputPathGuard.ValidateWritablePath(Path.Combine(outside, "baseline.md"), [allowed]));

            Assert.Contains("fail-closed", violation.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(outside, "baseline.md")));
        }
        finally
        {
            EvalTestHost.DeleteTempDir(allowed);
            EvalTestHost.DeleteTempDir(outside);
        }
    }

    [Fact]
    public void Guard_RejectsDotDotTraversalEscape()
    {
        var allowed = EvalTestHost.CreateTempDir();
        var outside = EvalTestHost.CreateTempDir();
        try
        {
            var escapeTarget = Path.Combine(allowed, "..", Path.GetFileName(outside), "escape.md");

            Assert.Throws<EvalPathPolicyException>(() =>
                OutputPathGuard.ValidateWritablePath(escapeTarget, [allowed]));
        }
        finally
        {
            EvalTestHost.DeleteTempDir(allowed);
            EvalTestHost.DeleteTempDir(outside);
        }
    }

    [Fact]
    public void Guard_AllowsNestedPathInsideRoot()
    {
        var allowed = EvalTestHost.CreateTempDir();
        try
        {
            var resolved = OutputPathGuard.ValidateWritablePath(Path.Combine(allowed, "sub", "report.md"), [allowed]);

            Assert.Equal(Path.GetFullPath(Path.Combine(allowed, "sub", "report.md")), resolved);
        }
        finally
        {
            EvalTestHost.DeleteTempDir(allowed);
        }
    }

    [Fact]
    public void Guard_EmptyAllowedRoots_RejectsEverything()
    {
        Assert.Throws<EvalPathPolicyException>(
            () => OutputPathGuard.ValidateWritablePath(Path.Combine(Path.GetTempPath(), "x.md"), []));
    }

    [Fact]
    public async Task Runner_OutsideAllowedRoots_RefusesAndCreatesNoFile()
    {
        var repoRoot = EvalTestHost.FindRepoRoot() ?? Path.GetTempPath();
        var outside = EvalTestHost.CreateTempDir();
        try
        {
            var outPath = Path.Combine(outside, "baseline.md");

            // fail-closed 在 fixture 加载之前裁决：路径越界时连数据集都不读取。
            var violation = await Assert.ThrowsAsync<EvalPathPolicyException>(() =>
                new BaselineRunner().RunAsync(new BaselineRunOptions
                {
                    RepoRoot = repoRoot,
                    OutPath = outPath,
                }));

            Assert.Contains("fail-closed", violation.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(outPath), "越界路径不得产生文件");
        }
        finally
        {
            EvalTestHost.DeleteTempDir(outside);
        }
    }

    [Fact]
    public async Task Runner_AdditionalAllowedRootNotCoveringOutPath_Refuses()
    {
        var repoRoot = EvalTestHost.FindRepoRoot() ?? Path.GetTempPath();
        var allowed = EvalTestHost.CreateTempDir();
        var outside = EvalTestHost.CreateTempDir();
        try
        {
            var outPath = Path.Combine(outside, "baseline.md");

            await Assert.ThrowsAsync<EvalPathPolicyException>(() =>
                new BaselineRunner().RunAsync(new BaselineRunOptions
                {
                    RepoRoot = repoRoot,
                    OutPath = outPath,
                    AdditionalAllowedOutputRoots = [allowed],
                }));

            Assert.False(File.Exists(outPath));
        }
        finally
        {
            EvalTestHost.DeleteTempDir(allowed);
            EvalTestHost.DeleteTempDir(outside);
        }
    }

    // ---------- R2 修复 MED-1：after 子命令拒绝写入保留名 baseline.md（fail-closed） ----------

    [Theory]
    [InlineData("baseline.md")]                 // 直接命中
    [InlineData("Baseline.md")]                 // 大小写变体（win32 惯例不区分）
    [InlineData("BASELINE.MD")]                 // 全大写变体
    [InlineData("baseline.md ")]                // 尾随空白（Windows 落盘会剥除）
    [InlineData("baseline.md.")]                // 尾随点（Windows 落盘会剥除）
    [InlineData("baseline.md:hidden")]          // NTFS ADS 流后缀
    [InlineData("baseline.md::$DATA")]          // ADS 主数据流显式形态
    [InlineData("..\\reports\\baseline.md")]    // 归一化后命中
    public void Guard_DenyBaselineOverwrite_RejectsReservedNameVariants(string fileName)
    {
        var allowed = EvalTestHost.CreateTempDir();
        try
        {
            var violation = Assert.Throws<EvalPathPolicyException>(() =>
                OutputPathGuard.ValidateWritablePath(
                    Path.Combine(allowed, "reports", fileName), [allowed], denyBaselineOverwrite: true));

            Assert.Contains("baseline.md", violation.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(Path.Combine(allowed, "reports", "baseline.md")));
        }
        finally
        {
            EvalTestHost.DeleteTempDir(allowed);
        }
    }

    [Fact]
    public void Guard_DenyBaselineOverwrite_AllowsOtherNames()
    {
        var allowed = EvalTestHost.CreateTempDir();
        try
        {
            var resolved = OutputPathGuard.ValidateWritablePath(
                Path.Combine(allowed, "after.md"), [allowed], denyBaselineOverwrite: true);

            Assert.Equal(Path.GetFullPath(Path.Combine(allowed, "after.md")), resolved);
        }
        finally
        {
            EvalTestHost.DeleteTempDir(allowed);
        }
    }

    [Fact]
    public void Guard_DefaultFlag_BaselineNameRemainsWritable()
    {
        // 默认 denyBaselineOverwrite=false：baseline 子命令生成基线是合法操作（现行为不变）。
        var allowed = EvalTestHost.CreateTempDir();
        try
        {
            var resolved = OutputPathGuard.ValidateWritablePath(
                Path.Combine(allowed, "baseline.md"), [allowed]);

            Assert.Equal(Path.GetFullPath(Path.Combine(allowed, "baseline.md")), resolved);
        }
        finally
        {
            EvalTestHost.DeleteTempDir(allowed);
        }
    }

    [Fact]
    public async Task Runner_AfterDenyBaselineOverwrite_RejectsBaselineReportPathBeforeAnyIo()
    {
        // after 子命令（DenyBaselineOverwrite=true）的 --out 指向 baseline.md：
        // fail-closed 在任何文件/目录创建之前拒绝。
        var repoRoot = EvalTestHost.FindRepoRoot() ?? Path.GetTempPath();
        var allowed = EvalTestHost.CreateTempDir();
        try
        {
            var outPath = Path.Combine(allowed, "baseline.md");

            var violation = await Assert.ThrowsAsync<EvalPathPolicyException>(() =>
                new BaselineRunner().RunAsync(new BaselineRunOptions
                {
                    RepoRoot = repoRoot,
                    OutPath = outPath,
                    AdditionalAllowedOutputRoots = [allowed],
                    DenyBaselineOverwrite = true,
                }));

            Assert.Contains("baseline.md", violation.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(outPath), "保留名路径不得产生文件");
            Assert.False(Directory.Exists(Path.Combine(allowed, "datasets")));
        }
        finally
        {
            EvalTestHost.DeleteTempDir(allowed);
        }
    }
}
