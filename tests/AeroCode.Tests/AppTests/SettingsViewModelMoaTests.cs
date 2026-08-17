// Copyright (c) AeroCode V3.0
// SettingsViewModel S9 策略配置段测试 —— MOA 选项（默认策略/四角色绑定/EnsembleSize/
// MaxUsdPerTurn/工具循环）的水合、校验、写回单例 + moa-options.json 落盘、改→存→重载回读一致。
// 全部走真实 SettingsService/ProviderFactory/ModelProfileCatalog/JsonMoaOptionsStore，零桩数据。
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AeroAgent.Conversation.Models;
using AeroAgent.Moa.Profiles;
using AeroAgent.Moa.Strategies;
using AeroCode.App.Configuration;
using AeroCode.App.Services;
using AeroCode.App.ViewModels;
using AeroCode.Harness.EventBus;
using AeroCode.Harness.Permission;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AeroCode.Tests.AppTests;

/// <summary>S9：MOA 策略配置段的水合 / 校验 / 保存 / 回读行为验证。</summary>
public sealed class SettingsViewModelMoaTests : IDisposable
{
    private readonly string _root;
    private readonly AppDataPaths _paths;
    private readonly SettingsService _settings;
    private readonly AeroCode.AI.Providers.ProviderFactory _factory;
    private readonly ModelProfileCatalog _catalog;
    private readonly MoaOptions _moaOptions;
    private readonly JsonMoaOptionsStore _moaStore;
    private readonly SettingsViewModel _vm;

