using AeroCode.App.ViewModels;
using Avalonia.Controls;
namespace AeroCode.App.Views;
public partial class SkillsView : UserControl
{
    public SkillsView()
    {
        InitializeComponent();
        DataContext ??= App.Services.GetService(typeof(SkillsViewModel));
    }
}
