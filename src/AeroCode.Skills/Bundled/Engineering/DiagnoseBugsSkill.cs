// Copyright (c) AeroCode V3.0
// DiagnoseBugsSkill — systematic bug diagnosis (Matt Pocock + 5 Whys).
using AeroCode.Skills.Registry;

namespace AeroCode.Skills.Bundled.Engineering;

/// <summary>
/// Systematic bug diagnosis skill (Matt Pocock diagnosing-bugs).
/// Phases: collect evidence → hypothesize → bisect → minimal repro → 5 Whys → fix → regression test.
/// </summary>
public sealed class DiagnoseBugsSkill : ISkill
{
    public string Id => "engineering/diagnosing-bugs";
    public string Name => "Diagnose Bugs";
    public string Description => "Systematic bug diagnosis: 5 Whys + bisect.";
    public string Category => "engineering";
    public string Author => "Matt Pocock (skills)";
    public string Version => "1.0.0";
    public IReadOnlyList<string> Tags => new[] { "debug", "engineering", "diagnosis" };

    public string GetSystemPrompt() => """
        # Bug Diagnosis (Matt Pocock methodology)
        1. COLLECT EVIDENCE — error message, stack trace, logs, repro steps
        2. HYPOTHESIZE — list at least 3 possible root causes
        3. BISECT — use git bisect or systematic elimination
        4. MINIMAL REPRO — construct smallest failing test
        5. 5 WHYS — root cause analysis (ask "why" 5 times)
        6. FIX — implement + write regression test
        7. VERIFY — run all tests + repro steps
        """;

    public bool IsAvailable() => true;

    public Task<SkillResult> ExecuteAsync(SkillInput input, SkillContext ctx, CancellationToken ct = default)
    {
        var symptom = input.Args.TryGetValue("symptom", out var s) ? s as string : null;
        if (string.IsNullOrWhiteSpace(symptom))
            return Task.FromResult(new SkillResult { Text = "Error: 'symptom' argument is required", Success = false });

        var evidence = input.Args.TryGetValue("evidence", out var e) ? e as string : "(no evidence provided)";

        var plan = $"""
            # Bug Diagnosis Plan

            **Symptom**: {symptom}
            **Evidence**: {evidence}

            ## Phase 1: Evidence Collection
            - [ ] Capture full error message
            - [ ] Capture full stack trace
            - [ ] Capture relevant log lines (timestamp, severity)
            - [ ] Identify the user-visible repro steps
            - [ ] Check: does it reproduce? On every run? On a specific input?

            ## Phase 2: Hypotheses (list at least 3)
            - H1: (most likely)
            - H2: (alternative)
            - H3: (less likely but possible)
            - H4: (environmental — config, network, race condition)
            - H5: (data — bad input, null, boundary)

            ## Phase 3: Bisect
            - Use git bisect to find the commit that introduced the bug
            - Or: comment out code in 50% increments until bug disappears

            ## Phase 4: Minimal Repro
            - Construct the smallest possible failing test case
            - The repro should not depend on environment specifics

            ## Phase 5: 5 Whys
            - Why did the bug happen? → A
            - Why did A happen? → B
            - Why did B happen? → C
            - Why did C happen? → D
            - Why did D happen? → E (root cause)

            ## Phase 6: Fix
            - Implement the minimal fix
            - Add a regression test
            - Verify the original repro is fixed

            ## Phase 7: Verify
            - [ ] Run all existing tests
            - [ ] Run the new regression test
            - [ ] Re-run the original repro
            """;

        return Task.FromResult(new SkillResult
        {
            Text = plan,
            Success = true,
            NextActions = new[] { "collect-evidence", "hypothesize", "bisect", "fix" },
        });
    }
}
