// Copyright (c) AeroCode V3.0
// PermissionDialog — 桌面授权窗口薄壳。内容在 PermissionDialogView（与 Android 覆盖层共享）。
using System;
using AeroCode.App.Services;
using Avalonia.Controls;

namespace AeroCode.App.Views;

public partial class PermissionDialog : Window
{
    private readonly PermissionDialogView _view = new();

    public PermissionDialog()
    {
        InitializeComponent();
        _view.Completed += result =>
        {
            try
            {
                Close(result);
            }
            catch (InvalidOperationException)
            {
                // 窗口已关闭（决定与取消竞态）：首个决定已随 ShowDialog 返回，忽略后续。
            }
        };
        Content = _view;
    }

    public PermissionDialog(PermissionPrompt prompt) : this()
    {
        _view.ApplyPrompt(prompt);
    }

    /// <summary>对话轮取消时由 presenter 调用：关闭且不产生决定（broker 按拒绝处理）。</summary>
    public void CloseByCancellation()
    {
        try
        {
            _view.CancelByRequest();
        }
        catch (InvalidOperationException)
        {
            // 对话框已关闭/尚未显示完成：无决定即拒绝，无需再处理。
        }
    }
}
