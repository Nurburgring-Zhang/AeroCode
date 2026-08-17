// Copyright (c) AeroCode V3.0
// TaskGraph — DAG of tasks for Harness (DeepSeek Harness "Cordis KMap" style).
// Each node is a Task with dependencies. Topological execution with parallel lanes
// for independent nodes. Replaces "manual step ordering" with explicit graph.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AeroCode.Harness.Graph;

/// <summary>Status of a task in the graph.</summary>
public enum TaskState
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Skipped,
    Cancelled
}

/// <summary>A single node in the task graph.</summary>
public sealed class TaskNode
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    /// <summary>Node IDs that must succeed before this node runs.</summary>
    public IReadOnlyList<string> DependsOn { get; init; } = Array.Empty<string>();
    public TaskState State { get; set; } = TaskState.Pending;
    public string? Error { get; set; }
    public TimeSpan Duration { get; set; }
    public string? Result { get; set; }

    /// <summary>The actual work to do. Async, may throw.</summary>
    public Func<CancellationToken, Task<string>>? Execute { get; set; }
}

/// <summary>Builder for a TaskGraph — fluent API.</summary>
public sealed class TaskGraphBuilder
{
    private readonly Dictionary<string, TaskNode> _nodes = new();

    public TaskGraphBuilder Add(string id, string name, Func<CancellationToken, Task<string>> exec,
        string[]? dependsOn = null, string? description = null)
    {
        _nodes[id] = new TaskNode
        {
            Id = id,
            Name = name,
            Description = description,
            DependsOn = dependsOn ?? Array.Empty<string>(),
            Execute = exec
        };
        return this;
    }

    public TaskGraph Build()
    {
        // Validate: every dependency must reference an existing node.
        foreach (var n in _nodes.Values)
            foreach (var d in n.DependsOn)
                if (!_nodes.ContainsKey(d))
                    throw new InvalidOperationException($"Task '{n.Id}' depends on missing node '{d}'");

        // Validate: no cycles (simple DFS)
        var state = new Dictionary<string, int>(); // 0=unvisited, 1=in-stack, 2=done
        void Visit(string id, Stack<string> stack)
        {
            if (state.TryGetValue(id, out var s))
            {
                if (s == 1) throw new InvalidOperationException($"Cycle detected: {string.Join(" -> ", stack.Reverse())} -> {id}");
                return;
            }
            state[id] = 1;
            stack.Push(id);
            foreach (var d in _nodes[id].DependsOn) Visit(d, stack);
            stack.Pop();
            state[id] = 2;
        }
        foreach (var id in _nodes.Keys) Visit(id, new Stack<string>());

        return new TaskGraph(_nodes);
    }
}

/// <summary>
/// DAG of tasks. Topological execution: tasks with no remaining dependencies run in parallel.
/// </summary>
public sealed class TaskGraph
{
    private readonly Dictionary<string, TaskNode> _nodes;

    public IReadOnlyDictionary<string, TaskNode> Nodes => _nodes;

    public TaskGraph(Dictionary<string, TaskNode> nodes)
    {
        _nodes = nodes;
        // Validate cycle (same DFS the Builder does, but in case caller bypasses it).
        var state = new Dictionary<string, int>();
        void Visit(string id, Stack<string> stack)
        {
            if (state.TryGetValue(id, out var s))
            {
                if (s == 1) throw new InvalidOperationException($"Cycle detected: {string.Join(" -> ", stack.Reverse())} -> {id}");
                return;
            }
            state[id] = 1;
            stack.Push(id);
            foreach (var d in _nodes[id].DependsOn) Visit(d, stack);
            stack.Pop();
            state[id] = 2;
        }
        foreach (var id in _nodes.Keys) Visit(id, new Stack<string>());
    }

    /// <summary>
    /// Execute all tasks in topological order, parallelising independent tasks within each layer.
    /// Returns when all tasks finish (success / failure / skip). Stops on first uncaught error
    /// unless <paramref name="continueOnError"/> is true.
    /// </summary>
    public async Task<GraphResult> ExecuteAsync(CancellationToken ct = default, bool continueOnError = false)
    {
        var startedAt = DateTime.UtcNow;
        var pending = _nodes.Values.Where(n => n.State == TaskState.Pending).ToList();
        while (pending.Count > 0)
        {
            // Find all tasks whose dependencies are all done (succeeded or skipped-or-failed-if-continueOnError).
            var ready = pending
                .Where(n => n.DependsOn.All(d =>
                {
                    var dep = _nodes[d].State;
                    return dep == TaskState.Succeeded || dep == TaskState.Skipped ||
                           (continueOnError && dep == TaskState.Failed);
                }))
                .ToList();
            if (ready.Count == 0) break; // no progress possible

            // Launch all ready tasks in parallel
            var tasks = ready.Select(n => RunOne(n, ct)).ToList();
            await Task.WhenAll(tasks);

            pending = _nodes.Values.Where(n => n.State == TaskState.Pending).ToList();
        }

        // Mark any remaining as skipped ONLY when continueOnError=true (cancelled-by-error is the user's choice).
        // With continueOnError=false (default), leave them Pending so callers can tell "blocked by upstream failure".
        if (continueOnError)
        {
            foreach (var n in _nodes.Values.Where(x => x.State == TaskState.Pending))
                n.State = TaskState.Skipped;
        }

        return new GraphResult
        {
            StartedAt = startedAt,
            FinishedAt = DateTime.UtcNow,
            Nodes = _nodes.Values.ToList(),
            AllSucceeded = _nodes.Values.All(n => n.State == TaskState.Succeeded)
        };
    }

    private async Task RunOne(TaskNode n, CancellationToken ct)
    {
        n.State = TaskState.Running;
        var t0 = DateTime.UtcNow;
        try
        {
            if (n.Execute is null) { n.State = TaskState.Skipped; n.Error = "no execute"; return; }
            var result = await n.Execute(ct);
            n.Result = result;
            n.State = TaskState.Succeeded;
        }
        catch (OperationCanceledException) { n.State = TaskState.Cancelled; }
        catch (Exception ex) { n.State = TaskState.Failed; n.Error = ex.Message; }
        finally { n.Duration = DateTime.UtcNow - t0; }
    }

    /// <summary>Render the graph as ASCII for logs.</summary>
    public string ToAscii()
    {
        var sb = new StringBuilder();
        sb.AppendLine("TaskGraph:");
        foreach (var n in _nodes.Values.OrderBy(x => x.Id))
        {
            var deps = n.DependsOn.Count == 0 ? "(no deps)" : "← " + string.Join(", ", n.DependsOn);
            sb.AppendLine($"  [{n.State,-9}] {n.Id} :: {n.Name}  {deps}");
        }
        return sb.ToString();
    }
}

public sealed class GraphResult
{
    public DateTime StartedAt { get; init; }
    public DateTime FinishedAt { get; init; }
    public IReadOnlyList<TaskNode> Nodes { get; init; } = Array.Empty<TaskNode>();
    public bool AllSucceeded { get; init; }
    public TimeSpan Total => FinishedAt - StartedAt;
}
