using System;
using System.Globalization;
using AeroCode.App.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace AeroCode.App.Converters;

public class DateTimeFormatConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is DateTime dt) return dt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        if (value is DateTimeOffset dto) return dto.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        return string.Empty;
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public class BoolToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var b = value is bool x && x;
        if (!b)
        {
            return Brushes.Transparent;
        }

        // 置顶指示色 = 主题 Accent 令牌（随 Light/Dark 切换），无应用上下文时回落常量。
        if (Application.Current is { } app &&
            app.TryFindResource("Accent", out var res) && res is IBrush brush)
        {
            return brush;
        }

        return new SolidColorBrush(Color.Parse("#5B9DFF"));
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public class StringNotEmptyToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string s && !string.IsNullOrWhiteSpace(s);
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public class CountToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int n && n > 0;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>int 值与 ConverterParameter 相等 → true（R5 侧栏导航内容面板切换）。</summary>
public class IndexEqualsToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int v && parameter is string s && int.TryParse(s, out var target))
        {
            return v == target;
        }

        return false;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>把 Markdown 字符串渲染为 Avalonia 控件（复用 MarkdownRenderer）。</summary>
public class MarkdownToControlConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var md = value as string;
        if (string.IsNullOrEmpty(md))
        {
            return new TextBlock { Text = string.Empty };
        }

        try
        {
            return MarkdownRenderer.Render(md, 14);
        }
        catch
        {
            // 渲染失败时退化为纯文本，保证对话流不中断。
            return new TextBlock { Text = md, TextWrapping = TextWrapping.Wrap };
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>true → 不透明，false → 半透明（流式占位提示用）。</summary>
public class BoolToOpacityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && b ? 1.0 : 0.45;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
