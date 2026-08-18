// Copyright (c) AeroCode V3.0
// OverlayService — single-view 平台（Android）的对话框承载层。
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace AeroCode.App.Services;

/// <summary>
/// 全屏覆盖层宿主：single-view 平台（Android）没有 Window，
/// 设置/授权/消息等"对话框"内容以覆盖层形式挂载在 MainView 的 OverlayRoot 之上。
/// 桌面仍走 Window 模态（历史行为不变），本服务仅在无 Window 环境使用。
/// 所有调用必须在 UI 线程。
/// </summary>
public sealed class OverlayService
{
    private Panel? _host;
    private readonly Dictionary<Border, Action> _removers = new();

    /// <summary>覆盖层宿主是否已挂载（MainView 加载完成后可用）。</summary>
    public bool HasHost => _host is not null;

    /// <summary>由 MainView 在 Loaded 时挂载宿主面板。</summary>
    public void AttachHost(Panel host) => _host = host;

    /// <summary>
    /// 将内容作为全屏覆盖层呈现（半透明遮罩 + 内容本身负责卡片样式）。
    /// 返回的 Task 在覆盖层被移除时完成。
    /// </summary>
    public Task ShowAsync(Control content)
    {
        var host = _host ?? throw new InvalidOperationException("覆盖层宿主尚未挂载（MainView 未加载？）");
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var container = new Border
        {
            Background = new SolidColorBrush(new Color(180, 0, 0, 0)),
            Padding = new Thickness(10),
            Child = content
        };
        void Remove()
        {
            if (host.Children.Contains(container))
            {
                host.Children.Remove(container);
            }
            tcs.TrySetResult();
        }
        _removers[container] = Remove;
        host.Children.Add(container);
        return tcs.Task;
    }

    /// <summary>移除包含 descendant 的覆盖层（向上遍历定位容器）；已移除则为 no-op。</summary>
    public void CloseOverlay(Control descendant)
    {
        for (Control? cur = descendant; cur is not null; cur = cur.Parent as Control)
        {
            if (cur is Border b && _removers.TryGetValue(b, out var remove))
            {
                _removers.Remove(b);
                remove();
                return;
            }
        }
    }
}
