// Copyright (c) AeroCode V3.0
// JsonPermissionStore tests — real temp files, no mocks.
using System;
using System.IO;
using System.Threading.Tasks;
using AeroCode.Harness.Permission;
using Xunit;

namespace AeroCode.Tests.HarnessTests;

/// <summary>
/// 权限决策持久化的读写回环与容错语义：保存→新实例重读一致；
/// 缺失/损坏/未知枚举值 → 诚实回退空配置；原子覆盖写；枚举按可读字符串落盘。
/// </summary>
public sealed class JsonPermissionStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public JsonPermissionStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"perm_store_{Guid.NewGuid():N}");
        _path = Path.Combine(_dir, "permissions.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public async Task RoundTrip_PreservesAllThreeDecisions()
    {
        var store = new JsonPermissionStore(_path);
        var settings = new PermissionSettings();
        settings.ToolDecisions["create_note"] = PermissionDecision.Allow;
        settings.ToolDecisions["run_shell"] = PermissionDecision.Deny;
        settings.ToolDecisions["mcp_external_tool"] = PermissionDecision.Ask;
        await store.SaveAsync(settings);

        // 用新实例读——验证真正走了磁盘而非内存缓存
        var loaded = await new JsonPermissionStore(_path).LoadAsync();
        Assert.Equal(3, loaded.ToolDecisions.Count);
        Assert.Equal(PermissionDecision.Allow, loaded.ToolDecisions["create_note"]);
        Assert.Equal(PermissionDecision.Deny, loaded.ToolDecisions["run_shell"]);
        Assert.Equal(PermissionDecision.Ask, loaded.ToolDecisions["mcp_external_tool"]);
    }

    [Fact]
    public async Task HumanReadable_EnumsSerializedAsStrings()
    {
        var store = new JsonPermissionStore(_path);
        var settings = new PermissionSettings();
        settings.ToolDecisions["read_file"] = PermissionDecision.Allow;
        await store.SaveAsync(settings);

        var raw = await File.ReadAllTextAsync(_path);
        Assert.Contains("\"Allow\"", raw);          // permissions.json 是用户可查看的配置
        Assert.DoesNotContain("read_file\": 0", raw); // 不是数字枚举
    }

    [Fact]
    public async Task MissingFile_ReturnsEmptySettings()
    {
        var loaded = await new JsonPermissionStore(_path).LoadAsync();
        Assert.Empty(loaded.ToolDecisions);
    }

    [Fact]
    public async Task CorruptFile_ReturnsEmptySettings_NotCrash()
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(_path, "{这不是 JSON");

        var loaded = await new JsonPermissionStore(_path).LoadAsync();
        Assert.Empty(loaded.ToolDecisions);
    }

    [Fact]
    public async Task UnknownEnumValue_FallsBackToEmpty()
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(_path,
            """{"ToolDecisions":{"read_file":"Banana"}}""");

        var loaded = await new JsonPermissionStore(_path).LoadAsync();
        Assert.Empty(loaded.ToolDecisions);
    }

    [Fact]
    public async Task NullToolDecisions_FallsBackToEmptyDictionary_NotNull()
    {
        // {"ToolDecisions":null} 是合法 JSON（不触发 JsonException）：
        // 必须兜底为空字典，否则启动期遍历 NRE。
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(_path, """{"ToolDecisions":null}""");

        var loaded = await new JsonPermissionStore(_path).LoadAsync();
        Assert.NotNull(loaded.ToolDecisions);
        Assert.Empty(loaded.ToolDecisions);
    }

    [Fact]
    public async Task SaveTwice_SecondWins_NoLeftoverTmpFiles()
    {
        var store = new JsonPermissionStore(_path);
        var first = new PermissionSettings();
        first.ToolDecisions["a_tool"] = PermissionDecision.Allow;
        await store.SaveAsync(first);

        var second = new PermissionSettings();
        second.ToolDecisions["b_tool"] = PermissionDecision.Deny;
        await store.SaveAsync(second);

        var loaded = await new JsonPermissionStore(_path).LoadAsync();
        var only = Assert.Single(loaded.ToolDecisions);
        Assert.Equal("b_tool", only.Key);
        Assert.Equal(PermissionDecision.Deny, only.Value);

        // 原子写不留临时残骸
        Assert.Single(Directory.GetFiles(_dir));
    }

    [Fact]
    public void Constructor_RejectsEmptyPath()
    {
        Assert.Throws<ArgumentException>(() => new JsonPermissionStore(""));
        Assert.Throws<ArgumentException>(() => new JsonPermissionStore("   "));
    }

    [Fact]
    public async Task SaveAsync_NullSettings_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => new JsonPermissionStore(_path).SaveAsync(null!));
    }

    /// <summary>
    /// 20 个并发 SaveAsync（内容互不相同但都合法）：_saveGate 串行化 + 原子 Move 保证
    /// 最终文件完整等于其中某一次写入——不允许半截混合、不允许孤儿 .tmp。
    /// “最后一次写入”无法在门外无竞态地判定完成顺序（闸门释放与外部登记之间存在竞态），
    /// 故按磁盘事实判定赢家：每次写入携带唯一键 tool_i，文件中出现哪个键即哪次写入最后生效，
    /// 再反查该次写入的决策值必须逐项一致——这是“完整快照”的强断言，任何交错/混合都会失败。
    /// </summary>
    [Fact]
    public async Task ConcurrentSaves_AllPersisted_LastWriteWinsConsistently()
    {
        var store = new JsonPermissionStore(_path);
        var decisions = new[] { PermissionDecision.Allow, PermissionDecision.Deny, PermissionDecision.Ask };
        const int n = 20;
        var tasks = new Task[n];
        var completed = 0;
        for (var i = 0; i < n; i++)
        {
            var settings = new PermissionSettings();
            settings.ToolDecisions[$"tool_{i}"] = decisions[i % decisions.Length];
            tasks[i] = Task.Run(async () =>
            {
                await store.SaveAsync(settings);
                System.Threading.Interlocked.Increment(ref completed);
            });
        }
        await Task.WhenAll(tasks);
        Assert.Equal(n, completed); // 全部写入完成，无异常

        // 最终状态必须完整等于某一次写入：可反序列化、恰好一个键（混合/损坏在此失败）
        var loaded = await new JsonPermissionStore(_path).LoadAsync();
        var only = Assert.Single(loaded.ToolDecisions);
        Assert.StartsWith("tool_", only.Key);
        var winnerIndex = int.Parse(only.Key["tool_".Length..]);
        Assert.InRange(winnerIndex, 0, n - 1);
        Assert.Equal(decisions[winnerIndex % decisions.Length], only.Value); // 与赢家的写入逐项一致

        // 原子写不留孤儿 .tmp：目录内恰好一个文件
        Assert.Single(Directory.GetFiles(_dir));
    }

    /// <summary>
    /// Move 失败的确定性复现：目标路径是一个已存在的目录 → tmp 正常写出但
    /// File.Move 必然失败（任何平台都无法用文件覆盖目录）。失败分支必须：
    /// 原异常重抛 + 孤儿 .tmp 被清理（Reviewer B P2 项的行为固化）。
    /// </summary>
    [Fact]
    public async Task SaveAsync_MoveFails_OrphanTmpCleanedUpAndOriginalExceptionRethrown()
    {
        Directory.CreateDirectory(_path); // 目标变成目录 → Move 必败
        var store = new JsonPermissionStore(_path);
        var settings = new PermissionSettings();
        settings.ToolDecisions["a_tool"] = PermissionDecision.Allow;

        var ex = await Record.ExceptionAsync(() => store.SaveAsync(settings));
        Assert.NotNull(ex); // 失败如实上抛，不静默吞掉

        var parent = Path.GetDirectoryName(_path)!;
        Assert.Empty(Directory.GetFiles(parent, "*.tmp")); // 孤儿 .tmp 已清理
    }

    /// <summary>
    /// 预取消令牌：闸门 WaitAsync 立即抛取消异常，尚未进入写盘段 →
    /// 不创建任何目录/文件，无副作用。
    /// </summary>
    [Fact]
    public async Task SaveAsync_PreCancelledToken_ThrowsImmediatelyWithNoSideEffects()
    {
        using var cts = new System.Threading.CancellationTokenSource();
        cts.Cancel();
        var store = new JsonPermissionStore(_path);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.SaveAsync(new PermissionSettings(), cts.Token));

        Assert.False(Directory.Exists(_dir)); // 进门前即取消：一个字节都不落盘
    }
}
