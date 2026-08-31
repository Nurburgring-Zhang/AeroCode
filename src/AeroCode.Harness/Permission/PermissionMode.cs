// Copyright (c) AeroCode
// PermissionMode — 会话级权限档位（对标 claude-code 5+2 模式 / letta mode.ts 四档）。
namespace AeroCode.Harness.Permission;

/// <summary>
/// 权限模式档位：在逐工具规则之上的一层整体裁决基线。
/// </summary>
public enum PermissionMode
{
    /// <summary>逐工具规则原样生效（read allow / write ask / shell ask+危险升级）。</summary>
    Default = 0,
    /// <summary>文件编辑类（write/edit/delete）自动放行；shell 与网络仍走原规则。</summary>
    AcceptEdits = 1,
    /// <summary>只读规划：只读工具放行，一切写/执行/网络动作类工具直接拒绝；write_plan 白名单放行。</summary>
    Plan = 2,
    /// <summary>跳过用户询问（Ask 基线放行）——但显式 Deny 与危险模式探测不受影响（不降级）。</summary>
    Bypass = 3,
}

/// <summary>
/// 档位裁决的纯函数变换：输入 (工具名, 规则默认决策) → 档位调整后的基线决策。
/// 约束（不可协商的不变量，与 PrudenceRank 一致）：
/// 1. 显式 Deny 永远优先，任何档位不得翻回（在 Check 中先于本变换短路）；
/// 2. 危险模式探测 Override 在变换之后仍然只升不降（Bypass 放行的是"Ask 基线"，不是"危险探测"）；
/// 3. Plan 档的只读白名单是硬边界，未知工具一律 Deny。
/// 纯函数可机检：全部档位×工具族矩阵可用表驱动测试钉死。
/// </summary>
public static class PermissionModeTransform
{
    /// <summary>Plan 档放行的只读工具白名单（与 CreateDefault 的 read-only 规则集对齐 + write_plan）。</summary>
    public static readonly IReadOnlySet<string> PlanReadOnlyTools = new HashSet<string>(StringComparer.Ordinal)
    {
        "read_file", "list_directory", "search_files", "grep_search",
        "web_search", "semantic_search", "web_extract", "write_plan",
        "list_notes", "get_note", "search_notes", "list_notebooks", "list_tags",
        "get_notes_by_tag", "list_skills", "git_status", "git_diff",
    };

    /// <summary>AcceptEdits 档自动放行的编辑类工具。</summary>
    public static readonly IReadOnlySet<string> EditTools = new HashSet<string>(StringComparer.Ordinal)
    {
        "write_file", "edit_file", "delete_file",
    };

    /// <summary>应用档位变换。</summary>
    public static PermissionDecision Apply(PermissionMode mode, string toolName, PermissionDecision ruleDefault)
    {
        switch (mode)
        {
            case PermissionMode.Default:
                return ruleDefault;
            case PermissionMode.AcceptEdits:
                // 编辑类放行；Ask 的其他基线不动（shell Ask 由用户/危险探测决定）。
                if (ruleDefault == PermissionDecision.Ask && EditTools.Contains(toolName))
                {
                    return PermissionDecision.Allow;
                }

                return ruleDefault;
            case PermissionMode.Plan:
                return PlanReadOnlyTools.Contains(toolName)
                    ? (ruleDefault == PermissionDecision.Deny ? PermissionDecision.Deny : PermissionDecision.Allow)
                    : PermissionDecision.Deny;
            case PermissionMode.Bypass:
                return ruleDefault == PermissionDecision.Ask ? PermissionDecision.Allow : ruleDefault;
            default:
                return ruleDefault;
        }
    }
}
