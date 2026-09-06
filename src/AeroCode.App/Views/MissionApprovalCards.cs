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
    // 与 DialogService/MissionView 相同的卡片配色（无样式资源依赖，纯代码构建）。
    private static readonly SolidColorBrush CardBg = new(Color.FromRgb(0x16, 0x1A, 0x23));
    private static readonly SolidColorBrush CardBorder = new(Color.FromRgb(0x2A, 0x31, 0x42));
    private static readonly SolidColorBrush FgPrimary = new(Color.FromRgb(0xE5, 0xE9, 0xF0));
    private static readonly SolidColorBrush FgMuted = new(Color.FromRgb(0x8A, 0x93, 0xA6));
    private static readonly SolidColorBrush AccentAmber = new(Color.FromRgb(0xF5, 0x9E, 0x0B));
    private static readonly SolidColorBrush AccentGreen = new(Color.FromRgb(0x10, 0xB9, 0x81));
    private static readonly SolidColorBrush AccentRed = new(Color.FromRgb(0xEF, 0x44, 0x44));

    /// <summary>
    /// 构建一张审批卡。<paramref name="onDecided"/> 在批准/拒绝任一决策发生后被调用
    ///（参数为卡片自身 Border，宿主用它调 OverlayService.CloseOverlay 收层）。
    /// </summary>
    public static Border Build(MissionEscalationCardViewModel card, Action<Border> onDecided)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(onDecided);

        var approveBtn = new Button
        {
            Content = "✅ 批准（消费一次性凭据）",
            Background = AccentGreen,
            Foreground = Brushes.White,
        };
        var rejectBtn = new Button
        {
            Content = "✕ 拒绝",
            Background = AccentRed,
            Foreground = Brushes.White,
        };

        var cardBorder = new Border
        {
            Background = CardBg,
            BorderBrush = CardBorder,
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
                        Foreground = AccentAmber,
                        TextWrapping = TextWrapping.Wrap,
                    },
                    new TextBlock
                    {
                        Text = card.Reason,
                        Foreground = FgPrimary,
                        TextWrapping = TextWrapping.Wrap,
                    },
                    new TextBlock
                    {
                        // 凭据恒为掩码文案（结构上无掩码字段 → 明示"已隐藏"，绝不回显原文）。
                        Text = $"凭据：{card.Credential}",
                        Foreground = FgMuted,
                        FontSize = 11,
                        TextWrapping = TextWrapping.Wrap,
                    },
                    new TextBlock
                    {
                        Text = $"升级时刻（本地）：{card.RaisedAt}",
                        Foreground = FgMuted,
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

            onDecided(cardBorder);
        };
        rejectBtn.Click += (_, _) =>
        {
            if (card.RejectCommand.CanExecute(null))
            {
                card.RejectCommand.Execute(null);
            }

            onDecided(cardBorder);
        };
        return cardBorder;
    }
}
