using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace AeroCode.App.Views;

/// <summary>桌面设置窗口薄壳：内容在 SettingsView（与 Android 覆盖层共享同一视图）。</summary>
public partial class SettingsDialog : Window
{
    public SettingsDialog()
    {
        InitializeComponent();
        var view = new SettingsView();
        // DataContext 由 Window 向下自动流转到 SettingsView。
        view.CloseRequested += () => Close();
        Content = view;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
