// Copyright (c) AeroCode V3.0
// ClusterPlan — validated branch DAG consumed by the ClusterScheduler. Plans are built
// either from an explicit branch list or by mapping the topology of an existing
// AeroCode.Harness.Graph.TaskGraph (reuse, not reinvention).
using System;
using System.Collections.Generic;
using System.Linq;
using AeroCode.Harness.Graph;

namespace AeroAgent.Autonomy.Cluster;

/// <summary>
/// An immutable, validated cluster plan: a DAG of branch specs. Construction validates
/// uniqueness, dependency existence, acyclicity and per-branch invariants; invalid
/// inputs throw at build time, never mid-run.
/// </summary>
public sealed class ClusterPlan
{
    /// <summary>The branch specs in registration order.</summary>
    public IReadOnlyList<ClusterBranchSpec> Branches { get; }

    private ClusterPlan(IReadOnlyList<ClusterBranchSpec> branches)
    {
        Branches = branches;
    }

    /// <summary>Build a plan from an explicit branch list (validated).</summary>
    /// <exception cref="ArgumentException">The list is empty or a branch is malformed.</exception>
    /// <exception cref="InvalidOperationException">Dependencies are missing or form a cycle.</exception>
    public static ClusterPlan FromBranches(IEnumerable<ClusterBranchSpec> branches)
    {
        ArgumentNullException.ThrowIfNull(branches);
        var list = branches.ToList();
        Validate(list);
        return new ClusterPlan(list);
    }

    /// <summary>
    /// Build a plan from an existing <see cref="TaskGraph"/> by mapping its topology:
    /// node id/name/dependencies are carried over and the node description (or name when
    /// no description exists) becomes the expert task text. The graph's own Execute
    /// delegates are intentionally not used — cluster work is executed by pool experts.
    /// </summary>
    /// <exception cref="ArgumentException">The graph contains no nodes or a node is malformed.</exception>
    public static ClusterPlan FromTaskGraph(TaskGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var specs = graph.Nodes.Values
            .OrderBy(n => n.Id, StringComparer.Ordinal)
            .Select(n => new ClusterBranchSpec
            {
                Id = n.Id,
                Name = n.Name,
                TaskText = string.IsNullOrWhiteSpace(n.Description) ? n.Name : n.Description!,
                DependsOn = n.DependsOn.ToList(),
            })
            .ToList();
        Validate(specs);
        return new ClusterPlan(specs);
    }

    private static void Validate(List<ClusterBranchSpec> branches)
    {
        if (branches.Count == 0)
        {
            throw new ArgumentException("Cluster plan must contain at least one branch.", nameof(branches));
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var branch in branches)
        {
            if (string.IsNullOrWhiteSpace(branch.Id))
            {
                throw new ArgumentException("Every cluster branch needs a non-empty id.", nameof(branches));
            }

            if (!ids.Add(branch.Id))
            {
                throw new ArgumentException($"Duplicate cluster branch id '{branch.Id}'.", nameof(branches));
            }

            if (string.IsNullOrWhiteSpace(branch.TaskText))
            {
                throw new ArgumentException($"Cluster branch '{branch.Id}' has an empty task text.", nameof(branches));
            }

            if (branch.FanOutCount < 1)
            {
                throw new ArgumentException($"Cluster branch '{branch.Id}' fan-out count must be >= 1.", nameof(branches));
            }
        }

        foreach (var branch in branches)
        {
            foreach (var dep in branch.DependsOn)
            {
                if (!ids.Contains(dep))
                {
                    throw new InvalidOperationException(
                        $"Cluster branch '{branch.Id}' depends on unknown branch '{dep}'.");
                }
            }
        }

        AssertAcyclic(branches);
    }

    /// <summary>DFS cycle detection (three-state marking), same approach as TaskGraph.</summary>
    private static void AssertAcyclic(List<ClusterBranchSpec> branches)
    {
        var byId = branches.ToDictionary(b => b.Id, StringComparer.Ordinal);
        var state = new Dictionary<string, int>(StringComparer.Ordinal); // 0=unvisited, 1=in-stack, 2=done

        void Visit(string id, Stack<string> stack)
        {
            if (state.TryGetValue(id, out var s))
            {
                if (s == 1)
                {
                    throw new InvalidOperationException(
                        $"Cluster plan contains a dependency cycle: {string.Join(" -> ", stack.Reverse())} -> {id}");
                }

                return;
            }

            state[id] = 1;
            stack.Push(id);
            foreach (var dep in byId[id].DependsOn)
            {
                Visit(dep, stack);
            }

            stack.Pop();
            state[id] = 2;
        }

        foreach (var branch in branches)
        {
            Visit(branch.Id, new Stack<string>());
        }
    }
}
