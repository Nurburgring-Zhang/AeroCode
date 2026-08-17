// Copyright (c) AeroCode V3.0
// PermissionDialog — real Avalonia authorization window (allow / deny / remember).
using AeroCode.App.Services;
using Avalonia.Controls;

namespace AeroCode.App.Views;

public partial class PermissionDialog : Window
{
    public PermissionDialog()
    {
        InitializeComponent();
    }

    public PermissionDialog(PermissionPrompt prompt) : this()
    {
        ToolNameText.Text = prompt.ToolName;
        ArgsText.Text = string.IsNullOrWhiteSpace(prompt.ArgumentsPreview)
            ? "(无参数)"
            : prompt.ArgumentsPreview;
    }

    private void OnAllowClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => Close(new PermissionDialogResult(Approved: true, Remember: RememberBox.IsChecked == true));

    private void OnDenyClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => Close(new PermissionDialogResult(Approved: false, Remember: RememberBox.IsChecked == true));

    /// <summary>对话轮取消时由 presenter 调用：关闭且不产生决定（broker 按拒绝处理）。</summary>
    public void CloseByCancellation()
    {
        try
        {
            Close(null);
        }
        catch (InvalidOperationException)
        {
            // 对话框已关闭/尚未显示完成：无决定即拒绝，无需再处理。
        }
    }
}
