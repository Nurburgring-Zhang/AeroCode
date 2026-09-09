using AeroCode.App.ViewModels;
using Avalonia.Controls;
using Avalonia.Input;

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

    /// <summary>Enter 发送（Shift+Enter 换行）。</summary>
    private void OnUserInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            e.Handled = true;
            if (DataContext is AIAssistantViewModel vm && vm.SendCommand.CanExecute(null))
            {
                vm.SendCommand.Execute(null);
            }
        }
    }
}
