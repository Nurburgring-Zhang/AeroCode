// Copyright (c) AeroCode V3.0
// AvaloniaPermissionDialogPresenter — shows the real PermissionDialog on the UI thread.
// 桌面：模态 Window；Android（single-view）：OverlayService 覆盖层。两条路径都是真实 UI 授权。
using AeroCode.App.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;

namespace AeroCode.App.Services;

/// <summary>
/// 生产对话框呈现层：把授权对话框调度到 UI 线程呈现。
/// 桌面 = 主窗口之上的模态 Window；single-view（Android）= MainView 覆盖层。
/// 无界面生命周期（设计器、无头测试）时返回 null —— broker 按拒绝处理，绝不静默放行。
/// </summary>
public sealed class AvaloniaPermissionDialogPresenter : IPermissionDialogPresenter
{
    private static readonly SolidColorBrush CardBg = new(Color.FromRgb(0x16, 0x1A, 0x23));
    private static readonly SolidColorBrush CardBorder = new(Color.FromRgb(0x2A, 0x31, 0x42));

    public async Task<PermissionDialogResult?> ShowAsync(PermissionPrompt prompt, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return null;
        }

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime
            && lifetime.MainWindow is { } owner)
        {
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

        if (Application.Current?.ApplicationLifetime is ISingleViewApplicationLifetime)
        {
            return await ShowViaOverlayAsync(prompt, ct);
        }

        return null;
    }

    /// <summary>Android（single-view）：授权视图经 OverlayService 全屏覆盖层呈现，语义与桌面一致。</summary>
    private static async Task<PermissionDialogResult?> ShowViaOverlayAsync(PermissionPrompt prompt, CancellationToken ct)
    {
        OverlayService overlay;
        try
        {
            overlay = App.Services.GetRequiredService<OverlayService>();
        }
        catch
        {
            return null; // 服务容器不可用：无决定 → broker 按拒绝
        }
        if (!overlay.HasHost)
        {
            return null;
        }

        return await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var view = new PermissionDialogView(prompt);
            var resultTcs = new TaskCompletionSource<PermissionDialogResult?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            view.Completed += result =>
            {
                overlay.CloseOverlay(view);
                resultTcs.TrySetResult(result);
            };
            // 对话轮取消 → 结束且不产生决定（与桌面 CloseByCancellation 同语义）。
            using var registration = ct.Register(
                () => Dispatcher.UIThread.Post(view.CancelByRequest));

            var card = new Border
            {
                Background = CardBg,
                BorderBrush = CardBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                MaxWidth = 560,
                MaxHeight = 480,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Child = view
            };
            try
            {
                await overlay.ShowAsync(card);
                // ShowAsync 返回 = 覆盖层已移除（含系统返回键经 TryCloseTop 关闭、
                // 未经过 view.Completed 的情况）。此时若视图尚未产生决定，
                // 补 null → broker 按拒绝收尾，避免 Task 永挂。
                // TrySetResult 幂等：用户已作出决定时此调用为 no-op。
                resultTcs.TrySetResult(null);
                return await resultTcs.Task;
            }
            catch (InvalidOperationException)
            {
                // 宿主尚未挂载等竞态：拿不到用户决定，交由 broker 诚实拒绝。
                return null;
            }
        });
    }
}
