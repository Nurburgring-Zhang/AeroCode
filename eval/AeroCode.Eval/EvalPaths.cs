namespace AeroCode.Eval;

/// <summary>
/// eval 目录约定与仓库根发现（上溯查找 AeroCode.sln，与既有测试的 FindRepoRoot 惯例一致）。
/// </summary>
public static class EvalPaths
{
    public const string SolutionFileName = "AeroCode.sln";

    /// <summary>从 <paramref name="startDirectory"/> 上溯查找仓库根（含 AeroCode.sln 的目录）；找不到返回 null。</summary>
    public static string? FindRepoRoot(string startDirectory)
    {
        var dir = new DirectoryInfo(Path.GetFullPath(string.IsNullOrWhiteSpace(startDirectory) ? "." : startDirectory));
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, SolutionFileName)))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }

    public static string DefaultDatasetsDir(string repoRoot) => Path.Combine(repoRoot, "eval", "datasets");

    public static string DefaultReportsDir(string repoRoot) => Path.Combine(repoRoot, "eval", "reports");

    public static string DefaultReportPath(string repoRoot) =>
        Path.Combine(DefaultReportsDir(repoRoot), "baseline.md");

    /// <summary>after 对照报告默认路径（C3 compare 用；绝不指向 baseline.md）。</summary>
    public static string DefaultAfterReportPath(string repoRoot) =>
        Path.Combine(DefaultReportsDir(repoRoot), "after.md");
}
