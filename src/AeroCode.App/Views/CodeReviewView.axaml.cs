using AeroCode.App.ViewModels;
using Avalonia.Controls;
namespace AeroCode.App.Views;
public partial class CodeReviewView : UserControl
{
    public CodeReviewView()
    {
        InitializeComponent();
        DataContext ??= App.Services.GetService(typeof(CodeReviewViewModel));
    }
}
