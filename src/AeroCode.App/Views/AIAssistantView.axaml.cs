using AeroCode.App.ViewModels;
using Avalonia.Controls;

namespace AeroCode.App.Views;

public partial class AIAssistantView : UserControl
{
    public AIAssistantView()
    {
        InitializeComponent();
        // 视图自身 DataContext 必须是 AIAssistantViewModel（编译绑定 x:DataType 所指）；
        // 不设则继承 MainView 的 MainWindowViewModel，全部内容绑定静默失败、面板空白。
        DataContext ??= App.Services.GetService(typeof(AIAssistantViewModel));
    }
}
