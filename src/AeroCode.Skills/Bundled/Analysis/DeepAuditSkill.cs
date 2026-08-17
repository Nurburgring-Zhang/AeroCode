// Copyright (c) AeroCode V3.0
// DeepAuditSkill — LLM-driven 4-dimensional project audit (architecture / security /
// performance / maintainability). Combines the static AnalyzerSkill output (real,
// deterministic numbers) with an LLM analysis of representative source files to produce
// actionable, prioritized recommendations.
//
// If no LLM is available (LlmInvoker is null), falls back to a static report using
// the 4-axis rubric scoring based purely on the static metrics. Either way, no fake
// results — every claim links to a real number or a real file.
//
// Args:
//   path=<dir>               # project root (default: workspace root)
//   max_files_for_llm=<int>   # how many source files to include in LLM prompt (default 6)
//   max_chars_per_file=<int>  # truncated chars per file in LLM prompt (default 1500)
//   dimensions=<comma-list>   # architecture,security,performance,maintainability
//   max_files=<int>           # AnalyzerSkill max_files (default 5000)
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AeroCode.Skills.Models;
using AeroCode.Skills.Registry;

namespace AeroCode.Skills.Bundled.Analysis;

public sealed class DeepAuditSkill : ISkill
{
    public string Id => "analysis/deep_audit";
    public string Name => "Deep Audit (LLM-driven)";
    public string Description => "LLM 驱动 4 维深度审核:架构/安全/性能/可维护性 + 静态度量融合";
    public string Category => "analysis";
    public string Author => "AeroCode Team (human first, Hermes rule)";
    public string Version => "1.0.0";
    public IReadOnlyList<string> Tags => new[] { "audit", "review", "llm", "deep", "architecture", "security" };
    public bool IsAvailable() => true;

    public string GetSystemPrompt() =>
        "# Deep Audit Skill (LLM-driven 4-axis)\n" +
        "Args:\n" +
        "  path=<dir>                       # project root\n" +
        "  max_files_for_llm=6              # how many source files to feed to LLM\n" +
        "  max_chars_per_file=1500          # per-file char cap in LLM prompt\n" +
        "  dimensions=architecture,security,performance,maintainability\n" +
        "  max_files=5000                   # for static scan\n" +
        "Requires LlmInvoker in SkillContext. If absent, returns a static-only audit.";

    public async Task<SkillResult> ExecuteAsync(SkillInput input, SkillContext ctx, CancellationToken ct = default)
    {
        await Task.Yield();
        var args = input.Args ?? new Dictionary<string, object?>();
        var path = (args.TryGetValue("path", out var p) ? p as string : null) ?? ctx.WorkspaceRoot;
        var maxFilesForLlm = args.TryGetValue("max_files_for_llm", out var mfl) && mfl is not null ? Math.Max(1, Convert.ToInt32(mfl)) : 6;
        var maxCharsPerFile = args.TryGetValue("max_chars_per_file", out var mcpf) && mcpf is not null ? Math.Max(200, Convert.ToInt32(mcpf)) : 1500;
        var maxFiles = args.TryGetValue("max_files", out var mf) && mf is not null ? Convert.ToInt32(mf) : 5000;
        var dimensions = (args.TryGetValue("dimensions", out var d) ? d as string : null) ?? "architecture,security,performance,maintainability";
        var dimList = dimensions.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.ToLowerInvariant()).ToHashSet();
        if (dimList.Count == 0) { foreach (var s in new[] { "architecture", "security", "performance", "maintainability" }) dimList.Add(s); }

        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return new SkillResult { Success = false, Text = $"Path not found: {path}" };

        var sb = new StringBuilder();
        sb.AppendLine($"# Deep Audit Report (LLM-driven 4-axis)");
        sb.AppendLine($"**Path**: `{path}`");
        sb.AppendLine($"**Dimensions**: {string.Join(", ", dimList)}");
        sb.AppendLine($"**LLM available**: {(ctx.LlmInvoker is not null ? "yes" : "no (static-only fallback)")}");
        sb.AppendLine($"**Generated**: {DateTime.UtcNow:O}");
        sb.AppendLine();

