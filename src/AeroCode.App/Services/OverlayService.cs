// Copyright (c) AeroCode V3.0
// OverlayService — single-view 平台（Android）的对话框承载层。
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;

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
    // 按打开顺序维护的覆盖层栈（末位 = 最上层）：供系统返回键逐层关闭。
    private readonly List<Border> _open = new();

    /// <summary>覆盖层宿主是否已挂载（MainView 加载完成后可用）。</summary>
    public bool HasHost => _host is not null;

    /// <summary>当前是否有打开的覆盖层（返回键拦截判定用）。</summary>
    public bool HasOpenOverlays => _open.Count > 0;

    /// <summary>由 MainView 在 Loaded 时挂载宿主面板。重挂时先收尾所有未关闭覆盖层的
    /// Task（经各自 remover），再清簿记——保证 ShowAsync 的 Task 绝不永挂。</summary>
    public void AttachHost(Panel host)
    {
        if (ReferenceEquals(_host, host))
        {
            return;
        }
        // Activity 重建防御：快照后逐个调用 remover（会修改两个集合）。
        foreach (var remove in _removers.Values.ToList())
        {
            remove();
        }
        _host = host;
        _open.Clear();
        _removers.Clear();
    }

    /// <summary>
    /// 将内容作为全屏覆盖层呈现（半透明遮罩 + 内容本身负责卡片样式）。
    /// 返回的 Task 在覆盖层被移除时完成（无论经由 CloseOverlay、TryCloseTop 还是宿主重挂）。
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
            _open.Remove(container);
            _removers.Remove(container);
            tcs.TrySetResult();
        }
        _removers[container] = Remove;
        _open.Add(container);
        host.Children.Add(container);
        return tcs.Task;
    }

    /// <summary>
    /// 移除包含 descendant 的覆盖层（向上遍历定位容器；脱离视觉树时按子树扫描兜底）。
    /// 返回是否实际移除了覆盖层——调用方据此判断 await 是否会有人收尾。
    /// </summary>
    public bool CloseOverlay(Control descendant)
    {
        for (Control? cur = descendant; cur is not null; cur = cur.Parent as Control)
        {
            if (cur is Border b && _removers.TryGetValue(b, out var remove))
            {
                remove();
                return true;
            }
        }

        // 兜底：descendant 已脱离视觉树（宿主重建等）时按子树归属查找，
        // 避免"找不到容器就静默放弃"导致 ShowAsync 的 Task 永挂。
        var match = _open.FirstOrDefault(c => ReferenceEquals(c.Child, descendant)
            || c.GetVisualDescendants().Any(d => ReferenceEquals(d, descendant)));
        if (match is not null && _removers.TryGetValue(match, out var fallbackRemove))
        {
            fallbackRemove();
            return true;
        }

        return false;
    }

    /// <summary>
    /// 关闭最上层覆盖层（Android 系统返回键语义：逐层关卡片而不是退出 App）。
    /// 无打开的覆盖层时返回 false，调用方交还系统默认行为。
    /// </summary>
    public bool TryCloseTop()
    {
        if (_open.Count == 0)
        {
            return false;
        }
        var top = _open[^1];
        if (_removers.TryGetValue(top, out var remove))
        {
            remove();
            return true;
        }
        // 簿记不一致的防御性清理：直接从宿主摘除并让对应 Task 完成。
        _open.RemoveAt(_open.Count - 1);
        _host?.Children.Remove(top);
        return true;
    }
}
