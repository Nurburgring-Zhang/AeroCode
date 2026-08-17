// Copyright (c) AeroCode V3.0
// CodeReviewSkill — 8-dimension code review (Google eng-practices + Matt Pocock).
using System.Text.RegularExpressions;
using AeroCode.Skills.Registry;

namespace AeroCode.Skills.Bundled.Engineering;

/// <summary>
/// 8-dimension code review skill, inspired by Google eng-practices + Matt Pocock code-review skill.
/// Checks: Design, Functionality, Complexity, Tests, Naming, Comments, Style, Documentation.
/// </summary>
public sealed class CodeReviewSkill : ISkill
{
    public string Id => "engineering/code-review";
    public string Name => "Code Review";
    public string Description => "Review code against 8 engineering dimensions.";
    public string Category => "engineering";
    public string Author => "Matt Pocock (skills), Google (eng-practices)";
    public string Version => "1.0.0";
    public IReadOnlyList<string> Tags => new[] { "code-review", "engineering", "quality" };

    public string GetSystemPrompt() => """
        # Code Review (8 Dimensions)
        When reviewing code, check these 8 dimensions (Google eng-practices):
        1. Design — well-designed and appropriate for the system?
        2. Functionality — behaves as intended? good for users?
        3. Complexity — could it be simpler? maintainable?
        4. Tests — has correct, well-designed automated tests?
        5. Naming — clear variable/class/method names?
        6. Comments — clear and useful?
        7. Style — follows style guide?
        8. Documentation — updated relevant docs?
        """;

    public bool IsAvailable() => true;

    public Task<SkillResult> ExecuteAsync(SkillInput input, SkillContext ctx, CancellationToken ct = default)
    {
        var code = input.Args.TryGetValue("code", out var c) ? c as string : null;
        if (string.IsNullOrWhiteSpace(code))
            return Task.FromResult(new SkillResult { Text = "Error: 'code' argument is required", Success = false });

        var report = new CodeReviewReport
        {
            Design = CheckDesign(code),
            Functionality = CheckFunctionality(code),
            Complexity = CheckComplexity(code),
            Tests = CheckTests(code),
            Naming = CheckNaming(code),
            Comments = CheckComments(code),
            Style = CheckStyle(code),
            Documentation = CheckDocumentation(code),
        };

        var summary = FormatReport(report);
        return Task.FromResult(new SkillResult
        {
            Text = summary,
            Data = report,
            Success = true,
            NextActions = new[] { "apply-suggestions", "ignore", "explain" },
        });
    }

    private static DimensionResult CheckDesign(string code)
    {
        var issues = new List<string>();
        if (code.Length > 2000) issues.Add("File is large (>2000 lines). Consider splitting.");
        if (CountOccurrences(code, "class ") > 5) issues.Add("Multiple classes in one file. Consider separating.");

        return new DimensionResult("Design", Severity.None, issues);
    }

    private static DimensionResult CheckFunctionality(string code)
    {
        var issues = new List<string>();
        // Empty methods are suspicious
        var emptyMethodMatches = Regex.Matches(code, @"\b(public|private|internal|protected)\s+[\w<>]+\s+\w+\([^)]*\)\s*\{\s*\}");
        if (emptyMethodMatches.Count > 0) issues.Add($"{emptyMethodMatches.Count} empty method(s) found. Did you forget to implement them?");
        return new DimensionResult("Functionality", Severity.None, issues);
    }

    private static DimensionResult CheckComplexity(string code)
    {
        var lineCount = code.Split('\n').Length;
        var keywords = new[] { "if", "for", "while", "case", "&&", "||" };
        var cyclomatic = 1 + keywords.Sum(k => CountOccurrences(code, k));
        var maxNesting = ComputeMaxNesting(code);

        var issues = new List<string>();
        if (cyclomatic > 20) issues.Add($"High cyclomatic complexity: {cyclomatic} (>20). Refactor recommended.");
        if (maxNesting > 5) issues.Add($"Deep nesting: {maxNesting} levels (>5). Extract helper methods.");
        if (lineCount > 500) issues.Add($"File has {lineCount} lines. Google recommends <200-400 lines per CL.");

        return new DimensionResult("Complexity", issues.Count == 0 ? Severity.None : Severity.Minor, issues)
        {
            Metrics = new Dictionary<string, object>
            {
                ["lineCount"] = lineCount,
                ["cyclomaticComplexity"] = cyclomatic,
                ["maxNesting"] = maxNesting,
            }
        };
    }

    private static DimensionResult CheckTests(string code)
    {
        var issues = new List<string>();
        var hasTestFile = code.Contains("[Test]", StringComparison.OrdinalIgnoreCase)
            || code.Contains("[Fact]", StringComparison.OrdinalIgnoreCase);
        var hasPublicApi = Regex.IsMatch(code, @"\b(public|internal)\s+(class|interface|static\s+\w+\s+)\w+");
        if (hasPublicApi && !hasTestFile) issues.Add("Public API exposed but no test attributes ([Test]/[Fact]) found in this file.");
        return new DimensionResult("Tests", hasTestFile ? Severity.None : Severity.Minor, issues);
    }

