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

/// <summary>
/// ACS 节点契约（E15：节点 = 边界清晰的工作单元；契约强制 inputs/outputs/acceptance）。
/// </summary>
public sealed record NodeContract(
    IReadOnlyList<string> Inputs,
    IReadOnlyList<string> Outputs,
    string Acceptance);

/// <summary>
/// ACS 边门（E19：验证门挂在边上；kind=cmd 必须写 expect_exit，否则无法判成败）。
/// </summary>
public sealed record EdgeGate(string Kind, string Command, int ExpectExit);

/// <summary>ACS barrier 汇聚语义（E20：四种显式声明，隐式 all_success 即故障温床）。</summary>
public enum BarrierPolicy
{
    /// <summary>全部上游成功才放行。</summary>
    AllSuccess,

    /// <summary>任一上游成功即放行。</summary>
    AnySuccess,

    /// <summary>≥MinCount 个上游成功即放行。</summary>
    MinSuccess,

    /// <summary>全部上游完成（不论成败）即放行。</summary>
    AllDone,
}

/// <summary>ACS barrier 配置（MinSuccess 时 MinCount 合法范围 1 ≤ MinCount ≤ 上游数）。</summary>
public sealed record BarrierConfig(BarrierPolicy Policy, int MinCount);

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

    /// <summary>ACS 节点契约（可选；T3 强制）。</summary>
    public NodeContract? Contract { get; init; }

    /// <summary>ACS 模型分层登记（E26：无聊节点跑便宜模型、判断节点跑强模型；计量字段）。</summary>
    public string? ModelTier { get; init; }

    /// <summary>ACS 入边门（可选；上游产出放行本节点前过门）。</summary>
    public EdgeGate? EdgeGate { get; init; }

    /// <summary>ACS barrier 汇聚语义（可选；多上游时显式声明汇聚策略）。</summary>
    public BarrierConfig? Barrier { get; init; }
}

/// <summary>Builder for a TaskGraph — fluent API.</summary>
public sealed class TaskGraphBuilder
{
    private readonly Dictionary<string, TaskNode> _nodes = new();

    public TaskGraphBuilder Add(string id, string name, Func<CancellationToken, Task<string>> exec,
        string[]? dependsOn = null, string? description = null,
        NodeContract? contract = null, string? modelTier = null,
        EdgeGate? edgeGate = null, BarrierConfig? barrier = null)
    {
        _nodes[id] = new TaskNode
        {
            Id = id,
            Name = name,
            Description = description,
            DependsOn = dependsOn ?? Array.Empty<string>(),
            Execute = exec,
            Contract = contract,
            ModelTier = modelTier,
            EdgeGate = edgeGate,
            Barrier = barrier
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

        // ACS E15: 节点契约若声明，inputs/outputs/acceptance 不可为空。
        foreach (var n in _nodes.Values)
        {
            if (n.Contract is { } c)
            {
                if (c.Inputs.Count == 0 || c.Outputs.Count == 0 || string.IsNullOrWhiteSpace(c.Acceptance))
                    throw new InvalidOperationException(
                        $"Task '{n.Id}' contract incomplete: inputs/outputs/acceptance all required (E15)");
            }

            // ACS E19: kind=cmd 边门必须写 expect_exit 与非空命令。
            if (n.EdgeGate is { } g && g.Kind == "cmd" &&
                (string.IsNullOrWhiteSpace(g.Command)))
            {
                throw new InvalidOperationException(
                    $"Task '{n.Id}' edge gate kind=cmd requires non-empty Command (E19)");
            }

            // ACS E20: MinSuccess barrier 的 MinCount 合法范围 1 ≤ MinCount ≤ 上游数。
            if (n.Barrier is { Policy: BarrierPolicy.MinSuccess } b)
            {
                if (b.MinCount < 1 || b.MinCount > n.DependsOn.Count)
                    throw new InvalidOperationException(
                        $"Task '{n.Id}' barrier MinSuccess MinCount={b.MinCount} out of range [1, {n.DependsOn.Count}] (E20)");
            }
        }

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
            // Find all tasks whose dependencies are satisfied per their barrier policy
            // (default = AllSuccess semantics, the pre-ACS behavior).
            var ready = pending.Where(n => IsReady(n, continueOnError)).ToList();
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

    /// <summary>
    /// ACS E20 barrier 感知就绪判定：有 Barrier 按声明策略，无 Barrier 用默认 AllSuccess 语义
    /// （上游全部成功/跳过，或 continueOnError 时失败也算）。
    /// </summary>
    private bool IsReady(TaskNode n, bool continueOnError)
    {
        if (n.DependsOn.Count == 0) return true;

        var depStates = n.DependsOn.Select(d => _nodes[d].State).ToList();
        // 上游尚有未决（Pending/Running）→ 一律不就绪
        if (depStates.Any(s => s == TaskState.Pending || s == TaskState.Running)) return false;

        var succeeded = depStates.Count(s => s == TaskState.Succeeded);
        var done = depStates.Count(s => s is TaskState.Succeeded or TaskState.Failed or TaskState.Skipped or TaskState.Cancelled);

        return n.Barrier switch
        {
            { Policy: BarrierPolicy.AllSuccess } => depStates.All(s =>
                s == TaskState.Succeeded || s == TaskState.Skipped || (continueOnError && s == TaskState.Failed)),
            { Policy: BarrierPolicy.AnySuccess } => succeeded >= 1,
            { Policy: BarrierPolicy.MinSuccess } b => succeeded >= b.MinCount,
            { Policy: BarrierPolicy.AllDone } => done == depStates.Count,
            _ => depStates.All(s =>
                s == TaskState.Succeeded || s == TaskState.Skipped || (continueOnError && s == TaskState.Failed)),
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
