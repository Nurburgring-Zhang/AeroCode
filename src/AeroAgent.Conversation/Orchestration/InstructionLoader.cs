// Copyright (c) AeroCode
// InstructionLoader — 项目指令文件自动加载（对标 opencode instruction.ts / codex agents_md / claude-code CLAUDE.md）。
// 发现序：全局（AppData/AeroCode/AGENTS.md）→ 项目级（工作区根 AGENTS.md，缺省回退 CLAUDE.md）。
// 合并顺序：全局在前、项目在后，段间加分隔标注；两个文件都缺时返回空串（不注入）。
namespace AeroAgent.Conversation.Orchestration;

/// <summary>
/// AGENTS.md / CLAUDE.md 指令装载器。文件内容是"用户写给 agent 的持久指令"，
/// 原样注入 system 上下文——本类不做任何语义改写（诚实转发）。
/// </summary>
public sealed class InstructionLoader
{
    /// <summary>全局指令文件名。</summary>
    public const string GlobalFileName = "AGENTS.md";
    /// <summary>项目级主文件名。</summary>
    public const string ProjectFileName = "AGENTS.md";
    /// <summary>项目级回退文件名（claude-code 生态兼容）。</summary>
    public const string ProjectFallbackFileName = "CLAUDE.md";

    private readonly string _globalPath;
    private readonly string _projectPrimaryPath;
    private readonly string _projectFallbackPath;

    /// <param name="appDataDir">AeroCode 应用数据目录（%LOCALAPPDATA%/AeroCode）。</param>
    /// <param name="workspaceRoot">工作区根（可为 null：无工作区场景只加载全局）。</param>
    public InstructionLoader(string appDataDir, string? workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(appDataDir))
        {
            throw new ArgumentException("app data dir must not be empty", nameof(appDataDir));
        }

        _globalPath = Path.Combine(appDataDir, GlobalFileName);
        _projectPrimaryPath = string.IsNullOrWhiteSpace(workspaceRoot)
            ? string.Empty
            : Path.Combine(workspaceRoot, ProjectFileName);
        _projectFallbackPath = string.IsNullOrWhiteSpace(workspaceRoot)
            ? string.Empty
            : Path.Combine(workspaceRoot, ProjectFallbackFileName);
    }

    /// <summary>是否存在任何可注入的指令文件。</summary>
    public bool HasAny =>
        File.Exists(_globalPath)
        || (!string.IsNullOrEmpty(_projectPrimaryPath) && File.Exists(_projectPrimaryPath))
        || (!string.IsNullOrEmpty(_projectFallbackPath) && File.Exists(_projectFallbackPath));

    /// <summary>返回合并后的指令文本（无文件=空串）；各段以标注分隔，来源可追溯。</summary>
    public string Load()
    {
        var sb = new System.Text.StringBuilder();

        AppendSection(sb, "global instructions", _globalPath);

        if (!string.IsNullOrEmpty(_projectPrimaryPath) && File.Exists(_projectPrimaryPath))
        {
            AppendSection(sb, $"project instructions ({ProjectFileName})", _projectPrimaryPath);
        }
        else if (!string.IsNullOrEmpty(_projectFallbackPath) && File.Exists(_projectFallbackPath))
        {
            AppendSection(sb, $"project instructions ({ProjectFallbackFileName})", _projectFallbackPath);
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>项目级生效文件路径（无则 null；诊断/测试用）。</summary>
    public string? EffectiveProjectFile()
    {
        if (!string.IsNullOrEmpty(_projectPrimaryPath) && File.Exists(_projectPrimaryPath))
        {
            return _projectPrimaryPath;
        }

        if (!string.IsNullOrEmpty(_projectFallbackPath) && File.Exists(_projectFallbackPath))
        {
            return _projectFallbackPath;
        }

        return null;
    }

    private static void AppendSection(System.Text.StringBuilder sb, string label, string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        var content = File.ReadAllText(path).Trim();
        if (content.Length == 0)
        {
            return;
        }

        if (sb.Length > 0)
        {
            sb.AppendLine().AppendLine();
        }

        sb.AppendLine($"<instructions source=\"{label}\">").Append(content).Append("</instructions>");
    }
}

/// <summary>
/// @文件 引用解析与扩展（对标 opencode @file / qoder @引用）。
/// 记号形态：@ 后跟非空白路径（可含 ./、子目录、扩展名），行尾标点（.,;:!?) 不属于路径。
/// 扩展：每个存在的 @path 追加 file 块（内容原样）；不存在的引用在尾部如实列出，不静默丢弃。
/// </summary>
public static class AtReference
{
    /// <summary>解析消息中的 @path 记号（去重保序）。纯函数。</summary>
    public static IReadOnlyList<string> Extract(string text)
    {
        var refs = new List<string>();
        if (string.IsNullOrEmpty(text))
        {
            return refs;
        }

        var spans = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in spans)
        {
            if (raw.Length < 2 || raw[0] != '@')
            {
                continue;
            }

            var token = raw[1..].TrimEnd('.', ',', ';', ':', '!', '?', ')');
            if (token.Length == 0 || token.Contains('@'))
            {
                continue;
            }

            if (refs.Count == 0 || !string.Equals(refs[^1], token, StringComparison.Ordinal))
            {
                // 仅去相邻重复；同一文件在消息不同位置出现属合法（保序）。
                refs.Add(token);
            }
        }

        return refs;
    }

    /// <summary>
    /// 扩展消息：每个 <paramref name="readFile"/> 返回内容的 @path 在原文尾部追加 file 块。
    /// readFile 返回 null = 不存在/不可读（列入未解析清单）。
    /// </summary>
    public static string Expand(string text, Func<string, string?> readFile)
    {
        ArgumentNullException.ThrowIfNull(readFile);
        var refs = Extract(text);
        if (refs.Count == 0)
        {
            return text;
        }

        var sb = new System.Text.StringBuilder(text);
        var unresolved = new List<string>();
        foreach (var r in refs)
        {
            var content = readFile(r);
            if (content is null)
            {
                unresolved.Add(r);
                continue;
            }

            sb.AppendLine().AppendLine()
                .Append("<file path=\"").Append(r).Append("\">").AppendLine()
                .Append(content)
                .AppendLine("</file>");
        }

        if (unresolved.Count > 0)
        {
            sb.AppendLine().Append("[aerocode] @references not found: ").Append(string.Join(", ", unresolved));
        }

        return sb.ToString();
    }
}