    private static DimensionResult CheckNaming(string code)
    {
        var issues = new List<string>();
        // C# convention: interfaces start with I, but no underscore
        if (Regex.IsMatch(code, @"\binterface\s+_[A-Z]")) issues.Add("Interface name starts with underscore. Use I prefix instead.");
        // constants should be UPPER_SNAKE_CASE
        var badConsts = Regex.Matches(code, @"\bconst\s+\w+\s+([a-z][a-zA-Z0-9]*)\s*=");
        if (badConsts.Count > 0) issues.Add($"{badConsts.Count} const(s) not in UPPER_SNAKE_CASE convention.");
        return new DimensionResult("Naming", issues.Count == 0 ? Severity.None : Severity.Nit, issues);
    }

    private static DimensionResult CheckComments(string code)
    {
        var issues = new List<string>();
        var todoCount = CountOccurrences(code, "TODO") + CountOccurrences(code, "FIXME") + CountOccurrences(code, "XXX");
        if (todoCount > 0) issues.Add($"{todoCount} TODO/FIXME/XXX marker(s) found. Resolve before merging.");
        var xmlDocOnPublic = Regex.Matches(code, @"///\s*<summary>");
        var publicDecls = Regex.Matches(code, @"\b(public)\s+(class|interface|static|void|async|[\w<>]+)\s+\w+");
        if (publicDecls.Count > 0 && xmlDocOnPublic.Count < publicDecls.Count)
            issues.Add($"{publicDecls.Count - xmlDocOnPublic.Count} public declaration(s) missing XML doc comment.");
        return new DimensionResult("Comments", issues.Count == 0 ? Severity.None : Severity.Minor, issues);
    }

    private static DimensionResult CheckStyle(string code)
    {
        var issues = new List<string>();
        // Tabs vs spaces (rough check)
        var tabs = code.Count(c => c == '\t');
        var leadingSpaces = Regex.Matches(code, @"^( +)", RegexOptions.Multiline).Count;
        if (tabs > 0 && leadingSpaces > 0) issues.Add("Mixed tabs and spaces. Pick one.");
        // Trailing whitespace
        if (Regex.IsMatch(code, @" +$", RegexOptions.Multiline)) issues.Add("Trailing whitespace found.");
        return new DimensionResult("Style", issues.Count == 0 ? Severity.None : Severity.Nit, issues);
    }

    private static DimensionResult CheckDocumentation(string code)
    {
        var issues = new List<string>();
        // Hard to detect "doc updated" automatically, so just check for any inline doc.
        var readmeRef = code.Contains("README", StringComparison.OrdinalIgnoreCase);
        if (!readmeRef) issues.Add("No mention of README in this change. Did you update user-facing docs?");
        return new DimensionResult("Documentation", Severity.None, issues);
    }

    private static int CountOccurrences(string text, string pattern)
    {
        var count = 0;
        var idx = 0;
        while ((idx = text.IndexOf(pattern, idx, StringComparison.Ordinal)) != -1)
        {
            count++;
            idx += pattern.Length;
        }
        return count;
    }

    private static int ComputeMaxNesting(string code)
    {
        var maxNesting = 0;
        foreach (var line in code.Split('\n'))
        {
            var trimmed = line.TrimStart();
            var indent = line.Length - trimmed.Length;
            // 4-space indent == 1 level
            var level = indent / 4;
            if (level > maxNesting && (trimmed.Contains('{') || trimmed.StartsWith("if") || trimmed.StartsWith("for")))
                maxNesting = level;
        }
        return maxNesting;
    }

    private static string FormatReport(CodeReviewReport r)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# Code Review Report (8 Dimensions)");
        sb.AppendLine();
        AppendDim(sb, r.Design);
        AppendDim(sb, r.Functionality);
        AppendDim(sb, r.Complexity);
        AppendDim(sb, r.Tests);
        AppendDim(sb, r.Naming);
        AppendDim(sb, r.Comments);
        AppendDim(sb, r.Style);
        AppendDim(sb, r.Documentation);
        return sb.ToString();
    }

    private static void AppendDim(System.Text.StringBuilder sb, DimensionResult d)
    {
        sb.AppendLine($"## {d.Name} — {d.Severity}");
        if (d.Metrics is { Count: > 0 })
        {
            foreach (var kv in d.Metrics)
                sb.AppendLine($"  - {kv.Key}: {kv.Value}");
        }
        foreach (var issue in d.Issues)
            sb.AppendLine($"  - {issue}");
        if (d.Issues.Count == 0)
            sb.AppendLine("  ✓ OK");
        sb.AppendLine();
    }
}

public enum Severity { None, Nit, Minor, Major, Blocker }

public sealed class DimensionResult
{
    public string Name { get; }
    public Severity Severity { get; }
    public List<string> Issues { get; }
    public Dictionary<string, object>? Metrics { get; set; }

    public DimensionResult(string name, Severity severity, List<string> issues)
    {
        Name = name;
        Severity = severity;
        Issues = issues;
    }
}

public sealed class CodeReviewReport
{
    public DimensionResult Design { get; set; } = new("Design", Severity.None, new());
    public DimensionResult Functionality { get; set; } = new("Functionality", Severity.None, new());
    public DimensionResult Complexity { get; set; } = new("Complexity", Severity.None, new());
    public DimensionResult Tests { get; set; } = new("Tests", Severity.None, new());
    public DimensionResult Naming { get; set; } = new("Naming", Severity.None, new());
    public DimensionResult Comments { get; set; } = new("Comments", Severity.None, new());
    public DimensionResult Style { get; set; } = new("Style", Severity.None, new());
    public DimensionResult Documentation { get; set; } = new("Documentation", Severity.None, new());
}
