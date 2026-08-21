// ExpertPool tests — registration/lookup/listing, memory append/load/snapshot,
// real JSON persistence on disk (reload by a fresh pool) and thread safety.
// All data paths point at a per-test temp directory (IDisposable cleanup).
using AeroAgent.Autonomy.Cluster;
using AeroAgent.Autonomy.Data;
using Xunit;

namespace AeroCode.Tests.Autonomy.Cluster;

public sealed class ClusterExpertPoolTests : IDisposable
{
    private readonly string _root;
    private readonly AutonomyDataPaths _paths;

    public ClusterExpertPoolTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "aerocode-cluster-pool-" + Guid.NewGuid().ToString("N"));
        _paths = new AutonomyDataPaths(_root);
        _paths.EnsureDirectories();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort temp cleanup; never fail a test on cleanup.
        }
    }

    private ExpertPool NewPool() => new(_paths);

    [Fact]
    public void RegisterExpert_ReturnsHandle_WithStableIdSessionAndTrimmedRole()
    {
        var pool = NewPool();

        var handle = pool.RegisterExpert("  backend engineer  ", "负责订单服务");

        Assert.StartsWith("expert-", handle.Id);
        Assert.StartsWith("expert-session-", handle.SessionId);
        Assert.Equal("backend engineer", handle.Role);
        Assert.Equal("负责订单服务", handle.Description);
        Assert.NotEqual(handle.Id, handle.SessionId);
        Assert.Equal(1, pool.Count);
    }

    [Fact]
    public void RegisterExpert_EmptyRole_Throws()
    {
        var pool = NewPool();
        Assert.Throws<ArgumentException>(() => pool.RegisterExpert("   "));
    }

    [Fact]
    public void GetExpert_UnknownId_ReturnsNull()
    {
        var pool = NewPool();
        Assert.Null(pool.GetExpert("expert-does-not-exist"));
    }

    [Fact]
    public void ListExperts_ReturnsRegisteredExperts_WithDistinctIds()
    {
        var pool = NewPool();
        var h1 = pool.RegisterExpert("架构师");
        var h2 = pool.RegisterExpert("测试工程师");
        var h3 = pool.RegisterExpert("前端工程师");

        var listed = pool.ListExperts();

        Assert.Equal(3, listed.Count);
        Assert.Equal(new[] { h1.Id, h2.Id, h3.Id }.ToHashSet(), listed.Select(e => e.Id).ToHashSet());
        Assert.Equal("架构师", listed.Single(e => e.Id == h1.Id).Role);
    }

    [Fact]
    public void AppendMemory_ThenLoadMemory_ReturnsEntriesInAppendOrder()
    {
        var pool = NewPool();
        var expert = pool.RegisterExpert("数据工程师");

        pool.AppendMemory(expert.Id, "cluster", "第一条记忆");
        pool.AppendMemory(expert.Id, "lesson", "第二条记忆");
        var returned = pool.AppendMemory(expert.Id, "cluster", "第三条记忆");

        var loaded = pool.LoadMemory(expert.Id);
        Assert.Equal(3, loaded.Count);
        Assert.Equal("第一条记忆", loaded[0].Content);
        Assert.Equal("cluster", loaded[0].Kind);
        Assert.Equal("第二条记忆", loaded[1].Content);
        Assert.Equal("lesson", loaded[1].Kind);
        Assert.Equal("第三条记忆", loaded[2].Content);
        Assert.Equal("cluster", returned.Kind);
        Assert.True(loaded[0].AtUtc <= loaded[2].AtUtc);
    }

    [Fact]
    public void AppendMemory_UnknownExpert_Throws()
    {
        var pool = NewPool();
        var ex = Assert.Throws<ArgumentException>(() => pool.AppendMemory("expert-ghost", "cluster", "x"));
        Assert.Contains("expert-ghost", ex.Message);
    }

    [Fact]
    public void LoadMemory_UnknownExpert_Throws()
    {
        var pool = NewPool();
        Assert.Throws<ArgumentException>(() => pool.LoadMemory("expert-ghost"));
    }

    [Fact]
    public void BuildMemorySnapshot_RendersRecentEntries_WithKindAndContent()
    {
        var pool = NewPool();
        var expert = pool.RegisterExpert("全栈工程师");
        pool.AppendMemory(expert.Id, "cluster", "旧记忆一");
        pool.AppendMemory(expert.Id, "lesson", "旧记忆二");
        pool.AppendMemory(expert.Id, "cluster", "最新记忆");

        var snapshot = pool.BuildMemorySnapshot(expert.Id, maxEntries: 10);

        Assert.Contains("(cluster) 旧记忆一", snapshot);
        Assert.Contains("(lesson) 旧记忆二", snapshot);
        Assert.Contains("(cluster) 最新记忆", snapshot);
        Assert.StartsWith("- [", snapshot);
        Assert.Equal(3, snapshot.Split('\n').Length); // 每条记忆一行，结尾无多余空行
    }

    [Fact]
    public void BuildMemorySnapshot_RespectsMaxEntries_KeepingTheMostRecent()
    {
        var pool = NewPool();
        var expert = pool.RegisterExpert("工程师");
        pool.AppendMemory(expert.Id, "cluster", "one");
        pool.AppendMemory(expert.Id, "cluster", "two");
        pool.AppendMemory(expert.Id, "cluster", "three");

        var snapshot = pool.BuildMemorySnapshot(expert.Id, maxEntries: 2);

        Assert.DoesNotContain("one", snapshot);
        Assert.Contains("(cluster) two", snapshot);
        Assert.Contains("(cluster) three", snapshot);
    }

    [Fact]
    public void BuildMemorySnapshot_NoMemoryOrZeroMax_ReturnsEmpty()
    {
        var pool = NewPool();
        var expert = pool.RegisterExpert("工程师");
        pool.AppendMemory(expert.Id, "cluster", "有内容");

        Assert.Equal(string.Empty, pool.BuildMemorySnapshot(expert.Id, maxEntries: 0));
        var fresh = pool.RegisterExpert("另一位");
        Assert.Equal(string.Empty, pool.BuildMemorySnapshot(fresh.Id, maxEntries: 5));
    }

    [Fact]
    public void Persistence_RealJsonFileOnDisk_IsReloadedByFreshPool()
    {
        var pool = NewPool();
        var expert = pool.RegisterExpert("持久化验证专家", "desc-1");
        pool.AppendMemory(expert.Id, "cluster", "落盘记忆内容");

        // The per-expert JSON file must really exist and contain the data. Note: the
        // default JSON encoder escapes non-ASCII text to \uXXXX, so Chinese content is
        // verified through JsonDocument rather than raw substring search.
        var file = Path.Combine(pool.ExpertsDirectory, expert.Id + ".json");
        Assert.True(File.Exists(file), $"expert file missing: {file}");
        var raw = File.ReadAllText(file);
        Assert.Contains(expert.Id, raw);
        Assert.Contains(expert.SessionId, raw);
        using (var doc = System.Text.Json.JsonDocument.Parse(raw))
        {
            Assert.Equal("持久化验证专家", doc.RootElement.GetProperty("Profile").GetProperty("Role").GetString());
            var memoryArray = doc.RootElement.GetProperty("Memory");
            Assert.Equal(1, memoryArray.GetArrayLength());
            Assert.Equal("落盘记忆内容", memoryArray[0].GetProperty("Content").GetString());
        }

        // A brand-new pool over the same data root must restore expert + memory.
        var pool2 = NewPool();
        Assert.Equal(1, pool2.Count);
        var reloaded = pool2.GetExpert(expert.Id);
        Assert.NotNull(reloaded);
        Assert.Equal("持久化验证专家", reloaded!.Role);
        Assert.Equal(expert.SessionId, reloaded.SessionId);
        var memory = pool2.LoadMemory(expert.Id);
        Assert.Single(memory);
        Assert.Equal("落盘记忆内容", memory[0].Content);
        Assert.Equal("cluster", memory[0].Kind);
    }

    [Fact]
    public void Persistence_CorruptedExpertFile_IsSkippedWithoutFailingThePool()
    {
        var pool = NewPool();
        var valid = pool.RegisterExpert("正常专家");
        File.WriteAllText(Path.Combine(pool.ExpertsDirectory, "expert-corrupted.json"), "{ this is not json");

        var pool2 = NewPool();

        Assert.Equal(1, pool2.Count);
        Assert.NotNull(pool2.GetExpert(valid.Id));
    }

    [Fact]
    public void ConcurrentRegistration_AllExpertsRegisteredAndPersisted()
    {
        var pool = NewPool();

        Parallel.For(0, 20, i => pool.RegisterExpert($"role-{i}"));

        Assert.Equal(20, pool.Count);
        var ids = pool.ListExperts().Select(e => e.Id).ToList();
        Assert.Equal(20, ids.Distinct().Count());
        Assert.All(ids, id => Assert.StartsWith("expert-", id));
        var files = Directory.GetFiles(pool.ExpertsDirectory, "*.json");
        Assert.Equal(20, files.Length);
    }

    [Fact]
    public void ConcurrentAppendMemory_NoEntryLost()
    {
        var pool = NewPool();
        var expert = pool.RegisterExpert("并发记忆专家");

        Parallel.For(0, 10, i => pool.AppendMemory(expert.Id, "cluster", $"entry-{i}"));

        var loaded = pool.LoadMemory(expert.Id);
        Assert.Equal(10, loaded.Count);
        Assert.Equal(
            Enumerable.Range(0, 10).Select(i => $"entry-{i}").ToHashSet(),
            loaded.Select(e => e.Content).ToHashSet());
    }
}
