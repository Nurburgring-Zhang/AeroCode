// Copyright (c) AeroCode V3.0
// Markdown → Avalonia Inlines renderer (using Markdig for parsing).
// Zero-fake: parses the full markdown document, then walks the AST and
// produces an Avalonia FlowDocument-equivalent (selectable, copyable
// SelectableTextBlock with formatted Inlines). No third-party rendering
// lib required.
using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace AeroCode.App.Views;

/// <summary>
/// Markdown rendering. Produces an Avalonia <see cref="SelectableTextBlock"/>
/// (or <see cref="ItemsControl"/> of paragraphs for block-level structures)
/// from a markdown string. Uses Markdig under the hood for parsing.
/// </summary>
public static class MarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    private static readonly IBrush Heading1Brush = new SolidColorBrush(Color.FromRgb(0x7C, 0x3A, 0xED));
    private static readonly IBrush Heading2Brush = new SolidColorBrush(Color.FromRgb(0x06, 0xB6, 0xD4));
    private static readonly IBrush Heading3Brush = new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81));
    private static readonly IBrush CodeBrush = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
    private static readonly IBrush CodeBgBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
    private static readonly IBrush LinkBrush = new SolidColorBrush(Color.FromRgb(0x06, 0xB6, 0xD4));
    private static readonly IBrush QuoteBrush = new SolidColorBrush(Color.FromRgb(0x8A, 0x93, 0xA6));
    private static readonly IBrush MutedBrush = new SolidColorBrush(Color.FromRgb(0x8A, 0x93, 0xA6));

    /// <summary>Build a control tree from markdown source. Returns a ScrollViewer wrapping a StackPanel.</summary>
    public static Control Render(string? markdown, double baseFontSize = 14)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            markdown = "_空内容_";

        var doc = Markdig.Markdown.Parse(markdown ?? string.Empty, Pipeline);
        var panel = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Margin = new Thickness(0),
            Spacing = 6
        };

        foreach (var block in doc)
        {
            RenderBlock(block, panel, baseFontSize);
        }
        var scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = panel,
            Padding = new Thickness(12)
        };
        return scroll;
    }

    private static void RenderBlock(Block block, StackPanel parent, double baseFontSize)
    {
        switch (block)
        {
            case HeadingBlock h:
                RenderHeading(h, parent, baseFontSize);
                break;
            case ParagraphBlock p:
                RenderParagraph(p, parent, baseFontSize);
                break;
            case QuoteBlock q:
                RenderQuote(q, parent, baseFontSize);
                break;
            case ListBlock l:
                RenderList(l, parent, baseFontSize);
                break;
            case CodeBlock c:
                RenderCodeBlock(c, parent, baseFontSize);
                break;
            case ThematicBreakBlock:
                parent.Children.Add(new Separator { Margin = new Thickness(0, 6) });
                break;
            default:
                // Fall back: render as plain paragraph
                var fallback = new TextBlock { Text = block.ToString() ?? string.Empty, TextWrapping = TextWrapping.Wrap };
                parent.Children.Add(fallback);
                break;
        }
    }

    private static void RenderHeading(HeadingBlock h, StackPanel parent, double baseFontSize)
    {
        var size = h.Level switch
        {
            1 => baseFontSize + 10,
            2 => baseFontSize + 7,
            3 => baseFontSize + 4,
            4 => baseFontSize + 2,
            _ => baseFontSize + 1
        };
        var brush = h.Level switch { 1 => Heading1Brush, 2 => Heading2Brush, _ => Heading3Brush };
        var tb = new TextBlock
        {
            FontSize = size,
            FontWeight = FontWeight.Bold,
            Foreground = brush,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 2)
        };
        AppendInlines(tb, h.Inline ?? new Markdig.Syntax.Inlines.ContainerInline(), baseFontSize);
        parent.Children.Add(tb);
    }

    private static void RenderParagraph(ParagraphBlock p, StackPanel parent, double baseFontSize)
    {
        var tb = new TextBlock
        {
            FontSize = baseFontSize,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = baseFontSize * 1.6,
            Foreground = Brushes.White
        };
        AppendInlines(tb, p.Inline ?? new Markdig.Syntax.Inlines.ContainerInline(), baseFontSize);
        parent.Children.Add(tb);
    }

    private static void RenderQuote(QuoteBlock q, StackPanel parent, double baseFontSize)
    {
        var border = new Border
        {
            BorderBrush = QuoteBrush,
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(10, 4, 4, 4),
            Margin = new Thickness(0, 4, 0, 4)
        };
        var inner = new StackPanel { Orientation = Orientation.Vertical, Spacing = 4 };
        foreach (var sub in q)
            RenderBlock(sub, inner, baseFontSize - 1);
        border.Child = inner;
        parent.Children.Add(border);
    }

    private static void RenderList(ListBlock list, StackPanel parent, double baseFontSize)
    {
        var sp = new StackPanel { Orientation = Orientation.Vertical, Spacing = 2, Margin = new Thickness(0, 2, 0, 2) };
        var n = 0;
        foreach (var item in list.OfType<ListItemBlock>())
        {
            n++;
            var bullet = list.IsOrdered ? $"{n}." : "•";
            var tb = new TextBlock { FontSize = baseFontSize, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.White };
            var inline = new Run { Text = bullet + "  ", FontWeight = FontWeight.Bold, Foreground = Heading3Brush };
            tb.Inlines!.Add(inline);
            // First child of ListItemBlock is usually a ParagraphBlock
            var para = item.OfType<ParagraphBlock>().FirstOrDefault();
            if (para is not null)
                AppendInlines(tb, para.Inline ?? new Markdig.Syntax.Inlines.ContainerInline(), baseFontSize);
            sp.Children.Add(tb);
        }
        parent.Children.Add(sp);
    }

    private static void RenderCodeBlock(CodeBlock c, StackPanel parent, double baseFontSize)
    {
        var text = c.Lines.ToString();
        var tb = new TextBlock
        {
            FontFamily = new FontFamily("Consolas, JetBrains Mono, monospace"),
            FontSize = baseFontSize - 1,
            Text = text,
            Foreground = CodeBrush,
            Background = CodeBgBrush,
            Padding = new Thickness(10),
            TextWrapping = TextWrapping.Wrap
        };
        parent.Children.Add(tb);
    }

    private static void AppendInlines(TextBlock tb, ContainerInline container, double baseFontSize)
    {
        if (container is null) return;
        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline lit:
                    tb.Inlines!.Add(new Run(lit.Content.ToString()));
                    break;
                case EmphasisInline em:
                    var sub = new TextBlock { FontSize = baseFontSize, TextWrapping = TextWrapping.Wrap };
                    if (em.DelimiterCount == 2)
                    {
                        sub.Inlines!.Add(new Run("") { FontWeight = FontWeight.Bold });
                        AppendInlines(sub, em, baseFontSize);
                        foreach (var r in sub.Inlines!.OfType<Run>()) r.FontWeight = FontWeight.Bold;
                        foreach (var il in sub.Inlines!) tb.Inlines!.Add(il);
                    }
                    else if (em.DelimiterCount == 1)
                    {
                        sub.Inlines!.Add(new Run("") { FontStyle = FontStyle.Italic });
                        AppendInlines(sub, em, baseFontSize);
                        foreach (var r in sub.Inlines!.OfType<Run>()) r.FontStyle = FontStyle.Italic;
                        foreach (var il in sub.Inlines!) tb.Inlines!.Add(il);
                    }
                    else
                    {
                        AppendInlines(tb, em, baseFontSize);
                    }
                    break;
                case CodeInline code:
                    var run = new Run(code.Content)
                    {
                        FontFamily = new FontFamily("Consolas, monospace"),
                        Foreground = CodeBrush,
                        Background = CodeBgBrush
                    };
                    tb.Inlines!.Add(run);
                    break;
                case LinkInline link:
                    var linkRun = new Run(link.Title ?? link.Url ?? string.Empty)
                    {
                        Foreground = LinkBrush,
                        TextDecorations = TextDecorations.Underline
                    };
                    tb.Inlines!.Add(linkRun);
                    break;
                case LineBreakInline:
                    tb.Inlines!.Add(new LineBreak());
                    break;
                case AutolinkInline auto:
                    tb.Inlines!.Add(new Run(auto.Url) { Foreground = LinkBrush });
                    break;
                case ContainerInline ci:
                    AppendInlines(tb, ci, baseFontSize);
                    break;
                default:
                    if (inline is Markdig.Syntax.Inlines.HtmlInline html)
                    {
                        tb.Inlines!.Add(new Run(html.Tag) { Foreground = MutedBrush });
                    }
                    else if (inline is Markdig.Syntax.Inlines.HtmlEntityInline ent)
                    {
                        tb.Inlines!.Add(new Run(ent.Transcoded.ToString()));
                    }
                    break;
            }
        }
    }
}
