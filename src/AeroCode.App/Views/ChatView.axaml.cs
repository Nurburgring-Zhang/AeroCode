using System;
using System.Collections.Specialized;
using System.ComponentModel;
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
        _ = _vm.InitializeAsync();
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
    }
}
