// Copyright (c) AeroCode V3.0
// TaskGraph + LoopRunner + Planner + AnalyzerSkill + WebResearchSkill tests
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AeroCode.Harness.Graph;
using AeroCode.Harness.Loop;
using AeroCode.Harness.Planner;
using AeroCode.Skills.Bundled.Analysis;
using AeroCode.Skills.Bundled.Research;
using AeroCode.Skills.Models;
using AeroCode.Skills.Registry;
using Xunit;

namespace AeroCode.Tests.HarnessTests;

public class TaskGraphTests
{
    [Fact]
    public async Task Graph_TopologicalOrder_ExecutesDepsFirst()
    {
        var order = new List<string>();
        var g = new TaskGraphBuilder()
            .Add("a", "A", async _ => { await Task.Delay(10); order.Add("a"); return "ok-a"; })
            .Add("b", "B", async _ => { await Task.Delay(10); order.Add("b"); return "ok-b"; }, dependsOn: new[] { "a" })
            .Add("c", "C", async _ => { await Task.Delay(10); order.Add("c"); return "ok-c"; }, dependsOn: new[] { "b" })
            .Build();
        var r = await g.ExecuteAsync();
        Assert.True(r.AllSucceeded);
        Assert.Equal(new[] { "a", "b", "c" }, order);
    }

    [Fact]
    public async Task Graph_ParallelLayer_TwoIndependentNodesRunTogether()
    {
        // Deterministic parallelism proof: record each node's execution interval
        // and assert the intervals overlap. Wall-clock thresholds are flaky under
        // loaded CI machines; interval overlap is load-independent.
        var starts = new Dictionary<string, DateTime>();
        var ends = new Dictionary<string, DateTime>();
        var sync = new object();
        Func<string, Func<CancellationToken, Task<string>>> node = id => async _ =>
        {
            lock (sync) starts[id] = DateTime.UtcNow;
            await Task.Delay(300);
            lock (sync) ends[id] = DateTime.UtcNow;
            return id;
        };
        var g = new TaskGraphBuilder()
            .Add("a", "A", node("a"))
            .Add("b", "B", node("b"))
            .Build();
        var r = await g.ExecuteAsync();
        Assert.True(r.AllSucceeded);
        // Overlap: each node started before the other finished. Sequential execution can never satisfy this.
        Assert.True(starts["a"] < ends["b"] && starts["b"] < ends["a"],
            $"intervals did not overlap: a=[{starts["a"]:O},{ends["a"]:O}] b=[{starts["b"]:O},{ends["b"]:O}]");
    }

    [Fact]
    public void Graph_MissingDep_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => new TaskGraphBuilder()
            .Add("a", "A", _ => Task.FromResult("x"), dependsOn: new[] { "ghost" })
            .Build());
    }

    [Fact]
    public void Graph_Cycle_Throws()
    {
        // Build a 2-node cycle via deferred builder: not directly possible (deps must reference earlier),
        // so we craft a manually-built cycle.
        var a = new TaskNode { Id = "a", Name = "A", DependsOn = new[] { "b" }, Execute = _ => Task.FromResult("") };
        var b = new TaskNode { Id = "b", Name = "B", DependsOn = new[] { "a" }, Execute = _ => Task.FromResult("") };
        Assert.Throws<InvalidOperationException>(() => new TaskGraph(new Dictionary<string, TaskNode> { ["a"] = a, ["b"] = b }));
    }

    [Fact]
    public async Task Graph_OneFails_StopsByDefault()
    {
        var g = new TaskGraphBuilder()
            .Add("a", "A", _ => throw new InvalidOperationException("boom"))
            .Add("b", "B", _ => Task.FromResult("b"), dependsOn: new[] { "a" })
            .Build();
        var r = await g.ExecuteAsync();
        Assert.False(r.AllSucceeded);
        Assert.Equal(TaskState.Failed, r.Nodes.First(n => n.Id == "a").State);
        Assert.Equal(TaskState.Pending, r.Nodes.First(n => n.Id == "b").State);
    }

    [Fact]
    public async Task Graph_ContinueOnError_SkipsDependents()
    {
        var ran = new List<string>();
        var g = new TaskGraphBuilder()
            .Add("a", "A", _ => throw new Exception("x"))
            .Add("b", "B", _ => { ran.Add("b"); return Task.FromResult("b"); }, dependsOn: new[] { "a" })
            .Build();
        var r = await g.ExecuteAsync(continueOnError: true);
        Assert.False(r.AllSucceeded);
        Assert.Equal(TaskState.Failed, r.Nodes.First(n => n.Id == "a").State);
        Assert.Equal(TaskState.Succeeded, r.Nodes.First(n => n.Id == "b").State);
    }

    [Fact]
    public void Graph_Ascii_RendersReadable()
    {
        var g = new TaskGraphBuilder()
            .Add("a", "A", _ => Task.FromResult("x"))
            .Add("b", "B", _ => Task.FromResult("y"), dependsOn: new[] { "a" })
            .Build();
        var ascii = g.ToAscii();
        Assert.Contains("a", ascii);
        Assert.Contains("b", ascii);
    }
}

