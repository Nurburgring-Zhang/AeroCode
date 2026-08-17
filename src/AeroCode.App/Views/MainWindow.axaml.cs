using System;
using AeroCode.App.Configuration;
using AeroCode.App.Services;
using AeroCode.App.ViewModels;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;

namespace AeroCode.App.Views;

public partial class MainWindow : Window
{
    private MainWindowViewModel? _vm;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private async void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
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
        try
        {
            var sp = App.Services;
            var vm = sp.GetRequiredService<SettingsViewModel>();
            // 单例 VM 的快照在窗口关闭后即过期：每次打开前强制刷新，
            // 否则陈旧快照随 Save 合并会擦掉期间"记住"的授权决策。
            vm.RefreshFromSources();
            var dlg = new SettingsDialog { DataContext = vm };
            await dlg.ShowDialog(this);
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
