// Copyright (c) AeroCode V3.0
// AIAssistantViewModel 热重载回归测试 —— 设置保存后 ProviderFactory.Reload 触发
// ProvidersChanged，助手面板的 provider 下拉必须就地刷新。
using System;
using System.Linq;
using AeroCode.AI.Configuration;
using AeroCode.AI.Providers;
using AeroCode.App.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AeroCode.Tests.AppTests;

/// <summary>
/// S8 热重载链回归：AIAssistantViewModel 若不订阅 ProvidersChanged，
/// 已删除的 provider 会留在下拉里，用户选中后 Get(已删id) 抛异常。
/// 使用真实 ProviderFactory（Reload → 触发事件），无 mock。
/// </summary>
public sealed class AIAssistantViewModelReloadTests
{
    private static ProviderConfig Cfg(string id) => new()
    {
        Id = id,
        DisplayName = id,
        Kind = "OpenAICompatible",
        BaseUrl = "http://127.0.0.1:9/v1",
        DefaultModel = "test-model",
        RequiresApiKey = false,
    };

    private static ProviderFactory MakeFactory(params string[] ids) =>
        new(new AIOptions
        {
            DefaultProviderId = ids.Length > 0 ? ids[0] : string.Empty,
            Providers = ids.Select(Cfg).ToList(),
        }, NullLoggerFactory.Instance);

    [Fact]
    public void Ctor_ListsConfiguredProviders_SelectsDefault()
    {
        var factory = MakeFactory("deepseek", "ollama");
        var vm = new AIAssistantViewModel(factory);

        Assert.Equal(new[] { "deepseek", "ollama" }, vm.AvailableProviders.ToArray());
        Assert.Equal("deepseek", vm.SelectedProviderId);
    }

    [Fact]
    public void Reload_AddsProvider_ListRefreshed_SelectionKept()
    {
        var factory = MakeFactory("deepseek");
        var vm = new AIAssistantViewModel(factory);

        factory.Reload(new AIOptions
        {
            DefaultProviderId = "deepseek",
            Providers = new[] { "deepseek", "qwen" }.Select(Cfg).ToList(),
        });

        Assert.Equal(new[] { "deepseek", "qwen" }, vm.AvailableProviders.ToArray());
        Assert.Equal("deepseek", vm.SelectedProviderId);
    }

    [Fact]
    public void Reload_RemovesSelectedProvider_FallsBackToFirst()
    {
        var factory = MakeFactory("deepseek", "ollama");
        var vm = new AIAssistantViewModel(factory);
        vm.SelectedProviderId = "ollama";

        factory.Reload(new AIOptions
        {
            DefaultProviderId = "deepseek",
            Providers = new[] { "deepseek" }.Select(Cfg).ToList(),
        });

        Assert.Equal(new[] { "deepseek" }, vm.AvailableProviders.ToArray());
        Assert.Equal("deepseek", vm.SelectedProviderId);
    }

    [Fact]
    public void Reload_RemovesAllProviders_SelectionBecomesEmpty()
    {
        var factory = MakeFactory("deepseek");
        var vm = new AIAssistantViewModel(factory);

        factory.Reload(new AIOptions { DefaultProviderId = string.Empty, Providers = new() });

        Assert.Empty(vm.AvailableProviders);
        Assert.Equal(string.Empty, vm.SelectedProviderId);
    }

    [Fact]
    public void Reload_DeletedProvider_GoneFromDropdown_GetWouldThrowIfStale()
    {
        // S8 核心缺陷场景：删除 provider 后下拉不刷新 → 用户选中残留项 → Get 抛异常。
        // 订阅刷新后，下拉与工厂保持一致，残留项不可选。
        var factory = MakeFactory("deepseek", "ollama");
        var vm = new AIAssistantViewModel(factory);

        factory.Reload(new AIOptions
        {
            DefaultProviderId = "deepseek",
            Providers = new[] { "deepseek" }.Select(Cfg).ToList(),
        });

        Assert.DoesNotContain("ollama", vm.AvailableProviders);
        Assert.Throws<InvalidOperationException>(() => factory.Get("ollama"));
    }
}
