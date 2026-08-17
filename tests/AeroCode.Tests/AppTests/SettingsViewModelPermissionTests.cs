// Copyright (c) AeroCode V3.0
// SettingsViewModel permission-section tests — real SettingsService/ProviderFactory/
// PermissionPolicy/JsonPermissionStore against a temp app-data root.
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AeroAgent.Moa.Profiles;
using AeroCode.App.Configuration;
using AeroCode.App.Services;
using AeroCode.App.ViewModels;
using AeroCode.Harness.EventBus;
using AeroCode.Harness.Permission;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AeroCode.Tests.AppTests;

/// <summary>
/// 设置页"工具权限"段：规则行来自当前策略快照；Save 把编辑结果写回策略
/// 并完整落盘 permissions.json；Reload 从生效策略重建（含对话框期间记住的决策）。
/// </summary>
public sealed class SettingsViewModelPermissionTests : IDisposable
{
    private readonly string _root;
    private readonly SettingsService _settings;
    private readonly PermissionPolicy _policy;
    private readonly JsonPermissionStore _store;
    private readonly SettingsViewModel _vm;

    public SettingsViewModelPermissionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"settings_perm_{Guid.NewGuid():N}");
        var paths = new AppDataPaths(_root);
        _settings = new SettingsService(paths);
        _settings.LoadAsync().GetAwaiter().GetResult();

        var factory = new AeroCode.AI.Providers.ProviderFactory(
            _settings.ToAiOptions(), NullLoggerFactory.Instance);
        _policy = PermissionPolicy.CreateDefault(new EventBus());
        // 模拟组合根：内建笔记工具默认放行
        _policy.SetDefaultDecision("create_note", PermissionDecision.Allow);
        _store = new JsonPermissionStore(paths.PermissionsFile);
        var catalog = new ModelProfileCatalog(new JsonFileProfileStore(paths.MoaProfilesFile));
        catalog.LoadAsync(BuiltInProfiles.Seed()).GetAwaiter().GetResult();
        _vm = new SettingsViewModel(
            _settings, new ThemeService(), factory, _policy, _store, catalog,
            new AeroAgent.Moa.Strategies.MoaOptions(),
            new AeroAgent.Moa.Strategies.JsonMoaOptionsStore(paths.MoaOptionsFile),
            NullLogger<SettingsViewModel>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Hydrate_ListsPolicyRules_WithCurrentDecisions()
    {
        Assert.True(_vm.PermissionRules.Count >= 10); // CreateDefault 规则表 + create_note

        var write = _vm.PermissionRules.Single(r => r.ToolName == "write_file");
        Assert.Equal(PermissionDecision.Ask, write.Decision.Value);
        Assert.Equal("Modifies disk", write.Notes);

        var create = _vm.PermissionRules.Single(r => r.ToolName == "create_note");
        Assert.Equal(PermissionDecision.Allow, create.Decision.Value);

        // 三态选项齐备
        Assert.Equal(3, PermissionRuleItem.AllChoices.Count);
        Assert.Contains(PermissionRuleItem.AllChoices, c => c.Value == PermissionDecision.Ask);
    }

    [Fact]
    public async Task Save_AppliesEditedDecisions_ToPolicyAndFile()
    {
        var write = _vm.PermissionRules.Single(r => r.ToolName == "write_file");
        write.Decision = PermissionRuleItem.AllChoices.Single(c => c.Value == PermissionDecision.Deny);
        var read = _vm.PermissionRules.Single(r => r.ToolName == "read_file");
        read.Decision = PermissionRuleItem.AllChoices.Single(c => c.Value == PermissionDecision.Ask);

        await _vm.SaveAsync();

        Assert.StartsWith("✅", _vm.StatusText);
        // 内存策略立即生效
        Assert.Equal(PermissionDecision.Deny, _policy.Check("write_file").Decision);
        Assert.Equal(PermissionDecision.Ask, _policy.Check("read_file").Decision);

        // 磁盘：用户视图完整落盘（含未修改的规则）
        var loaded = await _store.LoadAsync();
        Assert.Equal(_vm.PermissionRules.Count, loaded.ToolDecisions.Count);
        Assert.Equal(PermissionDecision.Deny, loaded.ToolDecisions["write_file"]);
        Assert.Equal(PermissionDecision.Ask, loaded.ToolDecisions["read_file"]);
        Assert.Equal(PermissionDecision.Allow, loaded.ToolDecisions["create_note"]);
    }

    [Fact]
    public async Task Reload_RehydratesFromLivePolicy_IncludingDialogRememberedDecisions()
    {
        // 模拟授权对话框在设置窗口打开期间"记住"了决定
        _policy.SetDefaultDecision("write_file", PermissionDecision.Allow);

        await _vm.ReloadAsync();

        var write = _vm.PermissionRules.Single(r => r.ToolName == "write_file");
        Assert.Equal(PermissionDecision.Allow, write.Decision.Value);
        Assert.StartsWith("🔄", _vm.StatusText);
    }

    [Fact]
    public async Task Save_MergesLivePolicy_DecisionsRememberedDuringOpenAreNotErased()
    {
        // P0-1 回归：设置页打开期间（快照已水合）授权对话框"记住"了新工具的决定，
        // 随后的 Save 不得用陈旧快照把它擦掉。
        _policy.SetDefaultDecision("mcp_new_tool", PermissionDecision.Deny);

        await _vm.SaveAsync();

        var loaded = await _store.LoadAsync();
        Assert.Equal(PermissionDecision.Deny, loaded.ToolDecisions["mcp_new_tool"]);
        // 用户列表内的编辑照常生效
        Assert.True(loaded.ToolDecisions.ContainsKey("write_file"));
    }

    [Fact]
    public void RefreshFromSources_PicksUpDecisionsRememberedAfterHydration()
    {
        // 单例 VM 开窗前刷新：期间记住的决定必须出现在列表中
        _policy.SetDefaultDecision("mcp_late_tool", PermissionDecision.Allow);

        _vm.RefreshFromSources();

        var item = _vm.PermissionRules.Single(r => r.ToolName == "mcp_late_tool");
        Assert.Equal(PermissionDecision.Allow, item.Decision.Value);
    }

    [Fact]
    public void Constructor_NullPermissionDeps_Throw()
    {
        var paths = new AppDataPaths(Path.Combine(_root, "other"));
        var settings = new SettingsService(paths);
        var factory = new AeroCode.AI.Providers.ProviderFactory(
            settings.ToAiOptions(), NullLoggerFactory.Instance);
        var catalog = new ModelProfileCatalog();
        var moaOptions = new AeroAgent.Moa.Strategies.MoaOptions();
        var moaStore = new AeroAgent.Moa.Strategies.JsonMoaOptionsStore(paths.MoaOptionsFile);

        Assert.Throws<ArgumentNullException>(() => new SettingsViewModel(
            settings, new ThemeService(), factory, null!, _store, catalog,
            moaOptions, moaStore, NullLogger<SettingsViewModel>.Instance));
        Assert.Throws<ArgumentNullException>(() => new SettingsViewModel(
            settings, new ThemeService(), factory, _policy, null!, catalog,
            moaOptions, moaStore, NullLogger<SettingsViewModel>.Instance));
        Assert.Throws<ArgumentNullException>(() => new SettingsViewModel(
            settings, new ThemeService(), factory, _policy, _store, null!,
            moaOptions, moaStore, NullLogger<SettingsViewModel>.Instance));
        Assert.Throws<ArgumentNullException>(() => new SettingsViewModel(
            settings, new ThemeService(), factory, _policy, _store, catalog,
            null!, moaStore, NullLogger<SettingsViewModel>.Instance));
        Assert.Throws<ArgumentNullException>(() => new SettingsViewModel(
            settings, new ThemeService(), factory, _policy, _store, catalog,
            moaOptions, null!, NullLogger<SettingsViewModel>.Instance));
    }
}
