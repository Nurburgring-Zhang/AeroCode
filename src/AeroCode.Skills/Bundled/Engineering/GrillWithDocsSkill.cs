// Copyright (c) AeroCode V3.0
// GrillWithDocsSkill — challenge implementation against official documentation.
using System.Text.RegularExpressions;
using AeroCode.Skills.Registry;

namespace AeroCode.Skills.Bundled.Engineering;

/// <summary>
/// Grill the implementation with official documentation (Matt Pocock grill-with-docs).
/// Identifies API misuse, deprecated calls, parameter order errors.
/// </summary>
public sealed class GrillWithDocsSkill : ISkill
{
    public string Id => "engineering/grill-with-docs";
    public string Name => "Grill With Docs";
    public string Description => "Verify implementation against official docs.";
    public string Category => "engineering";
    public string Author => "Matt Pocock (skills)";
    public string Version => "1.0.0";
    public IReadOnlyList<string> Tags => new[] { "documentation", "engineering", "verification" };

    public string GetSystemPrompt() => """
        # Grill With Docs
        For any code that uses a third-party API:
        1. Extract every API call from the code
        2. Look up each API in the official documentation
        3. Compare implementation against docs:
           - Is the API name correct?
           - Is the parameter order correct?
           - Are the parameter types correct?
           - Is the method deprecated? (prefer the new version)
           - Is there a better alternative?
        4. Report any discrepancies.
        """;

    public bool IsAvailable() => true;

    public Task<SkillResult> ExecuteAsync(SkillInput input, SkillContext ctx, CancellationToken ct = default)
    {
        var code = input.Args.TryGetValue("code", out var c) ? c as string : null;
        if (string.IsNullOrWhiteSpace(code))
            return Task.FromResult(new SkillResult { Text = "Error: 'code' argument is required", Success = false });

        // Heuristically extract what look like API calls (capitalized method names).
        var apiCalls = ExtractApiCalls(code);

        // Suggest checks.
        var checks = new List<string>();
        foreach (var call in apiCalls)
        {
            checks.Add($"- [ ] Verify `{call}` in official documentation");
        }

        if (checks.Count == 0)
            checks.Add("No obvious API calls detected. Code may be self-contained or this skill is not applicable.");

        // Heuristic: flag obviously suspicious patterns.
        var suspicious = new List<string>();
        if (code.Contains(".Result ", StringComparison.Ordinal) || code.Contains(".Wait()", StringComparison.Ordinal))
            suspicious.Add("Async-over-sync pattern detected. Use `await` instead of `.Result`/`.Wait()` (deadlock risk).");
        if (code.Contains("Thread.Sleep", StringComparison.Ordinal))
            suspicious.Add("`Thread.Sleep` in async code. Use `await Task.Delay` instead.");
        if (Regex.IsMatch(code, @"new\s+HttpClient\s*\(\s*\)"))
            suspicious.Add("Creating HttpClient directly. Use IHttpClientFactory for proper socket lifetime management.");
        if (code.Contains("DateTime.Now", StringComparison.Ordinal))
            suspicious.Add("`DateTime.Now` is local time. Use `DateTime.UtcNow` for storage/comparison.");
        if (code.Contains("== null", StringComparison.Ordinal) && code.Contains("string", StringComparison.Ordinal))
            suspicious.Add("`==` for null-check is fine for reference types, but be careful with operator overloading.");

        var report = $"""
            # Grill With Docs Report

            ## API Calls Detected ({apiCalls.Count})
            {string.Join("\n", checks)}

            ## Suspicious Patterns ({suspicious.Count})
            {(suspicious.Count == 0 ? "✓ None detected by heuristic" : string.Join("\n", suspicious.Select(s => $"- ⚠ {s}")))}

            ## Recommendations
            1. Look up each detected API in the official documentation.
            2. Verify the parameter order matches the signature in the docs.
            3. Check for newer versions of the API (deprecation warnings).
            4. Check the changelog for breaking changes.
            5. Add a comment citing the documentation URL next to each call.
            """;

        return Task.FromResult(new SkillResult
        {
            Text = report,
            Success = true,
            Data = new { ApiCalls = apiCalls, Suspicious = suspicious },
        });
    }

    private static List<string> ExtractApiCalls(string code)
    {
        // Match: CapitalizedMethodName(
        var matches = Regex.Matches(code, @"\b([A-Z][a-zA-Z0-9]+)\s*\(");
        return matches.Select(m => m.Groups[1].Value)
            .Distinct()
            .Where(n => !IsLikelyCsharpKeyword(n))
            .OrderBy(n => n)
            .ToList();
    }

    private static bool IsLikelyCsharpKeyword(string name)
    {
        var keywords = new[] { "If", "For", "While", "Switch", "Try", "Catch", "Finally", "Return", "Throw", "New", "Using", "Lock", "Async", "Await" };
        return keywords.Contains(name);
    }
}
