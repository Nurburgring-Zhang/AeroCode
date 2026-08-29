// Copyright (c) AeroCode V3.0
// Permission system — OpenCode + DSH guard fusion.
using System.Text.RegularExpressions;
using AeroCode.Harness.EventBus;

namespace AeroCode.Harness.Permission;

/// <summary>Permission decision for a tool call.</summary>
public enum PermissionDecision
{
    /// <summary>Allow without asking.</summary>
    Allow,
    /// <summary>Deny without asking.</summary>
    Deny,
    /// <summary>Ask the user for confirmation (XAML dialog).</summary>
    Ask,
}

/// <summary>Result of a permission check, with optional reason.</summary>
public sealed record PermissionResult(PermissionDecision Decision, string? Reason = null);

/// <summary>
/// A permission policy for a single tool.
/// OpenCode pattern: tool name -> (default decision, dangerous-pattern detector).
/// </summary>
public sealed class ToolPermissionRule
{
    public required string ToolName { get; init; }
    public PermissionDecision DefaultDecision { get; set; } = PermissionDecision.Ask;
    public Func<IReadOnlyDictionary<string, object?>?, PermissionDecision>? Override { get; set; }
    public string? Notes { get; init; }
}

/// <summary>
/// Permission policy store (OpenCode + DSH guard fusion).
/// Holds per-tool rules + a user-override callback for "Ask" decisions.
/// 线程安全：MOA 并行 worker 会并发 Check，UI 线程会并发写规则。
/// </summary>
public sealed class PermissionPolicy
{
    private readonly object _sync = new();
    private readonly Dictionary<string, ToolPermissionRule> _rules = new();
    private readonly EventBus.EventBus _eventBus;

    public PermissionPolicy(EventBus.EventBus eventBus)
    {
        _eventBus = eventBus;
    }

    /// <summary>Register a default rule for a tool.</summary>
    public void SetRule(ToolPermissionRule rule)
    {
        lock (_sync)
        {
            _rules[rule.ToolName] = rule;
        }
    }

    /// <summary>
    /// 用户/持久化层对某工具的默认决策覆盖：规则已存在则改判定，不存在则新建。
    /// 注意 Override（危险模式探测）保留不动——它只会在默认决策之上升级审慎度。
    /// </summary>
    public void SetDefaultDecision(string toolName, PermissionDecision decision)
    {
        lock (_sync)
        {
            if (_rules.TryGetValue(toolName, out var rule))
            {
                rule.DefaultDecision = decision;
            }
            else
            {
                _rules[toolName] = new ToolPermissionRule
                {
                    ToolName = toolName,
                    DefaultDecision = decision,
                    Notes = "user decision",
                };
            }
        }
    }

    /// <summary>列出全部规则（权限管理 UI 用）。返回快照。</summary>
    public IReadOnlyList<ToolPermissionRule> ListRules()
    {
        lock (_sync)
        {
            return _rules.Values
                .OrderBy(r => r.ToolName, StringComparer.Ordinal)
                .ToList();
        }
    }

    /// <summary>Check permission for a tool call.</summary>
    public PermissionResult Check(string toolName, IReadOnlyDictionary<string, object?>? args = null)
    {
        ToolPermissionRule? rule;
        lock (_sync)
        {
            _rules.TryGetValue(toolName, out rule);
        }

        if (rule is null)
        {
            // Unknown tool: ask by default (safe).
            return new PermissionResult(PermissionDecision.Ask, $"Unknown tool '{toolName}'");
        }

        // 显式 Deny 优先：用户/系统明确拒绝的工具不得被 Override（模式探测）翻成放行。
        if (rule.DefaultDecision == PermissionDecision.Deny)
        {
            return new PermissionResult(PermissionDecision.Deny, "Explicitly denied");
        }

        if (rule.Override is not null)
        {
            var d = rule.Override(args);
            // Override 只允许升级审慎度（Allow→Ask→Deny），绝不降级：
            // 用户把默认决策设为 Ask 后，模式探测不得把它悄悄放行为 Allow。
            if (PrudenceRank(d) > PrudenceRank(rule.DefaultDecision))
                return new PermissionResult(d, "Rule override");
        }

        return new PermissionResult(rule.DefaultDecision);
    }

    /// <summary>审慎度阶梯：Allow(0) &lt; Ask(1) &lt; Deny(2)。Override 只许向上走。</summary>
    private static int PrudenceRank(PermissionDecision d) => d switch
    {
        PermissionDecision.Allow => 0,
        PermissionDecision.Ask => 1,
        _ => 2,
    };

    /// <summary>
    /// run_shell 危险模式探测：先规范化（剥离引号/反引号混淆、压缩空白）再匹配破坏性命令清单。
    /// 命中即升级为 Ask——Override 语义只升不降（见 Check）。正则带 1 秒匹配超时，
    /// 超时按可疑处理（宁升不降）。公开以便测试直接钉住探测边界。
    /// </summary>
    public static bool ShellCommandLooksDangerous(string command)
    {
        // 剥掉单双引号与反引号，防 r''m / r"m" / r`m` 之类的拆字混淆；再压缩空白。
        var normalized = ObfuscationRx.Replace(command, string.Empty);
        normalized = WhitespaceRx.Replace(normalized, " ");
        foreach (var rx in DangerousShellPatterns)
        {
            try
            {
                if (rx.IsMatch(normalized)) return true;
            }
            catch (RegexMatchTimeoutException)
            {
                return true; // 匹配超时按可疑处理——宁升不降。
            }
        }
        return false;
    }

