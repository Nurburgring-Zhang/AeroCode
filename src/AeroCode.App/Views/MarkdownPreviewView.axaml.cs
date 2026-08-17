using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace AeroCode.App.Views;

public partial class MarkdownPreviewView : UserControl
{
    public MarkdownPreviewView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>Update the rendered markdown. Renders to "(空)" if null/empty.</summary>
    public void SetMarkdown(string? markdown)
    {
        try
        {
            var control = MarkdownRenderer.Render(markdown, 14);
            PreviewHost.Child = control;
        }
        catch { /* keep UI alive if render fails */ }
    }
}
