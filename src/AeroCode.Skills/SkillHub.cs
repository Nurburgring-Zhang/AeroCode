// Copyright (c) AeroCode V3.0
// SkillHub — main entry point for the Skills engine (Hermes-style hub).
using AeroCode.Skills.AutoCreate;
using AeroCode.Skills.Bundled.Analysis;
using AeroCode.Skills.Bundled.Engineering;
using AeroCode.Skills.Bundled.Productivity;
using AeroCode.Skills.Loader;
using AeroCode.Skills.Registry;

namespace AeroCode.Skills;

/// <summary>
/// Main entry point for the Skills engine.
/// Mirrors Hermes Skills Hub + Skill auto-creation + self-patch.
/// </summary>
public sealed class SkillHub
{
    public SkillRegistry Registry { get; }
    public SkillLoader Loader { get; }
    public SkillCreator Creator { get; }
    public SkillPatcher Patcher { get; }

    public string UserSkillsRoot { get; }
    public string BundledSkillsRoot { get; }

    public SkillHub(string userSkillsRoot, string? bundledSkillsRoot = null)
    {
        UserSkillsRoot = userSkillsRoot;
        // User skills live under <userSkillsRoot>/skills/ so that DeriveId finds the
        // "skills" ancestor and returns the correct hierarchical id.
        var userSkillsTree = Path.Combine(userSkillsRoot, "skills");
        BundledSkillsRoot = bundledSkillsRoot ?? Path.Combine(userSkillsRoot, "bundled");
        Directory.CreateDirectory(UserSkillsRoot);
        Directory.CreateDirectory(userSkillsTree);
        Directory.CreateDirectory(BundledSkillsRoot);

        Registry = new SkillRegistry();
        Loader = new SkillLoader(Registry);
        // Creator writes to <userSkillsRoot>/skills/<id>/SKILL.md
        Creator = new SkillCreator(Registry, userSkillsRoot);
        Patcher = new SkillPatcher(Loader, Registry);

        RegisterBundledSkills();
    }

    /// <summary>Register the 8 default bundled skills (5 engineering + 2 productivity + 1 analysis).</summary>
    private void RegisterBundledSkills()
    {
        TryRegister(() => new CodeReviewSkill());
        TryRegister(() => new TddSkill());
        TryRegister(() => new DiagnoseBugsSkill());
        TryRegister(() => new GrillWithDocsSkill());
        TryRegister(() => new SetupSkillsSkill());
        TryRegister(() => new SummarizeNoteSkill());
        TryRegister(() => new AutoTagNoteSkill());
        // DeepAuditSkill consumes SkillContext.LlmInvoker (real provider via SkillToolbox);
        // falls back to a static-only audit when no LLM is wired — never fake output.
        TryRegister(() => new DeepAuditSkill());
    }

    private void TryRegister(Func<ISkill> factory)
    {
        try
        {
            Registry.Register(factory());
        }
        catch
        {
            // Idempotent: skip if duplicate.
        }
    }

    /// <summary>Load user/hub skills from disk into the registry cache.</summary>
    public int LoadFromDisk()
    {
        if (Directory.Exists(UserSkillsRoot))
            Loader.LoadFromDirectory(UserSkillsRoot, "user");
        if (Directory.Exists(BundledSkillsRoot))
            Loader.LoadFromDirectory(BundledSkillsRoot, "bundled");
        return Loader.CachedSkillCount;
    }

    /// <summary>Build the system prompt fragment listing all available skills (Hermes pattern).</summary>
    public string BuildSystemPromptFragment(string? category = null)
        => Loader.BuildSystemPromptFragment(category);

    /// <summary>Get a skill by id, with full body loaded.</summary>
    public ISkill? Get(string id) => Registry.Get(id);

    /// <summary>List all skills, optionally filtered.</summary>
    public IReadOnlyList<ISkill> List(string? category = null, string? tag = null)
        => Registry.List(category, tag);
}
