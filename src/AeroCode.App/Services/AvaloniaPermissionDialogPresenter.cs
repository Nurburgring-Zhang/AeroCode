// Copyright (c) AeroCode V3.0
// AvaloniaPermissionDialogPresenter — shows the real PermissionDialog on the UI thread.
using AeroCode.App.Views;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace AeroCode.App.Services;

/// <summary>
/// 生产对话框呈现层：把授权对话框调度到 UI 线程并模态显示在主窗口之上。
/// 无主窗口/无界面生命周期（设计器、无头测试）时返回 null —— broker 按拒绝处理，绝不静默放行。
/// </summary>
public sealed class AvaloniaPermissionDialogPresenter : IPermissionDialogPresenter
{
    public async Task<PermissionDialogResult?> ShowAsync(PermissionPrompt prompt, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return null;
        }

        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime lifetime
            || lifetime.MainWindow is not { } owner)
        {
            return null;
        }

        return await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var dialog = new PermissionDialog(prompt);
            // 对话轮取消 → 立即关窗；未产生决定即按拒绝收尾。
            // Register 回调在触发取消的线程执行 → 必须调度回 UI 线程才能安全 Close。
            using var registration = ct.Register(
                () => Dispatcher.UIThread.Post(dialog.CloseByCancellation));
            try
            {
                return await dialog.ShowDialog<PermissionDialogResult?>(owner);
            }
            catch (InvalidOperationException)
            {
                // 属主窗口先行关闭等竞态：拿不到用户决定，交由 broker 诚实拒绝。
                return null;
            }
        });
    }
}