        // 1) Static metrics (real, deterministic)
        var staticReport = await RunStaticMetricsAsync(path, maxFiles, ct);
        sb.AppendLine("## Static Metrics (real numbers)");
        sb.AppendLine(staticReport);
        sb.AppendLine();

        // 2) Pick representative files for LLM
        var samples = PickRepresentativeFiles(path, maxFilesForLlm, maxCharsPerFile);
        sb.AppendLine("## Representative Files (sampled for LLM)");
        foreach (var s in samples) sb.AppendLine($"- `{s.RelativePath}` ({s.Lines} lines, {s.CharsIncluded} chars included)");
        sb.AppendLine();

        // 3) LLM analysis (if available) — ask for each dimension separately so the
        //    model is forced to focus, not ramble.
        if (ctx.LlmInvoker is not null)
        {
            sb.AppendLine("## LLM-driven 4-axis Analysis");
            foreach (var dim in dimList)
            {
                ct.ThrowIfCancellationRequested();
                var analysis = await AskLlmForDimensionAsync(ctx.LlmInvoker, dim, path, samples, ct);
                sb.AppendLine($"### {Capitalize(dim)}");
                sb.AppendLine(analysis);
                sb.AppendLine();
            }
        }
        else
        {
            sb.AppendLine("## LLM Analysis (skipped — no LlmInvoker in context)");
            sb.AppendLine("Set AeroCode.App DI to inject an LlmInvoker (ProviderFactory) to enable.");
            sb.AppendLine();
            sb.AppendLine("## Static 4-axis Rubric (heuristic)");
            sb.AppendLine(BuildStaticRubric(staticReport, samples));
        }