    private static readonly Regex ObfuscationRx = new("['\"`]", RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    private static readonly Regex WhitespaceRx = new(@"\s+", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    /// <summary>破坏性/提权/外泄类命令清单（全部 IgnoreCase + 1 秒超时）。</summary>
    private static readonly Regex[] DangerousShellPatterns =
    {
        // rm 家族：任意顺序的 -r/-f/-R/-F 组合、--recursive/--force、以及裸 rm
        new(@"\brm\s+(?:-[a-z]*\s+)*-[a-z]*[rf]", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1)),
        new(@"\brm\s+--(recursive|force)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1)),
        new(@"\brmdir\b|\bshred\b|\brm\b", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1)),
        // Windows 删除/格式化家族
        new(@"\b(del|erase|rd|remove-item|format|diskpart)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1)),
        // 提权
        new(@"\b(sudo|doas|pkexec|runas|gsudo)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1)),
        // 权限放开
        new(@"\bchmod\s+(-[a-z]+\s+)*[0-7]*777\b|\bicacls\b.*(/grant|/reset)|\btakeown\b", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1)),
        // 编码/间接执行
        new(@"\bpowershell(\.exe)?\s+[^|;&]*-enc\w*\b|\bcmd(\.exe)?\s+/[crv]\b|\binvoke-expression\b|\biex\s*\(", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1)),
        // 管道喂 shell（curl|sh 等）
        new(@"\b(curl|wget|invoke-webrequest|iwr)\b[^|;&]*\|\s*(sh|bash|zsh|dash|python3?|node|powershell)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1)),
        // 磁盘/设备级破坏
        new(@"\bdd\s+if=|\bmkfs|\bfdisk\b|\bparted\b|>\s*/dev/(sd|nvme|hd|disk|xvd)", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1)),
        // 关键系统文件写
        new(@"[>|]\s*/etc/(passwd|shadow|sudoers)", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1)),
        // 注册表改写
        new(@"\breg\s+(delete|add)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1)),
        // 强制推送（历史不可恢复）
        new(@"\bgit\s+push\s+[^|;&]*--force\b", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1)),
        // 关机/重启
        new(@"\b(shutdown|reboot|halt|poweroff)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1)),
    };

    /// <summary>Default safe policy matching the documented table in V3_INTEGRATION_PLAN.md §3.4.</summary>
    public static PermissionPolicy CreateDefault(EventBus.EventBus bus)
    {
        var p = new PermissionPolicy(bus);

        // read-only: allow
        p.SetRule(new ToolPermissionRule { ToolName = "read_file", DefaultDecision = PermissionDecision.Allow, Notes = "Read-only" });
        p.SetRule(new ToolPermissionRule { ToolName = "list_directory", DefaultDecision = PermissionDecision.Allow, Notes = "Read-only" });
        p.SetRule(new ToolPermissionRule { ToolName = "search_files", DefaultDecision = PermissionDecision.Allow, Notes = "Read-only" });
        p.SetRule(new ToolPermissionRule { ToolName = "grep_search", DefaultDecision = PermissionDecision.Allow, Notes = "Read-only" });
        p.SetRule(new ToolPermissionRule { ToolName = "web_search", DefaultDecision = PermissionDecision.Allow, Notes = "Read-only" });
        p.SetRule(new ToolPermissionRule { ToolName = "semantic_search", DefaultDecision = PermissionDecision.Allow, Notes = "Read-only" });

        // write: ask by default
        p.SetRule(new ToolPermissionRule { ToolName = "write_file", DefaultDecision = PermissionDecision.Ask, Notes = "Modifies disk" });
        p.SetRule(new ToolPermissionRule { ToolName = "edit_file", DefaultDecision = PermissionDecision.Ask, Notes = "Modifies disk" });
        p.SetRule(new ToolPermissionRule { ToolName = "delete_file", DefaultDecision = PermissionDecision.Ask, Notes = "Removes from disk" });

        // shell: pattern-based
        // 注意：应用当前未注册任何 run_shell 执行器（休眠规则）——默认 Allow 沿用
        // 文档化契约（V3_INTEGRATION_PLAN §3.4 安全命令放行）；探测器保持强化状态，
        // 执行器一旦接线立即受保护。接线执行器前应重新评估默认裁决（建议 Ask）。
        p.SetRule(new ToolPermissionRule
        {
            ToolName = "run_shell",
            DefaultDecision = PermissionDecision.Allow,
            Notes = "Safe shell (dormant: no executor wired); dangerous patterns always escalate to Ask",
            Override = args =>
            {
                if (args is null) return PermissionDecision.Ask;
                var cmd = args.TryGetValue("command", out var c) ? c as string : null;
                if (string.IsNullOrWhiteSpace(cmd)) return PermissionDecision.Ask;
                // dangerous patterns -> ask
                if (ShellCommandLooksDangerous(cmd)) return PermissionDecision.Ask;
                return PermissionDecision.Allow;
            }
        });

        // web: ask
        p.SetRule(new ToolPermissionRule { ToolName = "web_browser", DefaultDecision = PermissionDecision.Ask, Notes = "External network" });
        p.SetRule(new ToolPermissionRule { ToolName = "web_extract", DefaultDecision = PermissionDecision.Allow, Notes = "Read-only HTTP" });

        // git: ask for push
        p.SetRule(new ToolPermissionRule { ToolName = "git_push", DefaultDecision = PermissionDecision.Ask, Notes = "Side effect on remote" });
        p.SetRule(new ToolPermissionRule { ToolName = "git_commit", DefaultDecision = PermissionDecision.Ask, Notes = "Side effect on local repo" });
        p.SetRule(new ToolPermissionRule { ToolName = "git_status", DefaultDecision = PermissionDecision.Allow, Notes = "Read-only" });
        p.SetRule(new ToolPermissionRule { ToolName = "git_diff", DefaultDecision = PermissionDecision.Allow, Notes = "Read-only" });

        return p;
    }
}