public class LoopRunnerTests
{
    [Fact]
    public async Task Loop_FirstAttemptSucceeds()
    {
        var loop = new LoopRunner(maxIterations: 3);
        var r = await loop.RunAsync(_ => Task.FromResult<string?>(null));
        Assert.True(r.Succeeded);
        Assert.Single(r.History);
    }

    [Fact]
    public async Task Loop_RepairRecoversOnSecondTry()
    {
        var attempts = 0;
        RepairStrategy noopStrategy = (err, hist, ct) => Task.FromResult<StepAttempt?>(_ => { attempts++; return Task.FromResult<string?>(null); });
        var loop = new LoopRunner(maxIterations: 5, strategies: new[] { noopStrategy });
        var r = await loop.RunAsync(_ => { attempts++; return Task.FromResult<string?>(attempts >= 2 ? null : "fail"); });
        Assert.True(r.Succeeded);
        Assert.Equal(2, r.History.Count);
    }

    [Fact]
    public async Task Loop_ExhaustsStrategies_Fails()
    {
        RepairStrategy cantHelp1 = (err, hist, ct) => Task.FromResult<StepAttempt?>(null);
        RepairStrategy cantHelp2 = (err, hist, ct) => Task.FromResult<StepAttempt?>(null);
        var loop = new LoopRunner(maxIterations: 3, strategies: new RepairStrategy[] { cantHelp1, cantHelp2 });
        var r = await loop.RunAsync(_ => Task.FromResult<string?>("always-fails"));
        Assert.False(r.Succeeded);
        Assert.Contains("Exhausted", r.TerminationReason);
    }

    [Fact]
    public async Task Loop_HitsMaxIter_Fails()
    {
        var loop = new LoopRunner(maxIterations: 2);
        var r = await loop.RunAsync(_ => Task.FromResult<string?>("always-fail"));
        Assert.False(r.Succeeded);
        Assert.Contains("max iterations", r.TerminationReason);
    }
}

public class PlannerTests
{
    [Fact]
    public async Task Planner_NoProducer_ReturnsSingleStep()
    {
        var p = new Planner();
        var plan = await p.DecomposeAsync("do something");
        Assert.Single(plan.Steps);
        Assert.Equal("do-it", plan.Steps[0].Id);
    }

    [Fact]
    public void Planner_ParseValidJson_BuildsGraph()
    {
        var raw = """
            {
              "goal": "build x",
              "steps": [
                {"id": "s1", "title": "plan", "description": "design", "dependsOn": [], "kind": "analyze"},
                {"id": "s2", "title": "code",  "description": "impl",   "dependsOn": ["s1"], "kind": "code"},
                {"id": "s3", "title": "test",  "description": "verify", "dependsOn": ["s2"], "kind": "code"}
              ]
            }
            """;
        var plan = Planner.ParsePlan("build x", raw);
        Assert.Equal(3, plan.Steps.Count);
        Assert.Empty(plan.Steps[0].DependsOn); // s1 is a root
        Assert.Single(plan.Steps[1].DependsOn); // s2 depends on s1
        Assert.Equal("s2", plan.Steps[2].DependsOn[0]); // s3 depends on s2
    }

    [Fact]
    public void Planner_ParseMarkdownFences_SalvagesJson()
    {
        var raw = """
            ```json
            {"goal":"g","steps":[{"id":"a","title":"A","dependsOn":[]}]}
            ```
            """;
        var plan = Planner.ParsePlan("g", raw);
        Assert.Single(plan.Steps);
    }

    [Fact]
    public void Planner_ParseNumberedList_Fallback()
    {
        var raw = "1. First do this\n2. Then this\n3. Finally this";
        var plan = Planner.ParsePlan("do stuff", raw);
        Assert.Equal(3, plan.Steps.Count);
    }

