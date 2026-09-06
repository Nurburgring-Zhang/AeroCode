// Copyright (c) AeroCode
// R2 修复 MED-3 验证：moaoptions.json 非法时的 fail-safe 行为可观测化——
// LoadAsync 记录 LastLoadError（组合根 WARN 的数据源）+ 尽力抢救预算上限 MaxUsdPerTurn
//（最具安全意义的单字段；其余编排选项仍按既有语义回退默认，不阻塞启动、不伪造成功）。
using System;
using System.IO;
using System.Threading.Tasks;
using AeroAgent.Conversation.Models;
using AeroAgent.Moa.Strategies;
using Xunit;

namespace AeroCode.Tests.MoaTests;

public sealed class JsonMoaOptionsStoreRepairTests : IDisposable
{
    private readonly string _dir;
    private readonly string _filePath;

    public JsonMoaOptionsStoreRepairTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"moa_options_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _filePath = Path.Combine(_dir, "moaoptions.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public async Task ValidJson_LoadsNormally_LastLoadErrorNull()
    {
        // 本 store 无命名策略：PascalCase 落盘（枚举按字符串）。
        await File.WriteAllTextAsync(_filePath,
            "{\"DefaultStrategy\":\"Decompose\",\"MaxUsdPerTurn\":0.42,\"EnsembleSize\":3}");
        var store = new JsonMoaOptionsStore(_filePath);

        var options = await store.LoadAsync();

        Assert.Null(store.LastLoadError);
        Assert.Equal(0.42, options.MaxUsdPerTurn);
        Assert.Equal(3, options.EnsembleSize);
    }

    [Fact]
    public async Task CorruptJson_LastLoadErrorSet_BudgetCapSalvaged()
    {
        // 文件整体非法（预算字段附近语法损坏）：回退默认 + 记录拒载异常 + 预算上限抢救保留。
        await File.WriteAllTextAsync(_filePath,
            "{\"MaxUsdPerTurn\":1.25,\"Planner\":{\"ProviderId\":\"deepseek\", BROKEN");
        var store = new JsonMoaOptionsStore(_filePath);

        var options = await store.LoadAsync();

        Assert.NotNull(store.LastLoadError); // 拒载事实可观测（组合根 WARN 数据源）
        Assert.Equal(1.25, options.MaxUsdPerTurn); // 预算上限尽力抢救（不静默丢安全敞口）
        Assert.Null(options.Planner); // 其余选项如实回退默认
    }

    [Fact]
    public async Task CorruptJson_CamelCaseBudgetField_SalvagedToo()
    {
        await File.WriteAllTextAsync(_filePath,
            "{ \"maxUsdPerTurn\" : 2.5 , @@garbage@@");
        var store = new JsonMoaOptionsStore(_filePath);

        var options = await store.LoadAsync();

        Assert.NotNull(store.LastLoadError);
        Assert.Equal(2.5, options.MaxUsdPerTurn);
    }

    [Fact]
    public async Task CorruptJson_WithoutBudgetField_DefaultsKeepNoCap_LastLoadErrorSet()
    {
        await File.WriteAllTextAsync(_filePath, "not json at all ]]");
        var store = new JsonMoaOptionsStore(_filePath);

        var options = await store.LoadAsync();

        Assert.NotNull(store.LastLoadError);
        Assert.Null(options.MaxUsdPerTurn); // 无可抢救字段 = 保持默认（null = 不限制，语义不变）
        Assert.Equal(OrchestrationStrategy.Single, options.DefaultStrategy);
    }

    [Fact]
    public async Task CorruptJson_InvalidBudgetValue_NotSalvaged()
    {
        // 负数/非数值不是合法预算上限：不抢救（不伪造成功），只记拒载事实。
        await File.WriteAllTextAsync(_filePath, "{\"MaxUsdPerTurn\": -3.0, ]]");
        var store = new JsonMoaOptionsStore(_filePath);

        var options = await store.LoadAsync();

        Assert.NotNull(store.LastLoadError);
        Assert.Null(options.MaxUsdPerTurn);
    }

    [Fact]
    public async Task MissingFile_NoError_DefaultOptions()
    {
        var store = new JsonMoaOptionsStore(Path.Combine(_dir, "absent.json"));

        var options = await store.LoadAsync();

        Assert.Null(store.LastLoadError);
        Assert.Null(options.MaxUsdPerTurn);
    }

    [Fact]
    public async Task EmptyFile_NoError_DefaultOptions()
    {
        await File.WriteAllTextAsync(_filePath, "   ");
        var store = new JsonMoaOptionsStore(_filePath);

        var options = await store.LoadAsync();

        Assert.Null(store.LastLoadError); // 空文件 = 未配置，不算拒载
        Assert.Null(options.MaxUsdPerTurn);
    }
}
