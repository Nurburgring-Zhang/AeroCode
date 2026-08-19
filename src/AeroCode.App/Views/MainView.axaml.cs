// Copyright (c) AeroCode V3.0
// MainView — 平台无关主视图：桌面由 MainWindow 承载，Android 由 ISingleViewApplicationLifetime.MainView 承载。
using System;
using AeroCode.App.Configuration;
using AeroCode.App.Services;
using AeroCode.App.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Microsoft.Extensions.DependencyInjection;

namespace AeroCode.App.Views;

public partial class MainView : UserControl
{
    // 覆盖层卡片配色（与 SettingsView/PermissionDialogView 的 AXAML 资源一致）
    private static readonly SolidColorBrush CardBg = new(Color.FromRgb(0x16, 0x1A, 0x23));
    private static readonly SolidColorBrush CardBorder = new(Color.FromRgb(0x2A, 0x31, 0x42));

    private MainWindowViewModel? _vm;
    private bool _settingsOpen;

    public MainView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        // 覆盖层宿主挂载：single-view 平台（Android）无 Window，对话框经 OverlayService 呈现。
        // 设计时/服务容器未就绪时忽略。
        try
        {
            App.Services.GetRequiredService<OverlayService>().AttachHost(OverlayRoot);
        }
        catch { /* 服务容器尚未构建（设计器等场景）：无覆盖层不影响主流程 */ }
    }

    private async void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            // Activity 重建等场景可能换绑新 VM：先解绑旧的，避免双订阅。
            if (_vm is not null && !ReferenceEquals(_vm, vm))
            {
                _vm.PropertyChanged -= OnVmPropertyChanged;
            }
            _vm = vm;
            vm.PropertyChanged += OnVmPropertyChanged;
            await vm.InitializeAsync();
            RenderPreview();
        }
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.SelectedNote))
        {
            RenderPreview();
        }
    }

    private void RenderPreview()
    {
        if (_vm?.SelectedNote is null) return;
        var content = _vm.SelectedNote.Content ?? string.Empty;
        try
        {
            PreviewView.SetMarkdown(content);
        }
        catch { /* ignore render errors to keep UI alive */ }
    }

    private async void OnOpenSettingsClick(object? sender, RoutedEventArgs e)
    {
        // 重入守卫：Android 覆盖层路径非模态，快速连点会叠加两个共享单例
        // SettingsViewModel 的设置层（RefreshFromSources 将重置编辑快照）。
        if (_settingsOpen)
        {
            return;
        }
        _settingsOpen = true;
        try
        {
            var sp = App.Services;
            var vm = sp.GetRequiredService<SettingsViewModel>();
            // 单例 VM 的快照在窗口关闭后即过期：每次打开前强制刷新，
            // 否则陈旧快照随 Save 合并会擦掉期间"记住"的授权决策。
            vm.RefreshFromSources();

            if (TopLevel.GetTopLevel(this) is Window owner)
            {
                // 桌面路径：模态窗口（与历史行为一致）
                var dlg = new SettingsDialog { DataContext = vm };
                await dlg.ShowDialog(owner);
            }
            else
            {
                // single-view 平台（Android）：无 Window，设置视图走全屏覆盖层
                var overlay = sp.GetRequiredService<OverlayService>();
                var view = new SettingsView { DataContext = vm };
                var card = new Border
                {
                    Background = CardBg,
                    BorderBrush = CardBorder,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    MaxWidth = 860,
                    Child = view
                };
                view.CloseRequested += () => overlay.CloseOverlay(card);
                await overlay.ShowAsync(card);
            }

            // After settings close, the saved theme is already applied inside VM.
            // Refresh MainWindow VM status so user sees confirmation.
            if (DataContext is MainWindowViewModel main)
            {
                main.StatusText = vm.StatusText.StartsWith("✅") ? vm.StatusText : main.StatusText;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"OpenSettings failed: {ex}");
        }
        finally
        {
            _settingsOpen = false;
        }
    }

    private void OnCycleThemeClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var sp = App.Services;
            var settings = sp.GetRequiredService<SettingsService>();
            var theme = sp.GetRequiredService<ThemeService>();
            var cur = settings.Current.Ui.Theme;
            var next = cur switch
            {
                ThemeService.Dark => ThemeService.Light,
                ThemeService.Light => ThemeService.System,
                _ => ThemeService.Dark
            };
            settings.Current.Ui.Theme = next;
            theme.Apply(next);
            _ = settings.SaveAsync();
            if (DataContext is MainWindowViewModel main)
                main.StatusText = $"🌓 主题已切换: {next}";
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Cycle theme failed: {ex}");
        }
    }
}
