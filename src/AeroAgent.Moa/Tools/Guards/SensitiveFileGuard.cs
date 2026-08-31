// Copyright (c) AeroCode
// SensitiveFileGuard — 敏感文件默认拒（批次 B G3，builder-β）。.env 类零泄露。
// 规则按优先级：
//   1. AeroCode 自身配置（permissions.json / settings.json 文件名，或 AeroCode 数据目录
//      之下任意目标）→ 一律 Deny。force 不可升级——配置自保护是最高优先不变量；
//   2. .env 族凭据文件（WorkspaceContext.SensitiveFileNames + 扩展族，子目录命中、
//      大小写不敏感）→ Deny；args 带 force:true 时升级为 Ask（用户显式豁免才可过）；
//   3. run_shell 的 command 参数逐 token 做文件名匹配（cat .env 类零泄露；.env.example
//      等非凭据文件不命中）；
//   4. run_shell 的 command token 命中 AeroCode 配置文件名（permissions.json / settings.json /
//      hooks.json / jobs.json / appsettings.json，OrdinalIgnoreCase，整段文件名=词边界）→ Deny。
//      审计修复（Reviewer-S 2a）：命令文本是 %VAR% 展开前的字面，位置无法验证
//      （%LOCALAPPDATA%\AeroCode\hooks.json 不含盘符/UNC/~/POSIX 绝对形态，边界守卫弃权），
//      Bypass 档曾可静默改写 jobs.json/hooks.json = 持久化任意命令执行。配置写入必须走
//      显式用户操作，模型侧一律拒（非 Ask——不给弹窗放行的通道）。path 形参的既有
//      自保护（1 按名 + 按位置）保持不变：settings.json 等通用名在数据目录之外仍不按名误伤。
// 纯字符串/路径判定，不读文件内容、不执行命令，无副作用、线程安全。
using AeroAgent.Moa.Tools.Workspace;
using AeroCode.Harness.Permission;

namespace AeroAgent.Moa.Tools;

public sealed class SensitiveFileGuard : IToolGuard
{
    /// <summary>.env 族凭据文件名（与 <see cref="WorkspaceContext.SensitiveFileNames"/> 取并集使用）。</summary>
    public static readonly IReadOnlyList<string> EnvFileNames = new[]
    {
        ".env", ".env.local", ".env.development", ".env.test", ".env.staging", ".env.production",
    };

    /// <summary>AeroCode 配置文件名（按名全域拒绝；其余通用名如 settings.json 只按位置保护）。</summary>
    public static readonly IReadOnlyList<string> ConfigFileNames = new[]
    {
        "permissions.json",
    };

    /// <summary>
    /// AeroCode 配置文件名（run_shell 命令 token 命中即 Deny）。命令文本无法验证目标位置
    /// （%VAR% 展开前的字面、无盘符/UNC 形态的间接引用边界守卫不判定），故对这组
    /// AeroCode 专属配置名按名收紧：写配置必须走显式用户操作，模型侧一律拒。
    /// settings.json / appsettings.json 虽是通用名，但经 shell 命令读取/改写工作区外
    /// 同名文件的合法场景不成立，宁可误拒交用户手动完成。
    /// </summary>
    public static readonly IReadOnlyList<string> CommandConfigFileNames = new[]
    {
        "permissions.json", "settings.json", "hooks.json", "jobs.json", "appsettings.json",
    };

    private static readonly HashSet<string> EnvNameSet =
        new(EnvFileNames.Concat(WorkspaceContext.SensitiveFileNames), StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> CommandConfigNameSet =
        new(CommandConfigFileNames, StringComparer.OrdinalIgnoreCase);

    private readonly string? _aeroCodeDataDir;
    private readonly WorkspaceContext? _workspace;

    /// <param name="workspace">可空：用于把相对路径解析为绝对路径再做数据目录比对。</param>
    /// <param name="aeroCodeDataDir">
    /// AeroCode 数据目录；目录之下一律 Deny。null = 默认 %LOCALAPPDATA%\AeroCode
    /// （与 SettingsService 的真实落盘位置一致：settings.json / permissions.json 均在其下）。
    /// </param>
    public SensitiveFileGuard(WorkspaceContext? workspace = null, string? aeroCodeDataDir = null)
    {
        _workspace = workspace;
        _aeroCodeDataDir = aeroCodeDataDir is null
            ? NormalizePath(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AeroCode"))
            : NormalizePath(aeroCodeDataDir);
    }

    /// <inheritdoc />
    public string Name => "sensitive-file";

    /// <inheritdoc />
    public PermissionDecision? Check(string toolName, IReadOnlyDictionary<string, object?>? args)
    {
        if (args is null)
        {
            return null;
        }

        var force = IsTrue(args, "force");

        foreach (var kv in args)
        {
            if (kv.Value is not string s || string.IsNullOrWhiteSpace(s) || !ToolArgPaths.IsPathKey(kv.Key))
            {
                continue;
            }

            var verdict = JudgePath(s, force);
            if (verdict is not null)
            {
                return verdict;
            }
        }

        if (args.TryGetValue("command", out var cmdObj) && cmdObj is string command && !string.IsNullOrWhiteSpace(command))
        {
            foreach (var token in WorkspaceBoundaryGuard.TokenizeCommand(command))
            {
                var name = ToolArgPaths.FileNameOf(token);
                if (EnvNameSet.Contains(name) || CommandConfigNameSet.Contains(name))
                {
                    // 命令管道没有 force 语义：零泄露与配置自保护一律拒（Reviewer-S 2a 修复）
                    return PermissionDecision.Deny;
                }
            }
        }

        return null;
    }

    private PermissionDecision? JudgePath(string raw, bool force)
    {
        var name = ToolArgPaths.FileNameOf(raw);

        // 1) AeroCode 配置自保护：最高优先，force 不豁免。
        if (ConfigFileNames.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            return PermissionDecision.Deny;
        }

        if (_aeroCodeDataDir is { } dataDir && TryResolveAbsolute(raw) is { } absolute && IsUnder(absolute, dataDir))
        {
            return PermissionDecision.Deny;
        }

        // 2) .env 族：默认 Deny；force:true 升级为 Ask（用户显式豁免）。
        if (EnvNameSet.Contains(name))
        {
            return force ? PermissionDecision.Ask : PermissionDecision.Deny;
        }

        return null;
    }

    private static bool IsTrue(IReadOnlyDictionary<string, object?> args, string key) => args.TryGetValue(key, out var v) && v switch
    {
        bool b => b,
        string s => bool.TryParse(s, out var b) && b,
        _ => false,
    };

    /// <summary>把相对/绝对输入解析为绝对路径；无法解析返回 null。</summary>
    private string? TryResolveAbsolute(string raw)
    {
        var trimmed = raw.Trim().Trim('"', '\'');
        try
        {
            if (Path.IsPathRooted(trimmed))
            {
                return Path.GetFullPath(trimmed);
            }

            return _workspace is { } ws && ws.Resolve(trimmed) is { } resolved ? resolved : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string NormalizePath(string path) => Path.GetFullPath(path)
        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool IsUnder(string absolutePath, string dir) =>
        absolutePath.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(absolutePath, dir, StringComparison.OrdinalIgnoreCase);
}
