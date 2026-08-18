// Copyright (c) AeroCode V3.0
// SettingsView — 设置视图（平台无关）。桌面由 SettingsDialog(Window) 承载，
// Android 经 OverlayService 全屏覆盖层呈现。关闭语义经 CloseRequested 事件交给宿主。
using System;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace AeroCode.App.Views;

public partial class SettingsView : UserControl
{
    /// <summary>用户请求关闭（Cancel 按钮）时触发；宿主决定如何关（Window.Close / 移除覆盖层）。</summary>
    public event Action? CloseRequested;

    public SettingsView()
    {
        InitializeComponent();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => CloseRequested?.Invoke();
}
