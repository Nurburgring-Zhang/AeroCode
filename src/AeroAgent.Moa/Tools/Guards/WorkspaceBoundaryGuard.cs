// Copyright (c) AeroCode
// WorkspaceBoundaryGuard — 工作区边界守卫（批次 B G3，builder-β）。
// 从 args 的 path/command 目标判定是否越出 WorkspaceContext.Root：
// 越界一律升级 Ask（不 Deny——工作区外的合法操作仍可由用户在弹窗里放行）；
// 纯字符串/路径数学判定，不读文件内容、不执行命令，无副作用、线程安全。
using System.Text;
using AeroAgent.Moa.Tools.Workspace;
using AeroCode.Harness.Permission;

namespace AeroAgent.Moa.Tools;

/// <summary>工具参数里被视为"路径目标"的键名判定（守卫共用，内部辅助）。</summary>
internal static class ToolArgPaths
{
    /// <summary>已知路径键（覆盖 WorkspaceToolbox 命名与常见工具域命名；小写比较）。</summary>
    private static readonly string[] KnownKeys =
    {
        "path", "file", "filepath", "dir", "directory", "target",
        "old_path", "oldpath", "new_path", "newpath",
    };

    /// <summary>
    /// 键名是否按路径语义解读：已知键，或键名以 path/file/dir/directory 结尾
    /// （如 sourcePath/outputDir）。command/content/pattern 等语义键不在此列。
    /// </summary>
    public static bool IsPathKey(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return false;
        }

        var k = key.ToLowerInvariant();
        foreach (var known in KnownKeys)
        {
            if (k == known)
            {
                return true;
            }
        }

        return k.EndsWith("path", StringComparison.Ordinal)
            || k.EndsWith("file", StringComparison.Ordinal)
            || k.EndsWith("dir", StringComparison.Ordinal)
            || k.EndsWith("directory", StringComparison.Ordinal);
    }

    /// <summary>跨平台文件名提取：同时按 / 与 \ 切分（Path.GetFileName 在 Unix 上不认反斜杠）。</summary>
    public static string FileNameOf(string raw)
    {
        var trimmed = raw.Trim().Trim('"', '\'');
        var lastSlash = trimmed.LastIndexOf('/');
        var lastBackslash = trimmed.LastIndexOf('\\');
        var cut = Math.Max(lastSlash, lastBackslash);
        return cut >= 0 ? trimmed[(cut + 1)..] : trimmed;
    }
}

/// <summary>
/// 工作区边界守卫：args 中路径目标无法解析进 <see cref="WorkspaceContext.Root"/>，
/// 或 shell 命令引用了工作区外的绝对路径目标（盘符/UNC/多段绝对路径/~）→ Ask。
/// </summary>
public sealed class WorkspaceBoundaryGuard : IToolGuard
{
    private readonly WorkspaceContext _workspace;

    public WorkspaceBoundaryGuard(WorkspaceContext workspace)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
    }

    /// <inheritdoc />
    public string Name => "workspace-boundary";

    /// <inheritdoc />
    public PermissionDecision? Check(string toolName, IReadOnlyDictionary<string, object?>? args)
    {
        if (args is null)
        {
            return null;
        }

        foreach (var kv in args)
        {
            if (kv.Value is not string s || string.IsNullOrWhiteSpace(s) || !ToolArgPaths.IsPathKey(kv.Key))
            {
                continue;
            }

            if (_workspace.Resolve(s) is null)
            {
                return PermissionDecision.Ask; // 越界/无法验证：交人工裁决
            }
        }

        if (args.TryGetValue("command", out var cmdObj) &&
            cmdObj is string command &&
            !string.IsNullOrWhiteSpace(command) &&
            CommandTouchesOutside(command))
        {
            return PermissionDecision.Ask;
        }

        return null;
    }

    /// <summary>命令的任一 token 触及工作区外绝对路径目标即视为越界。</summary>
    private bool CommandTouchesOutside(string command)
    {
        foreach (var token in TokenizeCommand(command))
        {
            if (TokenIsOutsidePath(token))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 引号感知分词：引号内的空白与操作符不切分 token（含空格路径保真）；
    /// 引号外按空白与常见 shell 操作符切割。引号字符本身从 token 剥离。
    /// </summary>
    internal static IEnumerable<string> TokenizeCommand(string command)
    {
        var sb = new StringBuilder();
        var inSingle = false;
        var inDouble = false;
        foreach (var ch in command)
        {
            if (ch == '\'' && !inDouble)
            {
                inSingle = !inSingle;
                continue;
            }

            if (ch == '"' && !inSingle)
            {
                inDouble = !inDouble;
                continue;
            }

            if (!inSingle && !inDouble &&
                (char.IsWhiteSpace(ch) || "|&;<>()".Contains(ch)))
            {
                if (sb.Length > 0)
                {
                    yield return sb.ToString();
                    sb.Clear();
                }

                continue;
            }

            sb.Append(ch);
        }

        if (sb.Length > 0)
        {
            yield return sb.ToString();
        }
    }

    /// <summary>
    /// 单个 token 是否为工作区外的路径目标。仅识别明确形态：
    /// Windows 盘符（C:\…）、UNC（\\…）、多段 POSIX 绝对路径（/usr/bin；/c 型单段旗标不算）、
    /// ~（家目录）。相对路径与裸词不判定（交给策略与分类守卫）。
    /// </summary>
    private bool TokenIsOutsidePath(string token)
    {
        if (token.Length == 0)
        {
            return false;
        }

        if (token is "~" || token.StartsWith("~/") || token.StartsWith("~\\"))
        {
            return true; // 家目录必然在工作区外
        }

        var isDrive = token.Length >= 2 && token[1] == ':' && char.IsAsciiLetter(token[0]);
        var isUnc = token.StartsWith(@"\\", StringComparison.Ordinal);
        var isPosixAbsolute = token.StartsWith('/') && token.IndexOf('/', 1) >= 0;
        if (!isDrive && !isUnc && !isPosixAbsolute)
        {
            return false;
        }

        try
        {
            return !_workspace.IsWithin(token);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return true; // 路径形态无法解析 = 可疑，宁可升级
        }
    }
}
