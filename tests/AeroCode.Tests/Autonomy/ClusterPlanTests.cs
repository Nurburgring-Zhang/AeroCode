// ClusterPlan tests — FromBranches validation (duplicates, missing deps, cycles,
// empty/malformed branches) and FromTaskGraph topology mapping.
using AeroAgent.Autonomy.Cluster;
using AeroCode.Harness.Graph;
using Xunit;

namespace AeroCode.Tests.Autonomy.Cluster;

public sealed class ClusterPlanTests
{
    private static ClusterBranchSpec Branch(
        string id, string task = "任务文本", string[]? dependsOn = null, int fanOut = 1) => new()
        {
            Id = id,
            TaskText = task,
            DependsOn = dependsOn ?? Array.Empty<string>(),
            FanOutCount = fanOut,
        };

    [Fact]
    public void FromBranches_EmptyList_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() => ClusterPlan.FromBranches(Array.Empty<ClusterBranchSpec>()));
        Assert.Contains("at least one branch", ex.Message);
    }

    [Fact]
    public void FromBranches_NullList_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => ClusterPlan.FromBranches(null!));
    }

    [Fact]
    public void FromBranches_DuplicateId_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() => ClusterPlan.FromBranches(new[]
        {
            Branch("a"), Branch("a"),
        }));
        Assert.Contains("Duplicate cluster branch id 'a'", ex.Message);
    }

    [Fact]
    public void FromBranches_EmptyId_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() => ClusterPlan.FromBranches(new[] { Branch("  ") }));
        Assert.Contains("non-empty id", ex.Message);
    }

    [Fact]
    public void FromBranches_EmptyTaskText_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() => ClusterPlan.FromBranches(new[] { Branch("a", task: "  ") }));
        Assert.Contains("'a' has an empty task text", ex.Message);
    }

    [Fact]
    public void FromBranches_FanOutCountBelowOne_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() => ClusterPlan.FromBranches(new[] { Branch("a", fanOut: 0) }));
        Assert.Contains("fan-out count must be >= 1", ex.Message);
    }

    [Fact]
    public void FromBranches_UnknownDependency_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ClusterPlan.FromBranches(new[]
        {
            Branch("a", dependsOn: new[] { "ghost" }),
        }));
        Assert.Contains("'a' depends on unknown branch 'ghost'", ex.Message);
    }

    [Fact]
    public void FromBranches_DependencyCycle_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ClusterPlan.FromBranches(new[]
        {
            Branch("a", dependsOn: new[] { "b" }),
            Branch("b", dependsOn: new[] { "a" }),
        }));
        Assert.Contains("cycle", ex.Message);
    }

    [Fact]
    public void FromBranches_SelfDependency_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => ClusterPlan.FromBranches(new[]
        {
            Branch("a", dependsOn: new[] { "a" }),
        }));
    }

    [Fact]
    public void FromBranches_ValidPlan_PreservesOrderIdsTasksAndDependencies()
    {
        var plan = ClusterPlan.FromBranches(new[]
        {
            Branch("a", task: "任务A"),
            Branch("b", task: "任务B"),
            Branch("c", task: "任务C", dependsOn: new[] { "a", "b" }, fanOut: 2),
        });

        Assert.Equal(new[] { "a", "b", "c" }, plan.Branches.Select(b => b.Id).ToArray());
        Assert.Equal("任务A", plan.Branches[0].TaskText);
        Assert.Equal(new[] { "a", "b" }, plan.Branches[2].DependsOn.ToArray());
        Assert.Equal(2, plan.Branches[2].FanOutCount);
    }

    [Fact]
    public void FromTaskGraph_MapsTopology_DescriptionFallsBackToName()
    {
        var graph = new TaskGraphBuilder()
            .Add("n2", "第二个节点", _ => Task.FromResult("unused"), description: "节点二的详细描述")
            .Add("n1", "第一个节点", _ => Task.FromResult("unused"))
            .Add("n3", "第三个节点", _ => Task.FromResult("unused"), dependsOn: new[] { "n1", "n2" })
            .Build();

        var plan = ClusterPlan.FromTaskGraph(graph);

        // Ordered by node id (ordinal), topology carried over.
        Assert.Equal(new[] { "n1", "n2", "n3" }, plan.Branches.Select(b => b.Id).ToArray());
        Assert.Equal("第一个节点", plan.Branches[0].Name);
        Assert.Equal("第一个节点", plan.Branches[0].TaskText); // no description → name
        Assert.Equal("节点二的详细描述", plan.Branches[1].TaskText); // description wins
        Assert.Equal(new[] { "n1", "n2" }, plan.Branches[2].DependsOn.ToArray());
    }

    [Fact]
    public void FromTaskGraph_NullGraph_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => ClusterPlan.FromTaskGraph(null!));
    }
}
