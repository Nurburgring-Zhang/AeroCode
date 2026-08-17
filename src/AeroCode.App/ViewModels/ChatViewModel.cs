using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AeroAgent.Conversation.Models;
using AeroAgent.Conversation.Orchestration;
using AeroAgent.Conversation.Services;
using AeroCode.AI.Providers;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AeroCode.App.ViewModels;

/// <summary>会话列表项。</summary>
public partial class SessionItemViewModel : ObservableObject
{
    public string Id { get; init; } = string.Empty;

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private bool _isPinned;

    public OrchestrationStrategy Strategy { get; init; }
    public DateTime UpdatedAtUtc { get; init; }

    public string DisplayTime => UpdatedAtUtc.ToLocalTime().ToString("MM-dd HH:mm");
}

/// <summary>消息渲染项。流式过程中 Content 持续增长。</summary>
public partial class MessageItemViewModel : ObservableObject
{
    public string Id { get; init; } = string.Empty;
    public ChatRole Role { get; init; }
    public bool IsUser => Role == ChatRole.User;
    public bool IsAssistant => Role == ChatRole.Assistant;

    [ObservableProperty]
    private string _content = string.Empty;

    [ObservableProperty]
    private string? _reasoningContent;

    [ObservableProperty]
    private MessageStatus _status = MessageStatus.Completed;

    [ObservableProperty]
    private string? _errorText;

    public string? ProviderId { get; init; }
    public string? ModelId { get; init; }
    public StrategyRole OrchestrationRole { get; init; }

    /// <summary>模型归属徽章文本（模型名；编排角色非 None 时附加角色）。</summary>
    public string? AttributionBadge
    {
        get
        {
            if (ModelId is null)
            {
                return null;
            }

            return OrchestrationRole == StrategyRole.None
                ? ModelId
                : $"{ModelId} · {RoleName(OrchestrationRole)}";
        }
    }

    private static string RoleName(StrategyRole role) => role switch
    {
        StrategyRole.Router => "路由",
        StrategyRole.Planner => "规划",
        StrategyRole.Worker => "执行",
        StrategyRole.Judge => "评审",
        StrategyRole.Synthesizer => "汇总",
        _ => string.Empty,
    };
}

/// <summary>
/// 统一对话视图模型：会话 CRUD + 流式对话 + 事件流消费。
/// 持久化全部由 <see cref="IChatOrchestrationFacade"/> 负责，
/// 本 VM 只做 UI 投影（Dispatcher 保证 UI 线程安全）。
/// </summary>
public partial class ChatViewModel : ObservableObject
{
    private readonly ISessionService _sessions;
    private readonly IChatOrchestrationFacade _facade;
    private readonly IProviderRegistry _providers;

    private CancellationTokenSource? _streamCts;
    private string? _currentAssistantMessageId;

    public ChatViewModel(
        ISessionService sessions,
        IChatOrchestrationFacade facade,
        IProviderRegistry providers)
    {
        _sessions = sessions;
        _facade = facade;
        _providers = providers;

        ProviderIds = new ObservableCollection<string>(providers.ListConfiguredIds());
        _selectedProviderId = ProviderIds.FirstOrDefault() ?? string.Empty;
        Strategies = new ObservableCollection<OrchestrationStrategy>(
            Enum.GetValues<OrchestrationStrategy>());
    }

    public ObservableCollection<SessionItemViewModel> Sessions { get; } = new();
    public ObservableCollection<MessageItemViewModel> Messages { get; } = new();
    public ObservableCollection<string> ProviderIds { get; }
    public ObservableCollection<OrchestrationStrategy> Strategies { get; }

    [ObservableProperty]
    private SessionItemViewModel? _selectedSession;

    [ObservableProperty]
    private string _inputText = string.Empty;

    [ObservableProperty]
    private bool _isStreaming;

    [ObservableProperty]
    private string _selectedProviderId;

    [ObservableProperty]
    private OrchestrationStrategy _selectedStrategy = OrchestrationStrategy.Single;

    [ObservableProperty]
    private string _statusText = "就绪";

    /// <summary>视图加载时调用：拉取会话列表。</summary>
    public async Task InitializeAsync()
    {
        await ReloadSessionsAsync();
    }

    private async Task ReloadSessionsAsync()
    {
        var result = await _sessions.ListSessionsAsync();
        if (!result.IsSuccess || result.Value is null)
        {
            StatusText = $"会话列表加载失败：{result.Error}";
            return;
        }

        var selectedId = SelectedSession?.Id;
        Sessions.Clear();
        foreach (var s in result.Value)
        {
            Sessions.Add(new SessionItemViewModel
            {
                Id = s.Id,
                Title = s.Title,
                IsPinned = s.IsPinned,
                Strategy = s.Strategy,
                UpdatedAtUtc = s.UpdatedAtUtc,
            });
        }

        SelectedSession = Sessions.FirstOrDefault(s => s.Id == selectedId)
                          ?? Sessions.FirstOrDefault();
        if (SelectedSession is not null && Messages.Count == 0)
        {
            await LoadMessagesAsync(SelectedSession.Id);
        }
    }

    private async Task LoadMessagesAsync(string sessionId)
    {
        var result = await _sessions.GetMessagesAsync(sessionId);
        Messages.Clear();
        if (!result.IsSuccess || result.Value is null)
        {
            StatusText = $"消息加载失败：{result.Error}";
            return;
        }

        foreach (var m in result.Value)
        {
            Messages.Add(new MessageItemViewModel
            {
                Id = m.Id,
                Role = m.Role,
                Content = m.Content,
                Status = m.Status,
                ErrorText = m.Error,
                ProviderId = m.ProviderId,
                ModelId = m.ModelId,
                OrchestrationRole = m.OrchestrationRole,
            });
        }
    }

