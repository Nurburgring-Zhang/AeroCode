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
using Avalonia;
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

    [ObservableProperty]
    private OrchestrationStrategy _strategy;

    /// <summary>会话级 provider 偏好（空 = 全局默认）。</summary>
    public string? PreferredProviderId { get; init; }

    /// <summary>会话级模型偏好（空 = provider 默认）。</summary>
    public string? PreferredModel { get; init; }

    [ObservableProperty]
    private DateTime _updatedAtUtc;

    public string DisplayTime => UpdatedAtUtc.ToLocalTime().ToString("MM-dd HH:mm");

    partial void OnUpdatedAtUtcChanged(DateTime value) => OnPropertyChanged(nameof(DisplayTime));
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

    [ObservableProperty]
    private int _tokensIn;

    [ObservableProperty]
    private int _tokensOut;

    [ObservableProperty]
    private double _costUsd;

    public string? ProviderId { get; init; }
    public string? ModelId { get; init; }
    public StrategyRole OrchestrationRole { get; init; }

    /// <summary>父消息 Id（MOA 归属树），顶层为 null。</summary>
    public string? ParentMessageId { get; init; }

    /// <summary>编排子任务标签（如"候选 A"/planner 分配的子任务名）。</summary>
    public string? Label { get; init; }

    /// <summary>归属树深度的左缩进（父级在上方时按父深度 +1 计算）。
    /// 流式期间父消息可能晚于子消息到达、缩进需要回填，必须可通知。</summary>
    [ObservableProperty]
    private Thickness _indentMargin;

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

    /// <summary>成本徽章（未计价不显示——不猜）。</summary>
    public string? CostBadge => CostUsd > 0 ? $"${CostUsd:F4}" : null;

    /// <summary>用量徽章（真实 usage 未报不显示）。</summary>
    public string? UsageBadge => TokensIn > 0 || TokensOut > 0 ? $"{TokensIn}→{TokensOut} tok" : null;

    /// <summary>非完成态的状态角标（降级/失败/取消/流式中）。</summary>
    public string? StatusGlyph => Status switch
    {
        MessageStatus.Degraded => "降级",
        MessageStatus.Failed => "失败",
        MessageStatus.Cancelled => "已停止",
        MessageStatus.Streaming => "…",
        _ => null,
    };

    private static string RoleName(StrategyRole role) => role switch
    {
        StrategyRole.Router => "路由",
        StrategyRole.Planner => "规划",
        StrategyRole.Worker => "执行",
        StrategyRole.Judge => "评审",
        StrategyRole.Synthesizer => "汇总",
        _ => string.Empty,
    };

    partial void OnCostUsdChanged(double value) => OnPropertyChanged(nameof(CostBadge));
    partial void OnTokensInChanged(int value) => OnPropertyChanged(nameof(UsageBadge));
    partial void OnTokensOutChanged(int value) => OnPropertyChanged(nameof(UsageBadge));
    partial void OnStatusChanged(MessageStatus value) => OnPropertyChanged(nameof(StatusGlyph));
}

/// <summary>
/// 统一对话视图模型：会话 CRUD + 流式对话 + 事件流消费。
/// 持久化全部由 <see cref="IChatOrchestrationFacade"/> 负责，
/// 本 VM 只做 UI 投影（Dispatcher 保证 UI 线程安全）。
/// 事件路由按 MessageId 精确定位气泡——MOA 并行编排下多条消息
/// 交错产出，任何"当前消息"单指针都会串线。
/// </summary>
public partial class ChatViewModel : ObservableObject
{
    private readonly ISessionService _sessions;
    private readonly IChatOrchestrationFacade _facade;
    private readonly IProviderRegistry _providers;

    private CancellationTokenSource? _streamCts;
    private bool _suppressStrategySync;
    private bool _suppressSessionLoad;

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

