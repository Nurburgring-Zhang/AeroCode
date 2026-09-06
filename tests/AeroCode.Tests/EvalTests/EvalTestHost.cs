namespace AeroCode.Tests.EvalTests;

/// <summary>EvalTests 共用：从测试程序集目录上溯仓库根（AeroCode.sln 标记），与既有测试惯例一致。</summary>
internal static class EvalTestHost
{
    public static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AeroCode.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }

    /// <summary>创建一次性临时目录（调用方负责 DeleteTempDir 清理）。</summary>
    public static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "aero-eval-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    public static void DeleteTempDir(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响测试结论。
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
