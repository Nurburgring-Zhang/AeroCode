using Avalonia.Controls;
using Avalonia.Interactivity;
using AeroCode.App.ViewModels;

namespace AeroCode.App.Views;

public partial class DiagnosticsView : UserControl
{
    public DiagnosticsView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    /// <summary>
    /// 首载时触发 VM 的异步初始化（provider 健康检查等），替代旧版构造函数内
    /// 同步阻塞网络的 GetAwaiter().GetResult()。Loaded 只在 UI 线程发生，
    /// ObservableCollection 的后续变更经 UI 同步上下文回到 UI 线程。
    /// </summary>
    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (DataContext is DiagnosticsViewModel vm)
            {
                await vm.EnsureInitialLoadAsync();
            }
        }
        catch
        {
            // EnsureInitialLoadAsync 内部已全捕获（落到 StatusText），此兜底仅为
            // 防御 async-void 事件处理器的未观察异常语义。
        }
    }
}
