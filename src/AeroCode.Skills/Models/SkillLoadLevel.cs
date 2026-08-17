// Copyright (c) AeroCode V3.0
// Skill load level — three-tier progressive disclosure (Hermes pattern).
namespace AeroCode.Skills.Models;

/// <summary>
/// Three-tier progressive skill loading (Hermes pattern).
/// Minimizes token consumption by only loading full content when needed.
/// </summary>
public enum SkillLoadLevel
{
    /// <summary>List of (name, description) pairs only. ~3K tokens for hundreds of skills.</summary>
    List = 0,

    /// <summary>Full skill content + metadata. Loaded when skill is matched.</summary>
    Full = 1,

    /// <summary>Specific reference file within a skill. Loaded on demand.</summary>
    Reference = 2,
}
