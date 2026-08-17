// Copyright (c) AeroCode V3.0
// Permission system — OpenCode + DSH guard fusion.
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
/// </summary>
public sealed class PermissionPolicy
{
    private readonly Dictionary<string, ToolPermissionRule> _rules = new();
    private readonly EventBus.EventBus _eventBus;

    public PermissionPolicy(EventBus.EventBus eventBus)
    {
        _eventBus = eventBus;
    }

    /// <summary>Register a default rule for a tool.</summary>
    public void SetRule(ToolPermissionRule rule)
    {
        _rules[rule.ToolName] = rule;
    }

    /// <summary>Check permission for a tool call.</summary>
    public PermissionResult Check(string toolName, IReadOnlyDictionary<string, object?>? args = null)
    {
        if (!_rules.TryGetValue(toolName, out var rule))
        {
            // Unknown tool: ask by default (safe).
            return new PermissionResult(PermissionDecision.Ask, $"Unknown tool '{toolName}'");
        }

        if (rule.Override is not null)
        {
            var d = rule.Override(args);
            if (d != rule.DefaultDecision)
                return new PermissionResult(d, "Rule override");
        }

        return new PermissionResult(rule.DefaultDecision);
    }

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
        p.SetRule(new ToolPermissionRule
        {
            ToolName = "run_shell",
            DefaultDecision = PermissionDecision.Allow,
            Notes = "Safe shell",
            Override = args =>
            {
                if (args is null) return PermissionDecision.Allow;
                var cmd = args.TryGetValue("command", out var c) ? c as string : null;
                if (string.IsNullOrEmpty(cmd)) return PermissionDecision.Ask;
                // dangerous patterns -> ask
                if (System.Text.RegularExpressions.Regex.IsMatch(cmd, @"\b(rm\s+-rf|sudo|format|dd\s+if=|mkfs|chmod\s+777)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    return PermissionDecision.Ask;
                if (System.Text.RegularExpressions.Regex.IsMatch(cmd, @"\b(rm|del|rd|Remove-Item)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    return PermissionDecision.Ask;
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