    public SettingsViewModelMoaTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"settings_moa_{Guid.NewGuid():N}");
        _paths = new AppDataPaths(_root);
        _settings = new SettingsService(_paths);
        _settings.LoadAsync().GetAwaiter().GetResult();

        _factory = new AeroCode.AI.Providers.ProviderFactory(
            _settings.ToAiOptions(), NullLoggerFactory.Instance);
        var policy = PermissionPolicy.CreateDefault(new EventBus());
        var permStore = new JsonPermissionStore(_paths.PermissionsFile);
        _catalog = new ModelProfileCatalog(new JsonFileProfileStore(_paths.MoaProfilesFile));
        _catalog.LoadAsync(BuiltInProfiles.Seed()).GetAwaiter().GetResult();
        _moaOptions = new MoaOptions();
        _moaStore = new JsonMoaOptionsStore(_paths.MoaOptionsFile);
        _vm = new SettingsViewModel(
            _settings, new ThemeService(), _factory, policy, permStore, _catalog,
            _moaOptions, _moaStore, NullLogger<SettingsViewModel>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    // ────────────────────────── 水合 ──────────────────────────

    [Fact]
    public void Hydrate_ReflectsLiveOptions_AndChoiceComposition()
    {
        // 默认水合：新建 MoaOptions 的默认值如实呈现
        Assert.Equal(OrchestrationStrategy.Single, _vm.DefaultStrategy);
        Assert.Equal(2, _vm.EnsembleSize);
        Assert.Equal(string.Empty, _vm.MaxUsdText);
        Assert.True(_vm.ToolsEnabled);
        Assert.Equal(5, _vm.StrategyChoices.Count); // 全部编排策略枚举值

        // 选项构成：自动分配 + 每个已配置 provider 的默认模型（种子 4 个 provider）
        Assert.True(_vm.RoleBindingChoices.Count >= 5);
        Assert.Null(_vm.RoleBindingChoices[0].Binding); // 首项恒为自动分配
        Assert.Contains(_vm.RoleBindingChoices, c => c.Binding == new ModelBinding("deepseek", null));
        Assert.Equal("🤖 自动分配（按画像）", _vm.RoleBindingChoices[0].Display);

        Assert.Null(_vm.RouterChoice?.Binding);
        Assert.Null(_vm.JudgeChoice?.Binding);
    }

    [Fact]
    public void Hydrate_PresetOptions_MappedIntoUi()
    {
        // 预置选项（模拟 moa-options.json 启动加载后的单例）→ 新建 VM 水合一致
        var preset = new MoaOptions
        {
            DefaultStrategy = OrchestrationStrategy.Ensemble,
            Router = new ModelBinding("deepseek", null),
            Planner = new ModelBinding("qwen", null),
            EnsembleSize = 3,
            MaxUsdPerTurn = 0.5,
            ToolsEnabled = false,
        };
        var vm2 = new SettingsViewModel(
            _settings, new ThemeService(), _factory,
            PermissionPolicy.CreateDefault(new EventBus()),
            new JsonPermissionStore(_paths.PermissionsFile), _catalog,
            preset, _moaStore, NullLogger<SettingsViewModel>.Instance);

        Assert.Equal(OrchestrationStrategy.Ensemble, vm2.DefaultStrategy);
        Assert.Equal(3, vm2.EnsembleSize);
        Assert.Equal("0.5", vm2.MaxUsdText);
        Assert.False(vm2.ToolsEnabled);
        Assert.Equal(new ModelBinding("deepseek", null), vm2.RouterChoice!.Binding);
        Assert.Equal(new ModelBinding("qwen", null), vm2.PlannerChoice!.Binding);
        Assert.Null(vm2.SynthesizerChoice!.Binding);
    }

    [Fact]
    public void Hydrate_CatalogModelProfiles_AppearAsRoleChoices()
    {
        // 运行时自学习/用户添加的具体模型画像 → 角色绑定可精确到模型
        _catalog.Upsert(new ModelProfile
        {
            ProviderId = "deepseek",
            ModelId = "deepseek-v4-flash",
            Strengths = new System.Collections.Generic.List<string> { ModelStrength.Code },
        });
        _vm.HydrateMoaSection();

        Assert.Contains(_vm.RoleBindingChoices,
            c => c.Binding == new ModelBinding("deepseek", "deepseek-v4-flash"));
    }

    // ────────────────────────── 保存 → 单例 + 落盘 ──────────────────────────

    [Fact]
    public async Task Save_WritesSingletonAndDisk_StringEnumRoundTrip()
    {
        var fired = 0;
        _moaOptions.OptionsChanged += () => fired++;

        _vm.DefaultStrategy = OrchestrationStrategy.Router;
        _vm.RouterChoice = _vm.RoleBindingChoices.Single(c => c.Binding == new ModelBinding("deepseek", null));
        _vm.JudgeChoice = _vm.RoleBindingChoices.Single(c => c.Binding == new ModelBinding("qwen", null));
        _vm.EnsembleSize = 3;
        _vm.MaxUsdText = "0.75";
        _vm.ToolsEnabled = false;

        await _vm.SaveAsync();
        Assert.StartsWith("✅", _vm.StatusText);
        Assert.Equal(1, fired);

        // 单例就地生效（策略每轮读字段 → 下一轮即热生效）
        Assert.Equal(OrchestrationStrategy.Router, _moaOptions.DefaultStrategy);
        Assert.Equal(new ModelBinding("deepseek", null), _moaOptions.Router);
        Assert.Equal(new ModelBinding("qwen", null), _moaOptions.Judge);
        Assert.Null(_moaOptions.Planner);
        Assert.Equal(3, _moaOptions.EnsembleSize);
        Assert.Equal(0.75, _moaOptions.MaxUsdPerTurn);
        Assert.False(_moaOptions.ToolsEnabled);

        // moa-options.json 落盘：PascalCase + 枚举字符串，逐项断言
        var json = await File.ReadAllTextAsync(_paths.MoaOptionsFile);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("Router", doc.RootElement.GetProperty("DefaultStrategy").GetString());
        Assert.Equal("deepseek", doc.RootElement.GetProperty("Router").GetProperty("ProviderId").GetString());
        Assert.Equal("qwen", doc.RootElement.GetProperty("Judge").GetProperty("ProviderId").GetString());
        Assert.Equal(3, doc.RootElement.GetProperty("EnsembleSize").GetInt32());
        Assert.Equal(0.75, doc.RootElement.GetProperty("MaxUsdPerTurn").GetDouble());
        Assert.False(doc.RootElement.GetProperty("ToolsEnabled").GetBoolean());
    }

    [Fact]
    public async Task Save_ThenReloadFromDisk_RehydratesIdentical()
    {
        // 改 → 存 → 以磁盘为准重载回读一致（S9 验收：配置持久化断言）
        _vm.DefaultStrategy = OrchestrationStrategy.Decompose;
        _vm.SynthesizerChoice = _vm.RoleBindingChoices.Single(c => c.Binding == new ModelBinding("minimax", null));
        _vm.EnsembleSize = 4;
        _vm.MaxUsdText = "1.25";
        await _vm.SaveAsync();
        Assert.StartsWith("✅", _vm.StatusText);

        // 全新进程视角：从磁盘加载选项再构建 VM
        var reloaded = await new JsonMoaOptionsStore(_paths.MoaOptionsFile).LoadAsync();
        Assert.Equal(OrchestrationStrategy.Decompose, reloaded.DefaultStrategy);
        Assert.Equal(new ModelBinding("minimax", null), reloaded.Synthesizer);
        Assert.Equal(4, reloaded.EnsembleSize);
        Assert.Equal(1.25, reloaded.MaxUsdPerTurn);

        var vm2 = new SettingsViewModel(
            _settings, new ThemeService(), _factory,
            PermissionPolicy.CreateDefault(new EventBus()),
            new JsonPermissionStore(_paths.PermissionsFile), _catalog,
            reloaded, _moaStore, NullLogger<SettingsViewModel>.Instance);
        Assert.Equal(OrchestrationStrategy.Decompose, vm2.DefaultStrategy);
        Assert.Equal(4, vm2.EnsembleSize);
        Assert.Equal("1.25", vm2.MaxUsdText);
        Assert.Equal(new ModelBinding("minimax", null), vm2.SynthesizerChoice!.Binding);
    }

    [Fact]
    public async Task Save_EmptyBudget_PersistsNullUnlimited()
    {
        _vm.MaxUsdText = "   ";
        await _vm.SaveAsync();
        Assert.StartsWith("✅", _vm.StatusText);
        Assert.Null(_moaOptions.MaxUsdPerTurn);

        var reloaded = await new JsonMoaOptionsStore(_paths.MoaOptionsFile).LoadAsync();
        Assert.Null(reloaded.MaxUsdPerTurn);
    }

    [Fact]
    public async Task Save_ClampsEnsembleSize_ToSupportedRange()
    {
        _vm.EnsembleSize = 9;
        await _vm.SaveAsync();
        Assert.StartsWith("✅", _vm.StatusText);
        Assert.Equal(4, _moaOptions.EnsembleSize);

        _vm.EnsembleSize = 1;
        await _vm.SaveAsync();
        Assert.Equal(2, _moaOptions.EnsembleSize);
    }

    // ────────────────────────── 校验门 ──────────────────────────

    [Fact]
    public void TryParseMaxUsd_Variants()
    {
        Assert.True(SettingsViewModel.TryParseMaxUsd("", out var none, out _));
        Assert.Null(none);
        Assert.True(SettingsViewModel.TryParseMaxUsd("0.5", out var half, out _));
        Assert.Equal(0.5, half);

        Assert.False(SettingsViewModel.TryParseMaxUsd("0", out _, out var zeroErr));
        Assert.Contains("正数", zeroErr);
        Assert.False(SettingsViewModel.TryParseMaxUsd("-1", out _, out var negErr));
        Assert.Contains("正数", negErr);
        Assert.False(SettingsViewModel.TryParseMaxUsd("abc", out _, out var badErr));
        Assert.Contains("无法解析", badErr);
        Assert.False(SettingsViewModel.TryParseMaxUsd("NaN", out _, out var nanErr));
        Assert.NotNull(nanErr);
    }

    [Fact]
    public async Task Save_InvalidBudget_BlocksEntirely_NothingWritten()
    {
        _vm.MaxUsdText = "不是数字";
        _vm.DefaultStrategy = OrchestrationStrategy.Ensemble;

        await _vm.SaveAsync();

        Assert.StartsWith("❌", _vm.StatusText);
        Assert.Contains("预算", _vm.StatusText);
        Assert.False(File.Exists(_paths.MoaOptionsFile)); // 整体阻止：磁盘零改动
        Assert.Equal(OrchestrationStrategy.Single, _moaOptions.DefaultStrategy); // 单例同样未动
    }

    // ────────────────────────── 孤儿绑定与增删 provider 同步 ──────────────────────────

    [Fact]
    public async Task OrphanBinding_ListedHonestly_AndPreservedBySave()
    {
        // 绑定指向未配置的 provider：如实成列"未配置"，绝不静默改回自动分配
        _moaOptions.Router = new ModelBinding("gone-provider", null);
        _vm.HydrateMoaSection();

        Assert.Equal(new ModelBinding("gone-provider", null), _vm.RouterChoice!.Binding);
        Assert.Contains("未配置", _vm.RouterChoice.Display);
        Assert.Contains(_vm.RouterChoice, _vm.RoleBindingChoices);

        await _vm.SaveAsync();
        Assert.StartsWith("✅", _vm.StatusText);
        // 用户未改动 → 孤儿绑定原样保留（ModelResolver 运行时会回退自动分配，但意图不丢）
        Assert.Equal(new ModelBinding("gone-provider", null), _moaOptions.Router);
    }

    [Fact]
    public void AddProvider_RoleChoicesUpdated_SelectionKept()
    {
        var deepseekDefault = _vm.RoleBindingChoices.Single(c => c.Binding == new ModelBinding("deepseek", null));
        _vm.RouterChoice = deepseekDefault;

        _vm.AddProviderCommand.Execute(null);

        // 新 provider 立即可选；既有选择按绑定值恢复（不因重建而丢失）
        Assert.Equal(deepseekDefault.Binding, _vm.RouterChoice!.Binding);
        var added = _vm.Providers.Last().Id;
        Assert.Contains(_vm.RoleBindingChoices, c => c.Binding == new ModelBinding(added, null));
    }

    [Fact]
    public void RemoveProvider_BoundRoleBecomesOrphan_NotSilentlyReset()
    {
        // 选中一个非默认 provider（种子含 minimax）绑定后删除该 provider
        var minimaxDefault = _vm.RoleBindingChoices.Single(c => c.Binding == new ModelBinding("minimax", null));
        _vm.PlannerChoice = minimaxDefault;
        _vm.SelectedProvider = _vm.Providers.Single(p => p.Id == "minimax");

        _vm.RemoveSelectedProviderCommand.Execute(null);

        Assert.Equal(new ModelBinding("minimax", null), _vm.PlannerChoice!.Binding);
        Assert.Contains("未配置", _vm.PlannerChoice.Display);
        Assert.DoesNotContain(_vm.RoleBindingChoices,
            c => c.Binding == new ModelBinding("minimax", null) && !c.Display.Contains("未配置"));
    }
}
