// Copyright (c) AeroCode V3.0
// SetupSkillsSkill — initialize the engineering discipline for a project (Matt Pocock setup).
using AeroCode.Skills.Registry;

namespace AeroCode.Skills.Bundled.Engineering;

/// <summary>
/// Initialize engineering discipline (Matt Pocock setup-mattpocock-skills).
/// Injects engineering rules into the project's system prompt.
/// </summary>
public sealed class SetupSkillsSkill : ISkill
{
    public string Id => "engineering/setup-skills";
    public string Name => "Setup Skills";
    public string Description => "Initialize engineering discipline for a project.";
    public string Category => "engineering";
    public string Author => "Matt Pocock (skills), AeroCode V3.0";
    public string Version => "1.0.0";
    public IReadOnlyList<string> Tags => new[] { "setup", "engineering", "initialization" };

    public string GetSystemPrompt() => """
        # Setup Skills (Engineering Discipline)
        When invoked, register all engineering skills and inject the discipline rules:
        - Type safety is non-negotiable (no any, handle null)
        - Test first, code second (TDD)
        - Design before implement (write spec first)
        - Verify with documentation (grill-with-docs after every implementation)
        - Self-review before commit (code-review)
        - Diagnose systematically (use the diagnosing-bugs workflow)
        - Domain language matters (use business domain names)
        - Architecture is iterative (continuously improve)
        """;

    public bool IsAvailable() => true;

    public Task<SkillResult> ExecuteAsync(SkillInput input, SkillContext ctx, CancellationToken ct = default)
    {
        var projectType = input.Args.TryGetValue("project_type", out var p) ? p as string : "unknown";
        var rules = $"""
            # Engineering Discipline Rules (AeroCode V3.0)

            Project type: {projectType}

            ## Type Safety
            - No `any` (in TypeScript) or untyped `object` (in C#)
            - All nullable references must be explicitly marked
            - All public methods must have explicit return types

            ## Test-First
            - Every new feature starts with a failing test
            - No code lands without a corresponding test

            ## Design-Before-Implement
            - Every non-trivial change starts with a design doc / spec
            - The spec lists: problem, approach, alternatives, trade-offs

            ## Documentation Verification
            - After any code change, run grill-with-docs
            - Cite official documentation URL in code comments

            ## Self-Review
            - Before every commit, run code-review (8 dimensions)
            - Resolve all Blocker and Major issues

            ## Systematic Diagnosis
            - For any bug, use the diagnosing-bugs workflow
            - 5 Whys before fixing
            - Regression test for every fix

            ## Domain Language
            - Variable, class, method names reflect the business domain
            - Avoid generic names like `Manager`, `Helper`, `Util`

            ## Iterative Architecture
            - Continuously improve code structure
            - Refactor as part of every feature
            """;

        return Task.FromResult(new SkillResult
        {
            Text = rules,
            Success = true,
            Data = new { ProjectType = projectType, RulesInjected = true },
            NextActions = new[] { "inject-into-prompt", "save-to-disk", "share-with-team" },
        });
    }
}
