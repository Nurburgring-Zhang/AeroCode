// Copyright (c) AeroCode V3.0
// MarkdownRenderer tests — covers Markdig → Avalonia Inlines conversion.
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Xunit;

namespace AeroCode.Tests.ServiceTests;

public class MarkdownRendererTests
{
    [Fact]
    public void Empty_ReturnsValidControl()
    {
        var c = AeroCode.App.Views.MarkdownRenderer.Render(null);
        Assert.NotNull(c);
    }

    [Fact]
    public void SimpleParagraph_ProducesStackPanel()
    {
        var c = AeroCode.App.Views.MarkdownRenderer.Render("Hello world");
        var scroll = Assert.IsType<ScrollViewer>(c);
        var sp = Assert.IsType<StackPanel>(scroll.Content);
        Assert.Single(sp.Children!);
        var tb = Assert.IsType<TextBlock>(sp.Children![0]);
        Assert.Contains("Hello world", tb.Inlines!.OfType<Run>().Select(r => r.Text ?? ""));
    }

    [Fact]
    public void Headings_AreBoldAndLarger()
    {
        var c = AeroCode.App.Views.MarkdownRenderer.Render("# Title 1\n\n## Title 2");
        var sp = Assert.IsType<StackPanel>(((ScrollViewer)c).Content);
        // At least 2 heading TextBlocks
        Assert.True(sp.Children!.Count >= 2, $"expected >= 2 children, got {sp.Children.Count}");
        var headings = sp.Children!.OfType<TextBlock>().Where(t => t.FontWeight == FontWeight.Bold && t.FontSize > 14).ToList();
        Assert.True(headings.Count >= 2, $"expected >= 2 headings, got {headings.Count}");
    }

    [Fact]
    public void BoldAndItalic_InlineFormatting()
    {
        var c = AeroCode.App.Views.MarkdownRenderer.Render("**bold** and *italic*");
        var sp = Assert.IsType<StackPanel>(((ScrollViewer)c).Content);
        var tb = Assert.IsType<TextBlock>(sp.Children![0]);
        Assert.Contains(tb.Inlines!.OfType<Run>(), r => r.FontWeight == FontWeight.Bold);
        Assert.Contains(tb.Inlines!.OfType<Run>(), r => r.FontStyle == FontStyle.Italic);
    }

    [Fact]
    public void CodeBlock_RendersAsCodeFont()
    {
        var c = AeroCode.App.Views.MarkdownRenderer.Render("```\nint x = 42;\n```");
        var sp = Assert.IsType<StackPanel>(((ScrollViewer)c).Content);
        var tb = Assert.IsType<TextBlock>(sp.Children![0]);
        Assert.Contains("int x = 42;", tb.Text);
        Assert.NotNull(tb.FontFamily);
    }

    [Fact]
    public void BulletList_ProducesList()
    {
        var c = AeroCode.App.Views.MarkdownRenderer.Render("- a\n- b\n- c");
        var sp = Assert.IsType<StackPanel>(((ScrollViewer)c).Content);
        // One nested StackPanel (for the list), with 3 TextBlock children
        var listPanel = Assert.IsType<StackPanel>(sp.Children![0]);
        Assert.Equal(3, listPanel.Children!.Count);
    }

    [Fact]
    public void QuoteBlock_HasLeftBorder()
    {
        var c = AeroCode.App.Views.MarkdownRenderer.Render("> quoted text");
        var sp = Assert.IsType<StackPanel>(((ScrollViewer)c).Content);
        var border = Assert.IsType<Border>(sp.Children![0]);
        Assert.True(border.BorderThickness.Left >= 2);
    }
}
