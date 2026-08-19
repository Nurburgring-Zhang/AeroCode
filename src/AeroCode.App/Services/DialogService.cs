using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Microsoft.Extensions.DependencyInjection;

namespace AeroCode.App.Services;

public interface IDialogService
{
    Task ShowMessageAsync(string title, string message);
    Task<bool> ConfirmAsync(string title, string message);
}

/// <summary>
/// 消息/确认对话框：桌面 = 模态 Window（历史行为）；single-view（Android）= OverlayService 覆盖层。
/// </summary>
public class DialogService : IDialogService
{
    private static readonly SolidColorBrush CardBg = new(Color.FromRgb(0x16, 0x1A, 0x23));
    private static readonly SolidColorBrush CardBorder = new(Color.FromRgb(0x2A, 0x31, 0x42));
    private static readonly SolidColorBrush FgPrimary = new(Color.FromRgb(0xE5, 0xE9, 0xF0));
    private static readonly SolidColorBrush FgMuted = new(Color.FromRgb(0x8A, 0x93, 0xA6));

    public async Task ShowMessageAsync(string title, string message)
    {
        if (TryGetOverlay(out var overlay))
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var okBtn = new Button { Content = "确定", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
            var card = BuildCard(title, message, okBtn);
            okBtn.Click += (_, _) =>
            {
                overlay.CloseOverlay(card);
                tcs.TrySetResult();
            };
            await overlay.ShowAsync(card);
            // ShowAsync 返回 = 覆盖层已移除（含系统返回键直接关闭）：
            // 幂等补齐结果，避免 tcs.Task 永挂。
            tcs.TrySetResult();
            await tcs.Task;
            return;
        }

        if (IsSingleViewLifetime())
        {
            // [DEGRADED] single-view 生命周期但覆盖层宿主未挂载（MainView 尚未加载完成）：
            // 无 Window 可用，降级跳过弹窗，不让主流程崩溃。
            Console.Error.WriteLine($"[DEGRADED] ShowMessageAsync 无可用呈现路径，跳过: {title} - {message}");
            return;
        }

        var dlg = new Window
        {
            Title = title,
            Width = 420,
            Height = 200,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var okBtn2 = new Button { Content = "确定", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
        okBtn2.Click += (_, _) => dlg.Close();
        dlg.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                okBtn2
            }
        };
        if (TryGetOwner(out var owner)) await dlg.ShowDialog(owner);
        else dlg.Show();
    }

    public async Task<bool> ConfirmAsync(string title, string message)
    {
        if (TryGetOverlay(out var overlay))
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var okBtn = new Button { Content = "确定" };
            var cancelBtn = new Button { Content = "取消" };
            var card = BuildCard(title, message,
                new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancelBtn, okBtn }
                });
            okBtn.Click += (_, _) =>
            {
                overlay.CloseOverlay(card);
                tcs.TrySetResult(true);
            };
            cancelBtn.Click += (_, _) =>
            {
                overlay.CloseOverlay(card);
                tcs.TrySetResult(false);
            };
            await overlay.ShowAsync(card);
            // ShowAsync 返回 = 覆盖层已移除（含系统返回键直接关闭）：
            // 幂等补齐结果（无明确确认 → false），避免 tcs.Task 永挂。
            tcs.TrySetResult(false);
            return await tcs.Task;
        }

        if (IsSingleViewLifetime())
        {
            // [DEGRADED] single-view 生命周期但覆盖层宿主未挂载：
            // 无 Window 可用，降级按"取消"处理（不做破坏性默认确认）。
            Console.Error.WriteLine($"[DEGRADED] ConfirmAsync 无可用呈现路径，按取消处理: {title} - {message}");
            return false;
        }

        bool result = false;
        var dlg = new Window
        {
            Title = title,
            Width = 420,
            Height = 200,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var okBtn2 = new Button { Content = "确定" };
        var cancelBtn2 = new Button { Content = "取消" };
        okBtn2.Click += (_, _) => { result = true; dlg.Close(); };
        cancelBtn2.Click += (_, _) => dlg.Close();
        dlg.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancelBtn2, okBtn2 }
                }
            }
        };
        if (TryGetOwner(out var owner)) await dlg.ShowDialog(owner);
        else dlg.Show();
        return result;
    }

    /// <summary>覆盖层卡片（与设置/授权弹层同一视觉语言）。</summary>
    private static Border BuildCard(string title, string message, Control actions)
    {
        return new Border
        {
            Background = CardBg,
            BorderBrush = CardBorder,
            BorderThickness = new Avalonia.Thickness(1),
            CornerRadius = new CornerRadius(8),
            MaxWidth = 440,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Padding = new Avalonia.Thickness(20),
            Child = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = title, FontWeight = Avalonia.Media.FontWeight.SemiBold, Foreground = FgPrimary },
                    new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Foreground = FgMuted },
                    actions
                }
            }
        };
    }

    private static bool TryGetOwner(out Window owner)
    {
        var lifetime = Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
        owner = lifetime?.MainWindow!;
        return owner is not null;
    }

    /// <summary>是否运行在无 Window 的 single-view 生命周期（Android）。</summary>
    private static bool IsSingleViewLifetime()
        => Avalonia.Application.Current?.ApplicationLifetime is ISingleViewApplicationLifetime;

    /// <summary>仅当运行在无 Window 的 single-view 生命周期且覆盖层宿主已挂载时返回 true。</summary>
    private static bool TryGetOverlay(out OverlayService overlay)
    {
        overlay = null!;
        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime)
        {
            return false;
        }
        try
        {
            overlay = App.Services.GetRequiredService<OverlayService>();
            return overlay.HasHost;
        }
        catch
        {
            return false;
        }
    }
}