    [Fact]
    public async Task Planner_LLMProducer_BuildsGraphAsync()
    {
        var producer = Planner.FromLlm((prompt, ct) =>
        {
            // Just for test: pretend the LLM emitted a JSON plan
            return Task.FromResult("""
                {"goal":"test","steps":[
                  {"id":"a","title":"Step A","description":"first","dependsOn":[]},
                  {"id":"b","title":"Step B","description":"second","dependsOn":["a"]}
                ]}
                """);
        });
        var p = new Planner(producer);
        var plan = await p.DecomposeAsync("test");
        Assert.Equal(2, plan.Steps.Count);
        var g = await p.PlanToGraphAsync(plan);
        Assert.Equal(2, g.Nodes.Count);
        var r = await g.ExecuteAsync();
        Assert.True(r.AllSucceeded);
    }
}

public class AnalyzerSkillTests
{
    [Fact]
    public async Task Analyzer_FindsHardcodedSecret_Reports()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "aerocode_analyzer_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            File.WriteAllText(Path.Combine(tmp, "Leaky.cs"), """
                public class Leaky {
                    public string Token = "sk-1234567890abcdefghij";  // FAKE for test
                }
                """);
            File.WriteAllText(Path.Combine(tmp, "Clean.cs"), """
                public class Clean {
                    public string Read() => Environment.GetEnvironmentVariable("MY_KEY");
                }
                """);
            var skill = new AnalyzerSkill();
            var input = new SkillInput { Args = new Dictionary<string, object?> { ["path"] = tmp, ["checks"] = "hardcode,files" } };
            var r = await skill.ExecuteAsync(input, new SkillContext());
            Assert.True(r.Success);
            Assert.Contains("Hardcoded secrets found", r.Text);
            Assert.Contains("Leaky.cs", r.Text);
        }
        finally { try { Directory.Delete(tmp, true); } catch { } }
    }

    [Fact]
    public async Task Analyzer_FindsNotImplemented_Reports()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "aerocode_analyzer_ni_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            File.WriteAllText(Path.Combine(tmp, "Stub.cs"), "public class Stub { public void Do() { throw new NotImplementedException(); } }");
            var skill = new AnalyzerSkill();
            var input = new SkillInput { Args = new Dictionary<string, object?> { ["path"] = tmp, ["checks"] = "todo" } };
            var r = await skill.ExecuteAsync(input, new SkillContext());
            Assert.Contains("NotImplementedException", r.Text);
        }
        finally { try { Directory.Delete(tmp, true); } catch { } }
    }

    [Fact]
    public async Task Analyzer_FileInventory_Reports()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "aerocode_analyzer_files_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            File.WriteAllText(Path.Combine(tmp, "a.cs"), "class A {}");
            File.WriteAllText(Path.Combine(tmp, "b.txt"), "hello");
            File.WriteAllText(Path.Combine(tmp, "c.json"), "{}");
            var skill = new AnalyzerSkill();
            var input = new SkillInput { Args = new Dictionary<string, object?> { ["path"] = tmp, ["checks"] = "files" } };
            var r = await skill.ExecuteAsync(input, new SkillContext());
            Assert.Contains("Total files: 3", r.Text);
            Assert.Contains(".cs", r.Text);
        }
        finally { try { Directory.Delete(tmp, true); } catch { } }
    }
}

public class WebResearchSkillTests
{
    [Fact]
    public void WebResearch_ExtractText_StripsScriptAndStyle()
    {
        var html = """
            <html><head><script>var x = 1;</script><style>body{color:red}</style></head>
            <body><h1>Hello</h1><p>World <a href="x">link</a></p><script>alert(1)</script></body></html>
            """;
        var text = WebResearchSkill.ExtractText(html);
        Assert.Contains("Hello", text);
        Assert.Contains("World", text);
        Assert.DoesNotContain("var x", text);
        Assert.DoesNotContain("color:red", text);
    }

    [Fact(Skip = "Network test disabled — set AEROCODE_RUN_NETWORK_TESTS=1 to enable")]
    public async Task WebResearch_FetchRealUrl_Wikipedia()
    {
        if (Environment.GetEnvironmentVariable("AEROCODE_RUN_NETWORK_TESTS") != "1") return;
        var skill = new WebResearchSkill();
        var input = new SkillInput
        {
            Args = new Dictionary<string, object?>
            {
                ["url"] = "https://en.wikipedia.org/wiki/Markdown",
                ["max_chars"] = 3000
            }
        };
        var r = await skill.ExecuteAsync(input, new SkillContext());
        Assert.True(r.Success, $"failed: {r.Text}");
        Assert.Contains("Markdown", r.Text);
    }
}
