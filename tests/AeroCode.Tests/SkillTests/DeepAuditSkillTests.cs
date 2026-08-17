// Copyright (c) AeroCode V3.0
// DeepAuditSkill tests — static-only fallback (no LLM) + LLM path
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AeroCode.Skills.Bundled.Analysis;
using AeroCode.Skills.Models;
using AeroCode.Skills.Registry;
using Xunit;

namespace AeroCode.Tests.SkillTests;

public class DeepAuditSkillTests
{
    private static SkillContext Ctx(string? path = null) => new()
    {
        WorkspaceRoot = path ?? Environment.CurrentDirectory
    };

    [Fact]
    public async Task NoLlm_StaticFallback_Produces4AxisRubric()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "audit_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            File.WriteAllText(Path.Combine(tmp, "Sample.cs"), "class C { void M() { var x = 1; } }");
            var skill = new DeepAuditSkill();
            var input = new SkillInput
            {
                Args = new Dictionary<string, object?>
                {
                    ["path"] = tmp,
                    ["max_files_for_llm"] = 2,
                    ["max_files"] = 50
                }
            };
            var res = await skill.ExecuteAsync(input, Ctx(tmp));
            Assert.True(res.Success, res.Text);
            Assert.Contains("Deep Audit Report", res.Text);
            Assert.Contains("Static 4-axis Rubric", res.Text);
            Assert.Contains("Architecture", res.Text);
            Assert.Contains("Security", res.Text);
            Assert.Contains("Performance", res.Text);
            Assert.Contains("Maintainability", res.Text);
            Assert.Contains("no (static-only fallback)", res.Text);
        }
        finally { Directory.Delete(tmp, true); }
    }

    [Fact]
    public async Task WithLlm_LlmPath_IsInvokedOncePerDimension()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "audit_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            File.WriteAllText(Path.Combine(tmp, "Sample.cs"), "class C { void M() { } }");
            var invocations = 0;
            async Task<string> Llm(string prompt, IReadOnlyDictionary<string, object?>? options, CancellationToken ct)
            {
                invocations++;
                await Task.Delay(1, ct);
                return "**Architecture**\n- Sample strength: clean code\n**Security**\n- Sample issue: none\n**Performance**\n- Verdict: fine\n**Maintainability**\n- Verdict: fine";
            }
            var ctx = new SkillContext { WorkspaceRoot = tmp, LlmInvoker = Llm };
            var skill = new DeepAuditSkill();
            var input = new SkillInput
            {
                Args = new Dictionary<string, object?>
                {
                    ["path"] = tmp,
                    ["max_files_for_llm"] = 1,
                    ["max_files"] = 50
                }
            };
            var res = await skill.ExecuteAsync(input, ctx);
            Assert.True(res.Success, res.Text);
            Assert.Equal(4, invocations); // 4 dimensions
            Assert.Contains("LLM-driven 4-axis Analysis", res.Text);
            Assert.Contains("Architecture", res.Text);
            Assert.Contains("Security", res.Text);
            Assert.Contains("Performance", res.Text);
            Assert.Contains("Maintainability", res.Text);
        }
        finally { Directory.Delete(tmp, true); }
    }

    [Fact]
    public async Task WithLlm_LlmFails_ReportStillReturned()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "audit_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            File.WriteAllText(Path.Combine(tmp, "Sample.cs"), "class C { void M() { } }");
            Task<string> Llm(string prompt, IReadOnlyDictionary<string, object?>? options, CancellationToken ct)
                => throw new InvalidOperationException("API down");
            var ctx = new SkillContext { WorkspaceRoot = tmp, LlmInvoker = Llm };
            var skill = new DeepAuditSkill();
            var input = new SkillInput
            {
                Args = new Dictionary<string, object?>
                {
                    ["path"] = tmp,
                    ["max_files"] = 50
                }
            };
            var res = await skill.ExecuteAsync(input, ctx);
            Assert.True(res.Success, res.Text); // LLM failure is not fatal
            Assert.Contains("LLM call failed", res.Text);
        }
        finally { Directory.Delete(tmp, true); }
    }
}
