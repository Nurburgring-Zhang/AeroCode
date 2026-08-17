// Copyright (c) AeroCode V3.2
// RoslynAnalyzerSkill tests — real Roslyn parsing on synthetic code.
using System;
using System.IO;
using System.Threading.Tasks;
using AeroCode.Skills.Bundled.Analysis;
using AeroCode.Skills.Models;
using AeroCode.Skills.Registry;
using Xunit;

namespace AeroCode.Tests.AiTests;

public class RoslynAnalyzerSkillTests
{
    private static SkillContext Ctx() => new() { WorkspaceRoot = Environment.CurrentDirectory };

    [Fact]
    public async Task Roslyn_Real_Syntax_EmptyCatch_NotImpl_LongMethod_AsyncVoid_AllDetected()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "roslyn_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            // a) empty catch (no declaration) {}
            File.WriteAllText(Path.Combine(tmp, "EmptyCatch.cs"),
                "class A { void M() { try { } catch { } } }");
            // b) throw new NotImplementedException
            File.WriteAllText(Path.Combine(tmp, "NotImpl.cs"),
                "class B { void M() { throw new System.NotImplementedException(); } }");
            // c) long method (>50*80 chars in body)
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("class C { void M() {");
            for (var i = 0; i < 100; i++) sb.AppendLine($"    System.Console.WriteLine(\"line {i} padding padding padding padding padding padding padding padding\");");
            sb.AppendLine("} }");
            File.WriteAllText(Path.Combine(tmp, "Long.cs"), sb.ToString());
            // d) long parameter list (>5 params)
            File.WriteAllText(Path.Combine(tmp, "ManyParams.cs"),
                "class D { void M(int a, int b, int c, int d, int e, int f) { } }");
            // e) async void
            File.WriteAllText(Path.Combine(tmp, "AsyncVoid.cs"),
                "class E { async void M() { await System.Threading.Tasks.Task.Delay(1); } }");

            var skill = new RoslynAnalyzerSkill();
            var input = new SkillInput
            {
                Args = new System.Collections.Generic.Dictionary<string, object?>
                {
                    ["path"] = tmp,
                    ["max_files"] = 50
                }
            };
            var res = await skill.ExecuteAsync(input, Ctx());
            Assert.True(res.Success, res.Text);
            Assert.Contains("Roslyn Compiler Diagnostics (real)", res.Text);
            Assert.Contains("empty_catch_block**: 1", res.Text);
            Assert.Contains("not_implemented_throw**: 1", res.Text);
            Assert.Contains("long_method**: 1", res.Text);
            Assert.Contains("long_parameter_list**: 1", res.Text);
            Assert.Contains("async_void**: 1", res.Text);
        }
        finally { Directory.Delete(tmp, true); }
    }

    [Fact]
    public async Task Roslyn_DetectsReal_CompilerErrors_OnBrokenCode()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "roslyn_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            // missing semicolon → CS1002
            File.WriteAllText(Path.Combine(tmp, "Broken.cs"),
                "class X { void M() { var x = 1 } }"); // no ;
            var skill = new RoslynAnalyzerSkill();
            var input = new SkillInput
            {
                Args = new System.Collections.Generic.Dictionary<string, object?>
                {
                    ["path"] = tmp,
                    ["max_files"] = 50
                }
            };
            var res = await skill.ExecuteAsync(input, Ctx());
            Assert.True(res.Success);
            // Should report at least 1 compiler error.
            Assert.Contains("Errors:", res.Text);
        }
        finally { Directory.Delete(tmp, true); }
    }
}
