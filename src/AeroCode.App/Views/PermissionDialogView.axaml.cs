// Copyright (c) AeroCode V3.0
// PermissionDialogView — 授权视图（平台无关）。桌面由 PermissionDialog(Window) 承载，
// Android 经 OverlayService 覆盖层呈现。决定经 Completed 事件交给宿主（null = 无决定 → broker 按拒绝）。
using System;
using AeroCode.App.Services;
using Avalonia.Controls;

namespace AeroCode.App.Views;

public partial class PermissionDialogView : UserControl
{
    /// <summary>用户做出决定或请求被取消时触发一次以上也无妨：宿主用 TrySet 语义消费。</summary>
    public event Action<PermissionDialogResult?>? Completed;

    public PermissionDialogView()
    {
        InitializeComponent();
    }

    public PermissionDialogView(PermissionPrompt prompt) : this()
    {
        ApplyPrompt(prompt);
    }

    public void ApplyPrompt(PermissionPrompt prompt)
    {
        ToolNameText.Text = prompt.ToolName;
        ArgsText.Text = string.IsNullOrWhiteSpace(prompt.ArgumentsPreview)
            ? "(无参数)"
            : prompt.ArgumentsPreview;
    }

    private void OnAllowClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => Completed?.Invoke(new PermissionDialogResult(Approved: true, Remember: RememberBox.IsChecked == true));

    private void OnDenyClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => Completed?.Invoke(new PermissionDialogResult(Approved: false, Remember: RememberBox.IsChecked == true));

    /// <summary>对话轮取消时由 presenter 调用：结束且不产生决定（broker 按拒绝处理）。</summary>
    public void CancelByRequest() => Completed?.Invoke(null);
}