        // 就地更新：同 Id 的会话项保留原实例、只刷可变字段。
        // 若整体替换实例，SelectedSession 每轮都会变成新对象 →
        // OnSelectedSessionChanged 触发消息全量重载：UI 闪烁，且
        // ReasoningContent 等实时态（未持久化）会被重载冲掉。
        var selected = SelectedSession; // Clear() 会经列表双向绑定把选中项写回 null，先捕获
        var existingById = Sessions.ToDictionary(s => s.Id, StringComparer.Ordinal);
        var items = new List<SessionItemViewModel>(result.Value.Count);
        foreach (var s in result.Value)
        {
            SessionItemViewModel item;
            if (existingById.TryGetValue(s.Id, out var existing))
            {
                existing.Title = s.Title;
                existing.IsPinned = s.IsPinned;
                existing.Strategy = s.Strategy;
                existing.UpdatedAtUtc = s.UpdatedAtUtc;
                item = existing;
            }
            else
            {
                item = new SessionItemViewModel
                {
                    Id = s.Id,
                    Title = s.Title,
                    IsPinned = s.IsPinned,
                    Strategy = s.Strategy,
                    PreferredProviderId = s.PreferredProviderId,
                    PreferredModel = s.PreferredModel,
                    UpdatedAtUtc = s.UpdatedAtUtc,
                };
            }

            items.Add(item);
        }

        Sessions.Clear();
        foreach (var item in items)
        {
            Sessions.Add(item);
        }

