// Copyright (c) AeroCode
// SkillSpecGate — ACS v2.3.0 技能规格校验门。
// 校验技能是否符合规格：主入口行数上限、必填节齐全、引用可达。
// 用于技能入库前的合规检查（gate_skill_spec 的 C# 对应）。
namespace AeroCode.Harness.Acs;

/// <summary>技能规格校验结果。</summary>
public sealed record AcsSkillSpecResult(bool Passed, IReadOnlyList<string> Problems);

/// <summary>
/// 技能规格校验器。
/// </summary>
public sealed class SkillSpecGate
{
    /// <summary>主入口（SKILL.md 正文）行数上限（ACS：主入口 ≤120 行，子技能 ≤160）。</summary>
    public const int MainEntryMaxLines = 120;

    /// <summary>必填节（frontmatter 必备字段）。</summary>
    public static readonly IReadOnlyList<string> RequiredSections = new[]
    {
        "name", "description",
    };

    /// <summary>
    /// 校验技能正文。mainEntryLines 为主入口行数，frontmatter 为 frontmatter 文本，
    /// referencedFiles 为声明引用的文件路径（校验可达性时逐一检查存在）。
    /// </summary>
    public AcsSkillSpecResult Validate(
        int mainEntryLines,
        string frontmatter,
        IReadOnlyList<string>? referencedFiles = null,
        Func<string, bool>? fileExists = null)
    {
        var problems = new List<string>();

        // 主入口行数上限
        if (mainEntryLines > MainEntryMaxLines)
        {
            problems.Add($"主入口 {mainEntryLines} 行 > 上限 {MainEntryMaxLines}（主入口须精简，超出移入 reference）");
        }

        // 必填节齐全
        foreach (var section in RequiredSections)
        {
            if (string.IsNullOrWhiteSpace(frontmatter) ||
                frontmatter.IndexOf(section + ":", StringComparison.OrdinalIgnoreCase) < 0)
            {
                problems.Add($"frontmatter 缺必填节：{section}");
            }
        }

        // 引用可达
        if (referencedFiles is not null && fileExists is not null)
        {
            foreach (var f in referencedFiles)
            {
                if (!fileExists(f))
                {
                    problems.Add($"引用文件不可达：{f}");
                }
            }
        }

        return new AcsSkillSpecResult(problems.Count == 0, problems);
    }
}
