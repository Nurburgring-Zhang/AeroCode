using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using AeroCode.App.ViewModels;

namespace AeroCode.App.Views;

/// <summary>
/// 统一对话视图：Enter 发送（Shift+Enter 换行），流式期间自动滚动到底部。
/// </summary>
public partial class ChatView : UserControl
{
    private ScrollViewer? _scroller;
    private ChatViewModel? _vm;
    private MessageItemViewModel? _trackedStreamingMessage;

    public ChatView()
    {
        InitializeComponent();
        // 应用无隐式 VM→View 装配，显式从 DI 解析（与 SettingsDialog 同模式）。
        DataContext ??= App.Services.GetService(typeof(ChatViewModel));
        DataContextChanged += OnDataContextChanged;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
        _scroller = this.FindControl<ScrollViewer>("MessageScroller");
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm is not null)
        {
            _vm.Messages.CollectionChanged -= OnMessagesChanged;
            _vm.PropertyChanged -= OnVmPropertyChanged;
        }

        _vm = DataContext as ChatViewModel;
        if (_vm is null)
        {
            return;
        }

        _vm.Messages.CollectionChanged += OnMessagesChanged;
        _vm.PropertyChanged += OnVmPropertyChanged;
        _ = ObserveInitializeAsync(_vm);
    }

    /// <summary>初始化失败不应静默：把异常落到 VM 状态栏，保持可见。</summary>
    private static async Task ObserveInitializeAsync(ChatViewModel vm)
    {
        try
        {
            await vm.InitializeAsync();
        }
        catch (Exception ex)
        {
            vm.StatusText = $"✗ 初始化失败: {ex.Message}";
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 会话切换后回到顶部由集合变更触发；此处只处理流式滚动。
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is null)
        {
            return;
        }

        foreach (var item in e.NewItems)
        {
            if (item is MessageItemViewModel m && m.IsAssistant)
            {
                TrackStreamingMessage(m);
            }
        }

        ScrollToBottom();
    }

    private void TrackStreamingMessage(MessageItemViewModel m)
    {
        if (_trackedStreamingMessage is not null)
        {
            _trackedStreamingMessage.PropertyChanged -= OnStreamingContentChanged;
        }

        _trackedStreamingMessage = m;
        m.PropertyChanged += OnStreamingContentChanged;
    }

    private void OnStreamingContentChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MessageItemViewModel.Content))
        {
            ScrollToBottom();
        }
    }

    private void ScrollToBottom()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_scroller is null)
            {
                return;
            }

            _scroller.ScrollToEnd();
        }, DispatcherPriority.Background);
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            e.Handled = true;
            if (_vm is not null && _vm.SendCommand.CanExecute(null))
            {
                _vm.SendCommand.Execute(null);
            }
        }
        else if (e.Key == Key.V && e.KeyModifiers.HasFlag(KeyModifiers.Control) && _vm is not null)
        {
            // R4-γ：Ctrl+V 粘贴图片附件（仅处理图片；文本粘贴由 TextBox 原生处理）。
            _ = TryPasteImageFromClipboardAsync();
        }
    }

    private async Task TryPasteImageFromClipboardAsync()
    {
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null)
            {
                return;
            }

            var formats = await clipboard.GetFormatsAsync();
            if (formats is null)
            {
                return;
            }

            // 按优先级检查常见图片格式。
            string[] imageFormats = { "image/png", "image/jpeg", "image/gif", "image/webp" };
            foreach (var mime in imageFormats)
            {
                if (!Array.Exists(formats, f => f.Equals(mime, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var data = await clipboard.GetDataAsync(mime);
                if (data is not byte[] bytes || bytes.Length == 0)
                {
                    continue;
                }

                var ext = mime.Split('/')[^1];
                if (ext == "jpeg") ext = "jpg";
                _vm!.AttachFromClipboard(bytes, $"clipboard.{ext}", mime);
                return;
            }
        }
        catch
        {
            // 剪贴板访问失败不阻塞输入：静默忽略（用户可重试或用文件选择器）。
        }
    }
}
