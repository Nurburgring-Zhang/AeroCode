namespace AeroCode.Eval;

/// <summary>输出路径越界（fail-closed 拒绝执行）。</summary>
public sealed class EvalPathPolicyException : Exception
{
    public EvalPathPolicyException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// 评测 runner 输出的 fail-closed 路径策略：只允许写在允许根目录集合之内
/// （默认 = 仓库的 eval/reports/；显式传入的 --out 额外授权目录经 AdditionalAllowedOutputRoots 注入），
/// 相对路径先归一化（防 ../ 逃逸），越界即拒——拒绝发生在任何文件/目录创建之前。
/// 路径比较按 Windows 惯例忽略大小写（本仓库目标平台 win32）。
/// R2 修复 MED-1：<see cref="ValidateWritablePath(string, IReadOnlyList&lt;string&gt;, bool)"/> 可选开启
/// 基线文件保留名拒绝（after 子命令防 --out 覆盖 baseline.md，fail-closed）。
/// </summary>
public static class OutputPathGuard
{
    public static string ValidateWritablePath(string outputPath, IReadOnlyList<string> allowedRoots) =>
        ValidateWritablePath(outputPath, allowedRoots, denyBaselineOverwrite: false);

    /// <summary>
    /// 校验可写路径。<paramref name="denyBaselineOverwrite"/> = true 时（R2 修复 MED-1，after 子命令专用），
    /// 文件名保留名 baseline.md（含大小写变体、尾随空白/点、ADS 流后缀）一律 fail-closed 拒绝，
    /// 防止评测输出覆盖基线报告；baseline 子命令保持 false（生成基线是合法操作）。
    /// </summary>
    public static string ValidateWritablePath(string outputPath, IReadOnlyList<string> allowedRoots, bool denyBaselineOverwrite)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            throw new EvalPathPolicyException("输出路径为空（fail-closed 拒绝）");
        }

        if (allowedRoots.Count == 0)
        {
            throw new EvalPathPolicyException("允许输出根目录集合为空（fail-closed 拒绝一切写入）");
        }

        var full = Path.GetFullPath(outputPath);
        if (denyBaselineOverwrite && IsReservedBaselineName(full))
        {
            throw new EvalPathPolicyException(
                $"输出路径为保留名（fail-closed 拒绝）：after 子命令不允许写入基线报告 baseline.md（命中：{full}）。" +
                "请改用其他文件名，避免评测输出覆盖基线。");
        }

        var normalizedRoots = new List<string>(allowedRoots.Count);
        foreach (var root in allowedRoots)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            normalizedRoots.Add(fullRoot);
            if (IsInside(full, fullRoot))
            {
                return full;
            }
        }

        throw new EvalPathPolicyException(
            $"输出路径越界（fail-closed）：{full} 不在任何允许根目录之内；允许根 = [{string.Join("; ", normalizedRoots)}]。评测产物只允许写 eval/reports/ 或显式授权目录。");
    }

    private static bool IsInside(string fullPath, string fullRoot)
    {
        if (string.Equals(fullPath, fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var prefix = fullRoot + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// R2 修复 MED-1：判定最终路径的文件名是否为保留名 baseline.md。
    /// 覆盖变体：大小写（win32 惯例不区分）、尾随空白与点（Windows 文件系统会剥除）、
    /// NTFS ADS 流后缀（"baseline.md:*" 的冒号前缀段）。
    /// </summary>
    private static bool IsReservedBaselineName(string fullPath)
    {
        var fileName = Path.GetFileName(fullPath);
        if (fileName.Length == 0)
        {
            return false;
        }

        // Windows 在落盘时会剥除尾随空白/点——按落盘后的名字判定，防止 "baseline.md." 绕过。
        fileName = fileName.TrimEnd(' ', '.');
        var colon = fileName.IndexOf(':');
        if (colon >= 0)
        {
            fileName = fileName[..colon]; // ADS 流后缀（含 "::$DATA"）：只看主文件名段
        }

        return string.Equals(fileName, "baseline.md", StringComparison.OrdinalIgnoreCase);
    }
}
