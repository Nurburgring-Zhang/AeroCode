// Copyright (c) AeroCode V3.0
// ThemeService — applies UI theme to Avalonia 11 via RequestedThemeVariant.
// 3 variants: Light / Dark / System (follows OS).
using System;
using Avalonia;
using Avalonia.Styling;

namespace AeroCode.App.Services;

/// <summary>
/// 主题服务。Theme 字符串 "Light" / "Dark" / "System"。
/// 任何组件可调用 <see cref="Apply"/> 立即生效。
/// </summary>
public sealed class ThemeService
{
    public const string Light = "Light";
    public const string Dark = "Dark";
    public const string System = "System";

    public string Current { get; private set; } = Dark;

    /// <summary>应用主题。允许在 Avalonia 还未初始化时调用（no-op）。</summary>
    public void Apply(string theme)
    {
        if (string.IsNullOrWhiteSpace(theme)) theme = Dark;
        Current = theme;
        if (Application.Current is null) return;
        Application.Current.RequestedThemeVariant = theme switch
        {
            Light => ThemeVariant.Light,
            Dark => ThemeVariant.Dark,
            System => ThemeVariant.Default,
            _ => ThemeVariant.Dark
        };
    }
}
