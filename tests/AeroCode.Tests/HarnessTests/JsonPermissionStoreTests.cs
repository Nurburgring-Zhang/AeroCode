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
}
