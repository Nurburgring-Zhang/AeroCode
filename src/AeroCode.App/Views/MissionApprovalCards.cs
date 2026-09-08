// Copyright (c) AeroCode
// MissionApprovalCards — F-M5 升级审批卡片的视觉构建。
// 复用 OverlayService 卡片模式（与 DialogService.BuildCard / 设置/授权弹层同一视觉语言）：
// 本工厂只负责视觉与按钮接线；决策语义全部在卡片 VM（MissionEscalationCardViewModel）
// 与宿主 MissionViewModel。按钮 Click 显式走 VM 命令（不绑 Button.Command，避免
// 点击事件与命令执行顺序依赖），决策后经 onDecided 收层（OverlayService.CloseOverlay）。
using System;
using AeroCode.App.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace AeroCode.App.Views;

/// <summary>升级审批卡片工厂：OverlayService.ShowAsync 的 content 由本工厂产出。</summary>
public static class MissionApprovalCards
{
    // R5：配色运行时解析主题令牌（随 Light/Dark 切换），无应用上下文时回落深色常量。
    private static readonly SolidColorBrush FallbackCardBg = new(Color.FromRgb(0x24, 0x24, 0x24));
    private static readonly SolidColorBrush FallbackCardBorder = new(Color.FromRgb(0x2E, 0x2E, 0x2E));
    private static readonly SolidColorBrush FallbackFg = new(Color.FromRgb(0xF0, 0xF0, 0xF0));
    private static readonly SolidColorBrush FallbackMuted = new(Color.FromRgb(0x9A, 0x9A, 0x9A));
    private static readonly SolidColorBrush FallbackAccent = new(Color.FromRgb(0x5B, 0x9D, 0xFF));
    private static readonly SolidColorBrush FallbackDanger = new(Color.FromRgb(0xF8, 0x71, 0x71));

    private static IBrush Resolve(string key, IBrush fallback) =>
        Application.Current is { } app && app.TryFindResource(key, out var v) && v is IBrush b
            ? b
            : fallback;

    /// <summary>
    /// 构建一张审批卡。<paramref name="onDecided"/> 在批准/拒绝任一决策发生后被调用
    ///（参数为卡片自身 Border，宿主用它调 OverlayService.CloseOverlay 收层）。
    /// </summary>
    public static Border Build(MissionEscalationCardViewModel card, Action<Border> onDecided)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(onDecided);

        var cardBg = Resolve("BgElevated", FallbackCardBg);
        var cardBorder = Resolve("Border", FallbackCardBorder);
        var fgPrimary = Resolve("FgPrimary", FallbackFg);
        var fgMuted = Resolve("FgMuted", FallbackMuted);
        var accent = Resolve("Accent", FallbackAccent);
        var danger = Resolve("Danger", FallbackDanger);

        var approveBtn = new Button
        {
            Content = "批准（消费一次性凭据）",
            Background = accent,
            Foreground = Brushes.White,
        };
        var rejectBtn = new Button
        {
            Content = "✕ 拒绝",
            Background = danger,
            Foreground = Brushes.White,
        };

        var cardBorderCtrl = new Border
        {
            Background = cardBg,
            BorderBrush = cardBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            MaxWidth = 460,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(20),
            Child = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new TextBlock
                    {
                        Text = card.Title,
                        FontWeight = FontWeight.SemiBold,
                        Foreground = accent,
                        TextWrapping = TextWrapping.Wrap,
                    },
                    new TextBlock
                    {
                        Text = card.Reason,
                        Foreground = fgPrimary,
                        TextWrapping = TextWrapping.Wrap,
                    },
                    new TextBlock
                    {
                        // 凭据恒为掩码文案（结构上无掩码字段 → 明示"已隐藏"，绝不回显原文）。
                        Text = $"凭据：{card.Credential}",
                        Foreground = fgMuted,
                        FontSize = 11,
                        TextWrapping = TextWrapping.Wrap,
                    },
                    new TextBlock
                    {
                        Text = $"升级时刻（本地）：{card.RaisedAt}",
                        Foreground = fgMuted,
                        FontSize = 10,
                    },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { rejectBtn, approveBtn },
                    },
                },
            },
        };

        approveBtn.Click += (_, _) =>
        {
            if (card.ApproveCommand.CanExecute(null))
            {
                card.ApproveCommand.Execute(null);
            }

            onDecided(cardBorderCtrl);
        };
        rejectBtn.Click += (_, _) =>
        {
            if (card.RejectCommand.CanExecute(null))
            {
                card.RejectCommand.Execute(null);
            }

            onDecided(cardBorderCtrl);
        };
        return cardBorderCtrl;
    }
}
