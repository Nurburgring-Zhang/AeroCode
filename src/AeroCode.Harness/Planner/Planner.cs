// Copyright (c) AeroCode V3.0
// Planner — Decompose a high-level goal into a TaskGraph (DAG).
// Backed by an LLM for semantic decomposition, with a deterministic
// fallback for when no LLM is available.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AeroCode.Harness.Graph;
using AeroCode.Harness.Loop;

namespace AeroCode.Harness.Planner;

/// <summary>
/// A high-level plan: a list of sub-tasks with dependencies.
/// </summary>
public sealed class Plan
{
    public required string Goal { get; init; }
    public IReadOnlyList<PlanStep> Steps { get; init; } = Array.Empty<PlanStep>();
    public string? SourcePrompt { get; init; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}

public sealed class PlanStep
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public string? Description { get; init; }
    public IReadOnlyList<string> DependsOn { get; init; } = Array.Empty<string>();
    /// <summary>Optional type hint: "shell", "code", "read", "write", "web", "analyze".</summary>
    public string? Kind { get; init; }
}

/// <summary>
/// A function that produces a Plan from a goal string.
/// </summary>
public delegate Task<Plan> PlanProducer(string goal, CancellationToken ct);

/// <summary>
/// Planner that takes a goal and returns a TaskGraph ready to execute.
/// Uses an LLM producer when available; otherwise falls back to a single-node graph
/// (the caller handles the "do the goal" step).
/// </summary>
public sealed class Planner
{
    private readonly PlanProducer? _producer;

    public Planner(PlanProducer? producer = null)
    {
        _producer = producer;
    }

    public async Task<TaskGraph> PlanToGraphAsync(Plan plan, CancellationToken ct = default)
    {
        var builder = new TaskGraphBuilder();
        foreach (var step in plan.Steps)
        {
            var s = step; // capture
            builder.Add(s.Id, s.Title, async c =>
            {
                // Default execute is a no-op marker. Real step execution happens in
                // the orchestrator (e.g. AgentRunner) by hooking into TaskNode.
                await Task.CompletedTask;
                return s.Description ?? s.Title;
            }, s.DependsOn.ToArray(), s.Description);
        }
        return await Task.FromResult(builder.Build());
    }

    /// <summary>
    /// Decompose a free-form goal string into a Plan. If an LLM producer is configured
    /// and reachable, use it. Otherwise, return a single-step plan (the goal itself).
    /// </summary>
    public async Task<Plan> DecomposeAsync(string goal, CancellationToken ct = default)
    {
        if (_producer is null) return SingleStep(goal);
        try
        {
            var plan = await _producer(goal, ct);
            if (plan.Steps.Count == 0) return SingleStep(goal);
            return plan;
        }
        catch (Exception ex)
        {
            return new Plan
            {
                Goal = goal,
                Steps = new[] {
                    new PlanStep { Id = "fallback", Title = "Execute goal directly",
                        Description = $"LLM planner failed ({ex.Message}); running goal as a single step." }
                },
                SourcePrompt = goal
            };
        }
    }

    private static Plan SingleStep(string goal) => new()
    {
        Goal = goal,
        Steps = new[] { new PlanStep { Id = "do-it", Title = goal, Description = goal } }
    };

    /// <summary>
    /// Build an LLM producer that asks the model to emit JSON describing the plan.
    /// Falls back to deterministic parse if the model output is malformed.
    /// </summary>
    public static PlanProducer FromLlm(Func<string, CancellationToken, Task<string>> ask)
    {
        return async (goal, ct) =>
        {
            var sysPrompt = "You are a senior engineer. Given the goal, output a JSON plan with 2-7 steps. Format: " +
                             "{\"goal\":\"<goal>\",\"steps\":[{\"id\":\"s1\",\"title\":\"...\",\"description\":\"...\",\"dependsOn\":[],\"kind\":\"shell|code|read|write|web|analyze\"}, ...]}. " +
                             "Rules: id must be unique, dependsOn must reference earlier ids only, " +
                             "at most one root step, do NOT execute the goal yourself only plan it. Output JSON only, no other text.";
            var userPrompt = $"Goal: {goal}\n\nOutput JSON plan:";
            var raw = await ask(sysPrompt + "\n\n" + userPrompt, ct);
            return ParsePlan(goal, raw);
        };
    }

    /// <summary>Best-effort parse of LLM JSON output. Handles ```json fences and trailing prose.</summary>
    public static Plan ParsePlan(string goal, string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return SingleStep(goal);
        var text = raw.Trim();
        if (text.StartsWith("```"))
        {
            var s = text.IndexOf('{');
            var e = text.LastIndexOf('}');
            if (s >= 0 && e > s) text = text[s..(e + 1)];
        }
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var steps = new List<PlanStep>();
            if (root.TryGetProperty("steps", out var stepsEl) && stepsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in stepsEl.EnumerateArray())
                {
                    var id = s.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? Guid.NewGuid().ToString("N").Substring(0, 4) : Guid.NewGuid().ToString("N").Substring(0, 4);
                    var title = s.TryGetProperty("title", out var tEl) ? tEl.GetString() ?? "step" : "step";
                    var desc = s.TryGetProperty("description", out var dEl) ? dEl.GetString() : null;
                    var kind = s.TryGetProperty("kind", out var kEl) ? kEl.GetString() : null;
                    var deps = new List<string>();
                    if (s.TryGetProperty("dependsOn", out var dpEl) && dpEl.ValueKind == JsonValueKind.Array)
                        foreach (var d in dpEl.EnumerateArray()) deps.Add(d.GetString() ?? "");
                    steps.Add(new PlanStep { Id = id, Title = title, Description = desc, DependsOn = deps, Kind = kind });
                }
            }
            if (steps.Count == 0) return SingleStep(goal);
            return new Plan { Goal = goal, Steps = steps, SourcePrompt = raw };
        }
        catch
        {
            // Try to salvage: split on numbered lines "1. step\n2. step"
            var salvaged = new List<PlanStep>();
            var rx = new Regex(@"^\s*(\d+)\.\s+(.+)$", RegexOptions.Multiline);
            foreach (Match m in rx.Matches(raw))
            {
                salvaged.Add(new PlanStep { Id = $"s{salvaged.Count + 1}", Title = m.Groups[2].Value.Trim() });
            }
            if (salvaged.Count == 0) return SingleStep(goal);
            return new Plan { Goal = goal, Steps = salvaged, SourcePrompt = raw };
        }
    }
}