        if (selected is not null && items.Contains(selected))
        {
            // 选中会话仍在列：恢复被绑定清空前的选中实例。
            // 同一实例无需再同步策略/重载消息——压制 OnSelectedSessionChanged。
            if (!ReferenceEquals(SelectedSession, selected))
            {
                _suppressSessionLoad = true;
                try
                {
                    SelectedSession = selected;
                }
                finally
                {
                    _suppressSessionLoad = false;
                }
            }
        }
        else
        {
            // 尚未选中或选中的会话已被删除 → 重选，正常触发加载。
            SelectedSession = items.FirstOrDefault();
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
                ParentMessageId = m.ParentMessageId,
                Label = m.Label,
                TokensIn = m.TokensIn,
                TokensOut = m.TokensOut,
                CostUsd = m.CostUsd,
            });
        }

        RecomputeIndents();
    }

    /// <summary>按父消息链计算缩进：有父级的编排消息向右缩进，形成归属树视觉。</summary>
    private void RecomputeIndents()
    {
        var depthById = new Dictionary<string, int>(Messages.Count);
        foreach (var m in Messages)
        {
            var depth = 0;
            if (m.ParentMessageId is not null && depthById.TryGetValue(m.ParentMessageId, out var parentDepth))
            {
                depth = Math.Min(parentDepth + 1, 4);
            }

            depthById[m.Id] = depth;
            m.IndentMargin = new Thickness(depth * 28, 0, 0, 0);
        }
    }

    /// <summary>切换会话时同步策略/provider 选择并加载消息流。</summary>
    partial void OnSelectedSessionChanged(SessionItemViewModel? value)
    {
        if (value is null || _suppressSessionLoad)
        {
            return;
        }

        _suppressStrategySync = true;
        try
        {
            SelectedStrategy = value.Strategy;
            if (!string.IsNullOrEmpty(value.PreferredProviderId)
                && ProviderIds.Contains(value.PreferredProviderId))
            {
                SelectedProviderId = value.PreferredProviderId;
            }
        }
        finally
        {
            _suppressStrategySync = false;
        }
        _ = LoadMessagesAsync(value.Id);
    }

    /// <summary>策略下拉变更：持久化到当前会话（新会话则作为创建参数）。</summary>
    partial void OnSelectedStrategyChanged(OrchestrationStrategy value)
    {
        if (_suppressStrategySync || IsStreaming)
        {
            return;
        }

        var session = SelectedSession;
        if (session is null || session.Strategy == value)
        {
            return;
        }

        session.Strategy = value;
        _ = PersistStrategyAsync(session, value);
    }

    private async Task PersistStrategyAsync(SessionItemViewModel session, OrchestrationStrategy strategy)
    {
        var result = await _sessions.SetStrategyAsync(
            session.Id, strategy, session.PreferredProviderId, session.PreferredModel);
        StatusText = result.IsSuccess
            ? $"本会话策略已切换为 {strategy}"
            : $"策略切换失败：{result.Error}";
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

    internal void HandleEvent(ChatEvent ev)
    {
        // 跨会话守卫：流式进行中用户切走会话时，旧轮次的事件不得写入新会话的
        // 气泡列表（否则消息串流）。DB 已由门面/runner 落库，此处丢弃只影响投影。
        if (!string.Equals(ev.SessionId, SelectedSession?.Id, StringComparison.Ordinal))
        {
            return;
        }

        switch (ev)
        {
            case AssistantMessageStarted started:
                Messages.Add(new MessageItemViewModel
                {
                    Id = started.MessageId,
                    Role = ChatRole.Assistant,
                    ProviderId = started.ProviderId,
                    ModelId = started.ModelId,
                    OrchestrationRole = started.OrchestrationRole,
                    ParentMessageId = started.ParentMessageId,
                    Label = started.Label,
                    Status = MessageStatus.Streaming,
                });
                RecomputeIndents();
                StatusText = $"生成中（{started.ModelId}）…";
                break;

            case TextDeltaEvent delta:
                if (FindMessage(delta.MessageId) is { } target)
                {
                    target.Content += delta.Delta;
                }

                break;

            case ReasoningDeltaEvent reasoning:
                if (FindMessage(reasoning.MessageId) is { } reasoningTarget)
                {
                    reasoningTarget.ReasoningContent =
                        (reasoningTarget.ReasoningContent ?? string.Empty) + reasoning.Delta;
                }

                break;

            case MessageCompletedEvent completed:
                if (FindMessage(completed.MessageId) is { } done)
                {
                    done.Status = MessageStatus.Completed;
                    done.TokensIn = completed.TokensIn;
                    done.TokensOut = completed.TokensOut;
                    done.CostUsd = completed.CostUsd;
                }

                StatusText = $"完成 · {completed.TokensIn}→{completed.TokensOut} tokens · {completed.LatencyMs}ms";
                break;

            case MessageFailedEvent failed:
                if (!string.IsNullOrEmpty(failed.MessageId)
                    && FindMessage(failed.MessageId) is { } failedVm)
                {
                    failedVm.Status = MessageStatus.Failed;
                    failedVm.ErrorText = failed.Error;
                }
                else
                {
                    // 轮级失败（无对应消息）：如实投一条错误气泡，不静默。
                    Messages.Add(new MessageItemViewModel
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        Role = ChatRole.Assistant,
                        Status = MessageStatus.Failed,
                        ErrorText = failed.Error,
                    });
                }

                StatusText = $"失败：{failed.Error}";
                break;

            case MessageCancelledEvent cancelled:
                if (!string.IsNullOrEmpty(cancelled.MessageId)
                    && FindMessage(cancelled.MessageId) is { } cancelledVm)
                {
                    cancelledVm.Status = MessageStatus.Cancelled;
                }

                StatusText = "已取消";
                break;

            case TurnCompletedEvent turn:
                // 成本只在真实计价时展示：未配置单价的模型不显示 $0.0000（那会伪装成已核算）。
                var costPart = turn.TotalCostUsd > 0 ? $" · 成本 ${turn.TotalCostUsd:F4}" : string.Empty;
                StatusText = $"本轮结束 · {turn.TotalMessages} 条回复{costPart}";
                break;
        }
    }

    private MessageItemViewModel? FindMessage(string messageId)
        => string.IsNullOrEmpty(messageId)
            ? null
            : Messages.LastOrDefault(m => m.Id == messageId);
}
