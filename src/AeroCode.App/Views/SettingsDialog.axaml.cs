using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace AeroCode.App.Views;

public partial class SettingsDialog : Window
{
    public bool Saved { get; private set; }

    public SettingsDialog()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(Saved);
}
