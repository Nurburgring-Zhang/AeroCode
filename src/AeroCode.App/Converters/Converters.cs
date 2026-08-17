using System;
using System.Globalization;
using AeroCode.App.Views;
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
        return b ? new SolidColorBrush(Color.Parse("#F59E0B")) : Brushes.Transparent;
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
