// Copyright (c) AeroCode
// WorkspaceContext — 工作区根解析 + 路径边界 + 排除规则（对标 opencode external_directory / tools gitignore）。
using System.Text;

namespace AeroAgent.Moa.Tools.Workspace;

/// <summary>
/// 工作区上下文：所有文件类工具的路径都经由它解析与校验。
/// 边界不变量：<see cref="Resolve"/> 只返回位于 <see cref="Root"/> 之下的绝对路径，
/// 越界输入返回 null——调用方（权限前置守卫）据此把裁决升级为 Ask，绝不静默放行。
/// 排除规则 = 内建默认排除集（.git/node_modules/bin/obj 等构建与依赖目录）
/// + 工作区根 .gitignore 的简化解析（支持注释/空行/字面前缀/*.ext/dir//锚定/通配单段；
/// 负模式 ! 与 ** 递归通配是已知简化点，命中 ** 时按单段通配处理）。
/// </summary>
public sealed class WorkspaceContext
{
    /// <summary>任何工作区都无条件排除的目录名（依赖与构建产物，搜索/遍历噪音与隐私风险源）。</summary>
    public static readonly IReadOnlyList<string> DefaultExcludedDirs = new[]
    {
        ".git", ".hg", ".svn", "node_modules", "__pycache__", ".venv", "venv",
        "bin", "obj", ".gradle", "build", "dist", ".idea", ".vs", ".next",
    };

    /// <summary>无论 gitignore 如何都拒绝读写的高敏感文件名（凭据面，对标 opencode .env 默认 deny）。</summary>
    public static readonly IReadOnlyList<string> SensitiveFileNames = new[]
    {
        ".env", ".env.local", ".env.production",
    };

    public string Root { get; }

    private readonly IReadOnlyList<GitignoreRule> _gitignore;

    public WorkspaceContext(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new ArgumentException("workspace root must not be empty", nameof(root));
        }

        var full = Path.GetFullPath(root);
        if (!Directory.Exists(full))
        {
            throw new DirectoryNotFoundException($"workspace root does not exist: {full}");
        }

        Root = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        _gitignore = LoadGitignore(Root);
    }

    /// <summary>
    /// 把相对/绝对路径解析为 Root 之下的绝对路径。越界、无效、驱动器逃逸
    /// （如 "C:/other"、"..\/.."、UNC 路径）一律返回 null。
    /// </summary>
    public string? Resolve(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        string candidate;
        try
        {
            candidate = Path.IsPathRooted(path)
                ? Path.GetFullPath(path)
                : Path.GetFullPath(Path.Combine(Root, path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        return IsWithin(candidate) ? candidate : null;
    }

    /// <summary>绝对路径是否位于工作区根之下（含根本身）。</summary>
    public bool IsWithin(string absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath))
        {
            return false;
        }

        var full = Path.GetFullPath(absolutePath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || string.Equals(full, Root, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>相对路径是否应被搜索/遍历类工具排除（默认排除集 + gitignore）。</summary>
    public bool ShouldExclude(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return true;
        }

        var segments = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var seg in segments)
        {
            if (DefaultExcludedDirs.Contains(seg, StringComparer.Ordinal))
            {
                return true;
            }
        }

        var name = segments[^1];
        if (SensitiveFileNames.Contains(name, StringComparer.Ordinal))
        {
            return true;
        }

        var normalized = relativePath.Replace('\\', '/').TrimStart('/');
        foreach (var rule in _gitignore)
        {
            if (rule.Matches(normalized, name))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>解析内部路径为 Root 相对展示形式（日志/审批弹窗用）。</summary>
    public string Display(string absolutePath)
    {
        var full = Path.GetFullPath(absolutePath);
        return full.Length > Root.Length
            ? full[(Root.Length + 1)..].Replace('\\', '/')
            : "/";
    }

    private static IReadOnlyList<GitignoreRule> LoadGitignore(string root)
    {
        var file = Path.Combine(root, ".gitignore");
        if (!File.Exists(file))
        {
            return Array.Empty<GitignoreRule>();
        }

        var rules = new List<GitignoreRule>();
        foreach (var raw in File.ReadAllLines(file, Encoding.UTF8))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            // 已知简化：! 负模式与 ** 递归通配不做完整 gitignore 语义（** 降级为单段通配）。
            if (line.StartsWith('!'))
            {
                continue;
            }

            var anchored = line.StartsWith('/');
            var pattern = line.TrimStart('/').TrimEnd('/');
            if (pattern.Length == 0)
            {
                continue;
            }

            var dirOnly = raw.TrimEnd().EndsWith('/');
            rules.Add(new GitignoreRule(pattern.Replace("**", "*"), anchored, dirOnly));
        }

        return rules;
    }

    private sealed record GitignoreRule(string Pattern, bool Anchored, bool DirOnly)
    {
        public bool Matches(string relativePath, string fileName)
        {
            // "dir/" 只排除目录形态（路径中有后续段或本身以 / 结尾在此归一后无法区分，
            // 简化处理：命中 pattern 于任一路径段即排除）。
            var target = DirOnly ? relativePath : relativePath;

            if (Pattern.Contains('*'))
            {
                var rx = "^" + System.Text.RegularExpressions.Regex
                    .Escape(Pattern)
                    .Replace(@"\*", "[^/]*") + (DirOnly || !Pattern.Contains('.') ? "(/.*)?$" : "$");
                return System.Text.RegularExpressions.Regex.IsMatch(
                    target, rx, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }

            // 字面模式：锚定时按前缀匹配，非锚定时任一路径段等于 pattern 即命中。
            if (Anchored)
            {
                return target.StartsWith(Pattern + "/", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(target, Pattern, StringComparison.OrdinalIgnoreCase);
            }

            return target.Split('/').Any(s => string.Equals(s, Pattern, StringComparison.OrdinalIgnoreCase))
                || string.Equals(fileName, Pattern, StringComparison.OrdinalIgnoreCase);
        }
    }
}
