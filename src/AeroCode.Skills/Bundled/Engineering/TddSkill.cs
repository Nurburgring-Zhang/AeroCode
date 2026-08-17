// Copyright (c) AeroCode V3.0
// TddSkill — Test-Driven Development workflow (Matt Pocock + Kent Beck).
using AeroCode.Skills.Registry;

namespace AeroCode.Skills.Bundled.Engineering;

/// <summary>
/// TDD workflow (Matt Pocock + Kent Beck red-green-refactor).
/// </summary>
public sealed class TddSkill : ISkill
{
    public string Id => "engineering/tdd";
    public string Name => "TDD";
    public string Description => "Test-driven development: red, green, refactor.";
    public string Category => "engineering";
    public string Author => "Matt Pocock (skills), Kent Beck (TDD)";
    public string Version => "1.0.0";
    public IReadOnlyList<string> Tags => new[] { "tdd", "engineering", "testing" };

    public string GetSystemPrompt() => """
        # TDD Workflow
        For any new feature:
        1. RED — write a failing test first
        2. GREEN — write the minimal implementation to pass
        3. REFACTOR — improve the code while keeping tests green
        4. REPEAT for the next behavior
        Anti-patterns:
          - Writing implementation before the test
          - Writing multiple tests at once
          - Skipping the refactor phase
        """;

    public bool IsAvailable() => true;

    public Task<SkillResult> ExecuteAsync(SkillInput input, SkillContext ctx, CancellationToken ct = default)
    {
        var feature = input.Args.TryGetValue("feature", out var f) ? f as string : null;
        if (string.IsNullOrWhiteSpace(feature))
            return Task.FromResult(new SkillResult { Text = "Error: 'feature' argument is required", Success = false });

        var plan = $"""
            # TDD Plan for: {feature}

            ## Phase 1: RED (write failing test)
            1. Identify the smallest behavior to test
            2. Write a test that fails
            3. Run the test — confirm it fails for the RIGHT reason

            ## Phase 2: GREEN (minimal implementation)
            1. Write the smallest amount of code to make the test pass
            2. Resist the urge to add features not covered by tests
            3. Run the test — confirm it passes

            ## Phase 3: REFACTOR (improve code)
            1. Remove duplication
            2. Improve naming
            3. Extract helpers if needed
            4. Run all tests — confirm they still pass

            ## Phase 4: REPEAT
            Pick the next behavior and go back to Phase 1.
            """;

        return Task.FromResult(new SkillResult
        {
            Text = plan,
            Success = true,
            NextActions = new[] { "write-test", "implement", "refactor" },
        });
    }
}
