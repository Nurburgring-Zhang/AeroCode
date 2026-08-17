// Copyright (c) AeroCode V3.0
// AnalyzerSkill v2 deep tests (complexity, hash, bigfile, full report)
using System;
using System.IO;
using System.Threading.Tasks;
using AeroCode.Skills.Bundled.Analysis;
using AeroCode.Skills.Models;
using AeroCode.Skills.Registry;
using Xunit;

namespace AeroCode.Tests.SkillTests;

public class AnalyzerSkillDeepTests
{
    private static SkillContext Ctx() => new() { WorkspaceRoot = Environment.CurrentDirectory };

    [Fact]
    public async Task FullReport_AllChecks_ProduceAllSections()
    {
        // Use the test project itself as the "project under test" — it's a real .NET project.
        var testProjectRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        if (!Directory.Exists(testProjectRoot)) testProjectRoot = AppContext.BaseDirectory;
        var skill = new AnalyzerSkill();
        var input = new SkillInput
        {
            Args = new System.Collections.Generic.Dictionary<string, object?>
            {
                ["path"] = testProjectRoot,
                ["checks"] = "files,deps,hardcode,todo,git,complexity,hash,bigfile",
                ["max_files"] = 2000
            }
        };
        var res = await skill.ExecuteAsync(input, Ctx());
        Assert.True(res.Success, res.Text);
        Assert.Contains("File Inventory", res.Text);
        Assert.Contains("Dependency Graph", res.Text);
        Assert.Contains("Hardcoded Secret Scan", res.Text);
        Assert.Contains("TODO", res.Text);
        Assert.Contains("Cyclomatic Complexity Scan", res.Text);
        Assert.Contains("SHA-256 File Fingerprint", res.Text);
        Assert.Contains("Big-File Scan", res.Text);
    }

    [Fact]
    public async Task HardcodeScan_DetectsHardcodedKey_Fixture()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "audit_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            // positive case
            File.WriteAllText(Path.Combine(tmp, "Leaky.cs"),
                "class L { string key = \"sk-1234567890abcdef1234\"; void M() { var p = \"password=\\\"hunter2\\\"\"; } }");
            // negative case
            File.WriteAllText(Path.Combine(tmp, "Clean.cs"),
                "class C { string s = \"no secrets here\"; }");
            var skill = new AnalyzerSkill();
            var input = new SkillInput
            {
                Args = new System.Collections.Generic.Dictionary<string, object?>
                {
                    ["path"] = tmp,
                    ["checks"] = "hardcode",
                    ["max_files"] = 50
                }
            };
            var res = await skill.ExecuteAsync(input, Ctx());
            Assert.True(res.Success, res.Text);
            Assert.Contains("Leaky.cs", res.Text);
            Assert.Contains("Hardcoded secrets found: 1", res.Text);
        }
        finally { Directory.Delete(tmp, true); }
    }

    [Fact]
    public async Task HashFingerprint_ProducesAggregateHash_Deterministic()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "audit_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            File.WriteAllText(Path.Combine(tmp, "A.cs"), "class A {}");
            File.WriteAllText(Path.Combine(tmp, "B.cs"), "class B {}");
            var skill = new AnalyzerSkill();
            var input = new SkillInput
            {
                Args = new System.Collections.Generic.Dictionary<string, object?>
                {
                    ["path"] = tmp,
                    ["checks"] = "hash",
                    ["max_files"] = 50
                }
            };
            var r1 = await skill.ExecuteAsync(input, Ctx());
            var r2 = await skill.ExecuteAsync(input, Ctx());
            Assert.True(r1.Success);
            // Extract aggregate hash from both
            var hash1 = ExtractAggregate(r1.Text);
            var hash2 = ExtractAggregate(r2.Text);
            Assert.NotNull(hash1);
            Assert.Equal(hash1, hash2); // deterministic
        }
        finally { Directory.Delete(tmp, true); }
    }

    [Fact]
    public async Task ComplexityScan_FlagsComplexFixture()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "audit_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            // A function with 20+ decision points
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("class C {");
            sb.AppendLine("  int F(int x) {");
            for (var i = 0; i < 20; i++) sb.AppendLine($"    if (x == {i}) return {i};");
            sb.AppendLine("    return -1;");
            sb.AppendLine("  }");
            sb.AppendLine("}");
            File.WriteAllText(Path.Combine(tmp, "Complex.cs"), sb.ToString());
            var skill = new AnalyzerSkill();
            var input = new SkillInput
            {
                Args = new System.Collections.Generic.Dictionary<string, object?>
                {
                    ["path"] = tmp,
                    ["checks"] = "complexity",
                    ["max_files"] = 50,
                    ["complexity_threshold"] = 10
                }
            };
            var res = await skill.ExecuteAsync(input, Ctx());
            Assert.True(res.Success, res.Text);
            Assert.Contains("Complex.cs", res.Text);
            Assert.Contains("flagged", res.Text, StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(tmp, true); }
    }

    [Fact]
    public async Task BigFileScan_FlagsLargeFile()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "audit_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var sb = new System.Text.StringBuilder();
            for (var i = 0; i < 600; i++) sb.AppendLine($"// line {i}");
            File.WriteAllText(Path.Combine(tmp, "Big.cs"), sb.ToString());
            var skill = new AnalyzerSkill();
            var input = new SkillInput
            {
                Args = new System.Collections.Generic.Dictionary<string, object?>
                {
                    ["path"] = tmp,
                    ["checks"] = "bigfile",
                    ["max_files"] = 50,
                    ["big_file_lines"] = 500
                }
            };
            var res = await skill.ExecuteAsync(input, Ctx());
            Assert.True(res.Success, res.Text);
            Assert.Contains("Big.cs", res.Text);
        }
        finally { Directory.Delete(tmp, true); }
    }

    private static string? ExtractAggregate(string text)
    {
        var marker = "**Project aggregate hash**: `";
        var idx = text.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0) return null;
        var start = idx + marker.Length;
        var end = text.IndexOf('`', start);
        return end > start ? text[start..end] : null;
    }
}
