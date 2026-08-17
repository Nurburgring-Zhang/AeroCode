// Copyright (c) AeroCode V3.0
// SettingsViewModel — UI for editing app settings (theme/providers/font/memory caps).
// All edits go back into AppSettings on disk; no in-memory only state.
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using AeroCode.AI.Configuration;
using AeroCode.AI.Providers;
using AeroCode.App.Configuration;
using AeroCode.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AeroCode.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly ThemeService _theme;
    private readonly ProviderFactory _providerFactory;
    private readonly ILogger<SettingsViewModel> _logger;

    [ObservableProperty]
    private string _selectedTheme = ThemeService.Dark;

    [ObservableProperty]
    private int _fontSize = 14;

    [ObservableProperty]
    private int _memoryMaxChars = 2200;       // MEMORY.md cap (Hermes)
    [ObservableProperty]
    private int _userProfileMaxChars = 1375;  // USER.md cap (Hermes)

    [ObservableProperty]
    private string _defaultProviderId = "deepseek";
    [ObservableProperty]
    private string _defaultModel = string.Empty;

    [ObservableProperty]
    private string _statusText = string.Empty;
    [ObservableProperty]
    private bool _isBusy;

    public ObservableCollection<ThemeChoice> Themes { get; } = new()
    {
        new(ThemeService.Light, "☀️ Light"),
        new(ThemeService.Dark, "🌙 Dark"),
        new(ThemeService.System, "🖥️ Follow System"),
    };

    public ObservableCollection<string> AvailableProviderIds { get; } = new();

    public ObservableCollection<ProviderConfig> Providers { get; } = new();

    [ObservableProperty]
    private ProviderConfig? _selectedProvider;

    public SettingsViewModel(
        SettingsService settings,
        ThemeService theme,
        ProviderFactory providerFactory,
        ILogger<SettingsViewModel> logger)
    {
        _settings = settings;
        _theme = theme;
        _providerFactory = providerFactory;
        _logger = logger;
        HydrateFromSettings();
    }

    private void HydrateFromSettings()
    {
        var s = _settings.Current;
        SelectedTheme = string.IsNullOrWhiteSpace(s.Ui.Theme) ? ThemeService.Dark : s.Ui.Theme;
        FontSize = Math.Clamp(s.Ui.FontSize, 10, 22);
        MemoryMaxChars = Math.Clamp(s.Ui.MemoryMaxChars > 0 ? s.Ui.MemoryMaxChars : 2200, 200, 20000);
        UserProfileMaxChars = Math.Clamp(s.Ui.UserProfileMaxChars > 0 ? s.Ui.UserProfileMaxChars : 1375, 200, 10000);
        DefaultProviderId = s.Ai.DefaultProviderId;
        DefaultModel = s.Ai.DefaultModel;
        AvailableProviderIds.Clear();
        foreach (var p in s.Ai.Providers) AvailableProviderIds.Add(p.Id);
        Providers.Clear();
        foreach (var p in s.Ai.Providers) Providers.Add(p);
        SelectedProvider = Providers.FirstOrDefault(p => p.Id == DefaultProviderId) ?? Providers.FirstOrDefault();
    }

    [RelayCommand]
    public void SelectProvider(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        var p = Providers.FirstOrDefault(x => x.Id == id);
        if (p is not null) SelectedProvider = p;
    }

    [RelayCommand]
    public void AddProvider()
    {
        var p = new ProviderConfig
        {
            Id = $"custom-{Guid.NewGuid().ToString("N").Substring(0, 6)}",
            DisplayName = "New Provider",
            Kind = "OpenAICompatible",
            BaseUrl = "https://api.example.com/v1",
            DefaultModel = "model-name",
            ApiKeyEnvVar = "MY_API_KEY",
            RequiresApiKey = true
        };
        Providers.Add(p);
        SelectedProvider = p;
    }

    [RelayCommand]
    public void RemoveSelectedProvider()
    {
        if (SelectedProvider is null) return;
        if (Providers.Count <= 1)
        {
            StatusText = "⚠️ 至少保留一个 Provider";
            return;
        }
        var id = SelectedProvider.Id;
        Providers.Remove(SelectedProvider);
        if (DefaultProviderId == id)
            DefaultProviderId = Providers[0].Id;
        SelectedProvider = Providers[0];
    }

    [RelayCommand]
    public async Task SaveAsync()
    {
        try
        {
            IsBusy = true;
            var s = _settings.Current;
            s.Ui.Theme = SelectedTheme;
            s.Ui.FontSize = Math.Clamp(FontSize, 10, 22);
            s.Ui.MemoryMaxChars = Math.Clamp(MemoryMaxChars, 200, 20000);
            s.Ui.UserProfileMaxChars = Math.Clamp(UserProfileMaxChars, 200, 10000);
            s.Ai.DefaultProviderId = DefaultProviderId;
            s.Ai.DefaultModel = string.IsNullOrWhiteSpace(DefaultModel) ? "deepseek-v4-flash" : DefaultModel;
            s.Ai.Providers.Clear();
            foreach (var p in Providers) s.Ai.Providers.Add(p);
            await _settings.SaveAsync();
            _theme.Apply(SelectedTheme);
            StatusText = $"✅ 已保存 @ {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Save settings failed");
            StatusText = $"❌ 保存失败: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task ReloadAsync()
    {
        try
        {
            await _settings.LoadAsync();
            HydrateFromSettings();
            StatusText = "🔄 已从磁盘重新加载";
        }
        catch (Exception ex)
        {
            StatusText = $"❌ 重载失败: {ex.Message}";
        }
    }

    [RelayCommand]
    public async Task HealthCheckAllAsync()
    {
        try
        {
            IsBusy = true;
            StatusText = "⏳ 正在 ping 所有 provider...";
            var healthy = 0;
            var total = 0;
            foreach (var p in Providers)
            {
                total++;
                try
                {
                    var prov = _providerFactory.Get(p.Id);
                    if (await prov.HealthCheckAsync()) healthy++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Health check failed for {Id}", p.Id);
                }
            }
            StatusText = $"🏥 Provider 健康: {healthy}/{total} 在线";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public sealed record ThemeChoice(string Id, string Display);
}