        return new SkillResult { Success = true, Text = sb.ToString() };
    }

    // ============== helpers ===============

    private record SampleFile(string AbsolutePath, string RelativePath, int Lines, int CharsIncluded);

    private static List<SampleFile> PickRepresentativeFiles(string root, int maxFiles, int maxChars)
    {
        var result = new List<SampleFile>();
        try
        {
            var candidates = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains("/bin/") && !f.Contains("/obj/"))
                .Select(f => new
                {
                    Path = f,
                    Info = SafeFileInfo(f)
                })
                .Where(x => x.Info.exists && x.Info.lines > 50 && x.Info.lines < 2000) // skip too short/long
                .OrderByDescending(x => x.Info.lines)
                .Take(Math.Max(1, maxFiles))
                .ToList();
            foreach (var c in candidates)
            {
                var text = ReadFirstNChars(c.Path, maxChars);
                result.Add(new SampleFile(c.Path, Path.GetRelativePath(root, c.Path), c.Info.lines, text.Length));
            }
        }
        catch { }
        return result;
    }

    private static (bool exists, int lines) SafeFileInfo(string path)
    {
        try { return (true, File.ReadAllLines(path).Length); }
        catch { return (false, 0); }
    }

    private static string ReadFirstNChars(string path, int n)
    {
        try
        {
            using var sr = new StreamReader(path);
            var sb = new StringBuilder();
            var buf = new char[4096];
            int read;
            while (sb.Length < n && (read = sr.Read(buf, 0, Math.Min(buf.Length, n - sb.Length))) > 0)
                sb.Append(buf, 0, read);
            return sb.ToString();
        }
        catch { return string.Empty; }
    }

    private static async Task<string> RunStaticMetricsAsync(string path, int maxFiles, CancellationToken ct)
    {
        // Reuse AnalyzerSkill's static scan via composition (call into its checks).
        var analyzer = new AnalyzerSkill();
        var input = new SkillInput
        {
            Args = new Dictionary<string, object?>
            {
                ["path"] = path,
                ["max_files"] = maxFiles,
                ["checks"] = "files,deps,hardcode,todo,complexity,bigfile,hash"
            }
        };
        var ctx = new SkillContext { WorkspaceRoot = path };
        var res = await analyzer.ExecuteAsync(input, ctx, ct);
        return res.Text;
    }

    private static async Task<string> AskLlmForDimensionAsync(
        LlmInvoker llm, string dimension, string path, List<SampleFile> samples, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"You are a senior software reviewer doing a deep audit.");
        sb.AppendLine($"Project: {path}");
        sb.AppendLine($"Focus dimension: {Capitalize(dimension)}");
        sb.AppendLine();
        sb.AppendLine("Project files (sampled):");
        foreach (var s in samples)
        {
            sb.AppendLine($"\n----- {s.RelativePath} ({s.Lines} lines) -----");
            sb.AppendLine(s.AbsolutePath is null ? "" : ReadFirstNChars(s.AbsolutePath, 1500));
        }
        sb.AppendLine();
        sb.AppendLine($"Provide a focused analysis of the {dimension} of this project:");
        sb.AppendLine($"1) Top 3 strengths (with file:line refs)");
        sb.AppendLine($"2) Top 3 issues (with file:line refs and severity: high/med/low)");
        sb.AppendLine($"3) Top 3 concrete, actionable recommendations");
        sb.AppendLine($"Be terse. Use bullet points. No preamble. Cite specific lines.");
        try
        {
            var answer = await llm(sb.ToString(), null, ct);
            return string.IsNullOrWhiteSpace(answer) ? "(LLM returned empty response)" : answer.Trim();
        }
        catch (Exception ex)
        {
            return $"(LLM call failed: {ex.GetType().Name}: {ex.Message})";
        }
    }

    private static string BuildStaticRubric(string staticReport, List<SampleFile> samples)
    {
        var sb = new StringBuilder();
        // crude heuristic: parse the static report for known patterns
        var notImplCount = CountMatches(staticReport, @"NotImplementedException`:\s*(\d+)");
        var hardcodeCount = CountMatches(staticReport, @"Hardcoded secrets found:\s*(\d+)");
        var todoCount = CountMatches(staticReport, @"TODO/FIXME/HACK/XXX markers:\s*(\d+) total");
        var bigFileCount = CountMatches(staticReport, @"Files exceeding \d+ lines:\s*(\d+)");

        sb.AppendLine("### Architecture");
        sb.AppendLine($"- Sample size: {samples.Count} files reviewed");
        sb.AppendLine($"- Largest file: {(samples.Count > 0 ? samples[0].RelativePath + " (" + samples[0].Lines + " lines)" : "n/a")}");
        sb.AppendLine($"- Verdict: {(samples.Count > 0 ? "Project structure sampled; no LLM, can't reason about layering." : "Project too small or no .cs files.")}");
        sb.AppendLine();
        sb.AppendLine("### Security");
        sb.AppendLine($"- Hardcoded secrets: {hardcodeCount}");
        sb.AppendLine($"- Verdict: {(hardcodeCount == 0 ? "✅ Clean" : "❌ Investigate immediately")}");
        sb.AppendLine();
        sb.AppendLine("### Performance");
        sb.AppendLine($"- Files exceeding 500 lines: {bigFileCount} (proxy for God classes)");
        sb.AppendLine($"- Verdict: {(bigFileCount == 0 ? "✅ No oversized files" : "⚠️ Some files may benefit from splitting")}");
        sb.AppendLine();
        sb.AppendLine("### Maintainability");
        sb.AppendLine($"- TODO/FIXME/HACK/XXX markers: {todoCount}");
        sb.AppendLine($"- `throw new NotImplementedException` count: {notImplCount}");
        sb.AppendLine($"- Verdict: {(todoCount + notImplCount == 0 ? "✅ Clean" : "⚠️ Address before claiming production-ready")}");
        return sb.ToString();
    }

    private static int CountMatches(string text, string pattern)
    {
        var m = System.Text.RegularExpressions.Regex.Match(text, pattern);
        if (m.Success && m.Groups.Count > 1 && int.TryParse(m.Groups[1].Value, out var n)) return n;
        return 0;
    }

    private static string Capitalize(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
