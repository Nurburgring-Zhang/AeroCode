using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace AeroCode.App.Views;

public partial class SettingsDialog : Window
{
    public SettingsDialog()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    // 保存成功与否由 SettingsViewModel.SaveAsync 的真实落盘与 StatusText 如实反映；
    // 此处不再维护一个恒假的 Saved 标志（无消费方，属误导性死代码）。
    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();
}
