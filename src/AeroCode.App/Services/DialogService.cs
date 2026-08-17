using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace AeroCode.App.Services;

public interface IDialogService
{
    Task ShowMessageAsync(string title, string message);
    Task<bool> ConfirmAsync(string title, string message);
}

public class DialogService : IDialogService
{
    public async Task ShowMessageAsync(string title, string message)
    {
        var dlg = new Window
        {
            Title = title,
            Width = 420,
            Height = 200,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var okBtn = new Button { Content = "确定", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
        okBtn.Click += (_, _) => dlg.Close();
        dlg.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                okBtn
            }
        };
        if (TryGetOwner(out var owner)) await dlg.ShowDialog(owner);
        else dlg.Show();
    }

    public async Task<bool> ConfirmAsync(string title, string message)
    {
        bool result = false;
        var dlg = new Window
        {
            Title = title,
            Width = 420,
            Height = 200,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var okBtn = new Button { Content = "确定" };
        var cancelBtn = new Button { Content = "取消" };
        okBtn.Click += (_, _) => { result = true; dlg.Close(); };
        cancelBtn.Click += (_, _) => dlg.Close();
        dlg.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancelBtn, okBtn }
                }
            }
        };
        if (TryGetOwner(out var owner)) await dlg.ShowDialog(owner);
        else dlg.Show();
        return result;
    }

    private static bool TryGetOwner(out Window owner)
    {
        var lifetime = Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
        owner = lifetime?.MainWindow!;
        return owner is not null;
    }
}