    [RelayCommand]
    private async Task NewSessionAsync()
    {
        var result = await _sessions.CreateSessionAsync(
            SelectedStrategy, SelectedProviderId, null);
        if (!result.IsSuccess)
        {
            StatusText = $"新建会话失败：{result.Error}";
            return;
        }

        await ReloadSessionsAsync();
        SelectedSession = Sessions.FirstOrDefault(s => s.Id == result.Value!.Id);
        Messages.Clear();
    }

    [RelayCommand]
    private async Task SelectSessionAsync(SessionItemViewModel? session)
    {
        if (session is null || session.Id == SelectedSession?.Id)
        {
            return;
        }

        SelectedSession = session;
        await LoadMessagesAsync(session.Id);
    }

    [RelayCommand]
    private async Task DeleteSessionAsync(SessionItemViewModel? session)
    {
        if (session is null)
        {
            return;
        }

        var result = await _sessions.DeleteSessionAsync(session.Id);
        if (!result.IsSuccess)
        {
            StatusText = $"删除失败：{result.Error}";
            return;
        }

        if (SelectedSession?.Id == session.Id)
        {
            SelectedSession = null;
            Messages.Clear();
        }

        await ReloadSessionsAsync();
    }

    [RelayCommand]
    private async Task TogglePinAsync(SessionItemViewModel? session)
    {
        if (session is null)
        {
            return;
        }

        await _sessions.TogglePinAsync(session.Id);
        await ReloadSessionsAsync();
    }

    [RelayCommand]
    private void Stop()
    {
        _streamCts?.Cancel();
        StatusText = "正在停止…";
    }

    [RelayCommand]
    private async Task SendAsync()
    {
        var text = InputText.Trim();
        if (text.Length == 0 || IsStreaming)
        {
            return;
        }

        // 无会话则先按当前 provider/策略建一个。
        if (SelectedSession is null)
        {
            var created = await _sessions.CreateSessionAsync(
                SelectedStrategy, SelectedProviderId, null);
            if (!created.IsSuccess)
            {
                StatusText = $"新建会话失败：{created.Error}";
                return;
            }

            await ReloadSessionsAsync();
            SelectedSession = Sessions.FirstOrDefault(s => s.Id == created.Value!.Id);
        }

        var sessionId = SelectedSession!.Id;
        InputText = string.Empty;
        IsStreaming = true;
        StatusText = "思考中…";
        _streamCts = new CancellationTokenSource();
        _currentAssistantMessageId = null;

        // 用户消息即时投影（门面负责持久化）。
        Messages.Add(new MessageItemViewModel
        {
            Id = Guid.NewGuid().ToString("N"),
            Role = ChatRole.User,
            Content = text,
        });

        try
        {
            await foreach (var ev in _facade.SendAsync(sessionId, text, _streamCts.Token))
            {
                await Dispatcher.UIThread.InvokeAsync(() => HandleEvent(ev));
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = "已停止";
        }
        catch (Exception ex)
        {
            StatusText = $"对话失败：{ex.Message}";
        }
        finally
        {
            IsStreaming = false;
            _streamCts.Dispose();
            _streamCts = null;
            await ReloadSessionsAsync(); // 标题可能因首条消息自动更新
        }
    }

    private void HandleEvent(ChatEvent ev)
    {
        switch (ev)
        {
            case AssistantMessageStarted started:
                _currentAssistantMessageId = started.MessageId;
                Messages.Add(new MessageItemViewModel
                {
                    Id = started.MessageId,
                    Role = ChatRole.Assistant,
                    ProviderId = started.ProviderId,
                    ModelId = started.ModelId,
                    OrchestrationRole = started.OrchestrationRole,
                    Status = MessageStatus.Streaming,
                });
                StatusText = $"生成中（{started.ModelId}）…";
                break;

            case TextDeltaEvent delta:
                if (FindCurrentAssistant() is { } target)
                {
                    target.Content += delta.Delta;
                }

                break;

            case ReasoningDeltaEvent reasoning:
                if (FindCurrentAssistant() is { } reasoningTarget)
                {
                    reasoningTarget.ReasoningContent =
                        (reasoningTarget.ReasoningContent ?? string.Empty) + reasoning.Delta;
                }

                break;

            case MessageCompletedEvent completed:
                if (FindCurrentAssistant() is { } done)
                {
                    done.Status = MessageStatus.Completed;
                }

                StatusText = $"完成 · {completed.TokensIn}→{completed.TokensOut} tokens · {completed.LatencyMs}ms";
                break;

            case MessageFailedEvent failed:
                if (FindCurrentAssistant() is { } failedVm)
                {
                    failedVm.Status = MessageStatus.Failed;
                    failedVm.ErrorText = failed.Error;
                }

                StatusText = $"失败：{failed.Error}";
                break;

            case MessageCancelledEvent:
                if (FindCurrentAssistant() is { } cancelledVm)
                {
                    cancelledVm.Status = MessageStatus.Cancelled;
                }

                StatusText = "已取消";
                break;

            case TurnCompletedEvent turn:
                StatusText = $"本轮结束 · {turn.TotalMessages} 条回复 · 成本 ${turn.TotalCostUsd:F4}";
                break;
        }
    }

    private MessageItemViewModel? FindCurrentAssistant()
        => _currentAssistantMessageId is null
            ? null
            : Messages.LastOrDefault(m => m.Id == _currentAssistantMessageId);
}
