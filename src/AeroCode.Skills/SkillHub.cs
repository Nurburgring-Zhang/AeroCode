// Copyright (c) AeroCode V3.0 / V3.3
// SkillHub — main entry point for the Skills engine (Hermes-style hub).
// V3.3 (PHASE 5): registers the full bundled catalog incl. research skills,
// logs registration failures instead of swallowing them, and wires the
// invoke→record→auto-patch learning loop through real public APIs.
using AeroCode.Skills.AutoCreate;
using AeroCode.Skills.Bundled.Analysis;
using AeroCode.Skills.Bundled.Engineering;
using AeroCode.Skills.Bundled.Productivity;
using AeroCode.Skills.Bundled.Research;
using AeroCode.Skills.Loader;
using AeroCode.Skills.Models;
using AeroCode.Skills.Registry;
using AeroCode.Skills.Research;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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

    private readonly ILogger _logger;
    private readonly SearchService _searchService;

    public SkillHub(string userSkillsRoot, string? bundledSkillsRoot = null, ILogger? logger = null, SearchService? searchService = null)
    {
        _logger = logger ?? NullLogger.Instance;
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
        _searchService = searchService ?? SearchService.CreateDefault(_logger);

        RegisterBundledSkills();
    }

    /// <summary>
    /// Register the 13 default bundled skills:
    /// 5 engineering + 2 productivity + 1 deep-audit analysis + 4 research
    /// (web research / browser / embedding / Roslyn) + 1 acquire-deploy.
    /// </summary>
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
        // Research catalog (PHASE 5): all real implementations, previously never registered.
        TryRegister(() => new WebResearchSkill(_searchService));
        TryRegister(() => new BrowserSkill());
        TryRegister(() => new EmbeddingSkill());
        TryRegister(() => new RoslynAnalyzerSkill());
        TryRegister(() => new AcquireDeploySkill());
    }

    private void TryRegister(Func<ISkill> factory)
    {
        try
        {
            Registry.Register(factory());
        }
        catch (Exception ex)
        {
            // Idempotent on duplicates, but never silent: the reason is always logged.
            _logger.LogWarning("技能注册跳过: {Reason}", ex.Message);
        }
    }

    /// <summary>
    /// Execute a registered skill and automatically record the outcome into the
    /// invoke→record→auto-patch learning loop (real wiring of SkillPatcher).
    /// </summary>
    /// <param name="skillId">Registered skill id.</param>
    /// <param name="input">Skill input.</param>
    /// <param name="ctx">Execution context.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<SkillResult> InvokeAsync(string skillId, SkillInput input, SkillContext ctx, CancellationToken ct = default)
    {
        var skill = Registry.Get(skillId);
        if (skill is null)
            return new SkillResult { Success = false, Text = $"Skill not found: {skillId}" };

        SkillResult result;
        try
        {
            result = await skill.ExecuteAsync(input, ctx, ct);
        }
        catch (Exception ex)
        {
            result = new SkillResult { Success = false, Text = $"{ex.GetType().Name}: {ex.Message}" };
        }

        ReportInvocation(skillId, result.Success ? null : (string.IsNullOrWhiteSpace(result.Text) ? "unknown error" : TruncateForRecord(result.Text)));
        return result;
    }

    /// <summary>
    /// Record an invocation outcome and trigger the self-patch policy (real SkillPatcher call path).
    /// </summary>
    /// <param name="skillId">Skill that ran.</param>
    /// <param name="errorMessage">Null/empty = success.</param>
    /// <returns>True when a patch was applied to the skill definition.</returns>
    public bool ReportInvocation(string skillId, string? errorMessage)
        => Patcher.RecordFailureAndMaybePatch(skillId, errorMessage);

    /// <summary>
    /// Offer a completed task to the auto-create policy (real SkillCreator call path).
    /// Returns the created skill, or null when the trigger conditions are not met.
    /// </summary>
    public Skill? TryAutoCreateSkill(AutoCreateCandidate candidate)
        => Creator.TryCreate(candidate);

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

    private static string TruncateForRecord(string s)
        => s.Length <= 300 ? s : s[..300];
}
