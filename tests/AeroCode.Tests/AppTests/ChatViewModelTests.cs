using System;
using System.Collections.Generic;
using System.Threading;
using AeroAgent.Conversation.Models;
using AeroAgent.Conversation.Orchestration;
using AeroAgent.Conversation.Services;
using AeroAgent.Moa.Strategies;
using AeroCode.App.ViewModels;
using AeroCode.Tests.ConversationTests;
using Xunit;

namespace AeroCode.Tests.AppTests;

/// <summary>
/// ChatViewModel 事件路由回归测试。
/// P1-2 背景：HandleEvent 曾不校验事件 SessionId——流式进行中用户切走会话时，
/// 旧轮次事件会写进新会话的气泡列表（跨会话串流）。
/// 直接调用 internal HandleEvent 验证守卫，不依赖 Avalonia Dispatcher。
/// </summary>
public sealed class ChatViewModelEventRoutingTests
{
    private static ChatViewModel MakeViewModel() =>
        new(new NullSessionService(), new UnusedFacade(), new TestProviderRegistry(), new MoaOptions());

    [Fact]
    public void Event_FromOtherSession_IsDiscarded()
    {
        var vm = MakeViewModel();
        vm.SelectedSession = new SessionItemViewModel { Id = "session-B" };

        // 来自 session-A（旧轮次）的事件：一律丢弃，不新增气泡、不改状态文本。
        vm.HandleEvent(new AssistantMessageStarted
        {
            SessionId = "session-A",
            MessageId = "msg-1",
            ProviderId = "p",
            ModelId = "m",
            OrchestrationRole = StrategyRole.None,
        });
        vm.HandleEvent(new TextDeltaEvent
        {
            SessionId = "session-A",
            MessageId = "msg-1",
            Delta = "不该出现的内容",
        });
        vm.HandleEvent(new TurnCompletedEvent
        {
            SessionId = "session-A",
            MessageId = string.Empty,
            Strategy = OrchestrationStrategy.Single,
            TotalMessages = 1,
            TotalCostUsd = 0,
        });

        // 跨会话回归点：任何旧轮次事件都不得生成气泡（含轮级错误气泡）。
        // StatusText 不作断言——选中会话本身会异步加载消息并更新状态文本，属正常行为。
        Assert.Empty(vm.Messages);
    }

    [Fact]
    public void Event_FromSelectedSession_IsProjected()
    {
        var vm = MakeViewModel();
        vm.SelectedSession = new SessionItemViewModel { Id = "session-B" };

        vm.HandleEvent(new AssistantMessageStarted
        {
            SessionId = "session-B",
            MessageId = "msg-1",
            ProviderId = "p",
            ModelId = "m",
            OrchestrationRole = StrategyRole.None,
        });
        vm.HandleEvent(new TextDeltaEvent
        {
            SessionId = "session-B",
            MessageId = "msg-1",
            Delta = "正常",
        });
        vm.HandleEvent(new TextDeltaEvent
        {
            SessionId = "session-B",
            MessageId = "msg-1",
            Delta = "内容",
        });

        var message = Assert.Single(vm.Messages);
        Assert.Equal("msg-1", message.Id);
        Assert.Equal("正常内容", message.Content);
        Assert.Equal(MessageStatus.Streaming, message.Status);
    }

    [Fact]
    public void Event_WithoutSelectedSession_IsDiscarded()
    {
        var vm = MakeViewModel();
        Assert.Null(vm.SelectedSession);

        vm.HandleEvent(new AssistantMessageStarted
        {
            SessionId = "session-A",
            MessageId = "msg-1",
            ProviderId = "p",
            ModelId = "m",
            OrchestrationRole = StrategyRole.None,
        });

        Assert.Empty(vm.Messages);
    }
}

/// <summary>事件路由测试不需要会话服务——所有成员如实报不可用。</summary>
internal sealed class NullSessionService : AeroAgent.Conversation.Services.ISessionService
{
    private static AeroCode.Core.Common.Result<T> Fail<T>() =>
        AeroCode.Core.Common.Result<T>.Fail("事件路由测试不使用会话服务");

    public Task<AeroCode.Core.Common.Result<ChatSession>> CreateSessionAsync(
        OrchestrationStrategy strategy = OrchestrationStrategy.Single,
        string? preferredProviderId = null,
        string? preferredModel = null,
        string? title = null)
        => Task.FromResult(Fail<ChatSession>());

    public Task<AeroCode.Core.Common.Result<IReadOnlyList<ChatSessionSummary>>> ListSessionsAsync(
        bool includeDeleted = false)
        => Task.FromResult(Fail<IReadOnlyList<ChatSessionSummary>>());

    public Task<AeroCode.Core.Common.Result<ChatSession>> GetSessionAsync(string id)
        => Task.FromResult(Fail<ChatSession>());

    public Task<AeroCode.Core.Common.Result<ChatSession>> RenameSessionAsync(string id, string title)
        => Task.FromResult(Fail<ChatSession>());

    public Task<AeroCode.Core.Common.Result<ChatSession>> SetStrategyAsync(
        string id,
        OrchestrationStrategy strategy,
        string? preferredProviderId,
        string? preferredModel)
        => Task.FromResult(Fail<ChatSession>());

    public Task<AeroCode.Core.Common.Result<ChatSession>> TogglePinAsync(string id)
        => Task.FromResult(Fail<ChatSession>());

    public Task<AeroCode.Core.Common.Result<bool>> DeleteSessionAsync(string id)
        => Task.FromResult(Fail<bool>());

    public Task<AeroCode.Core.Common.Result<bool>> RestoreSessionAsync(string id)
        => Task.FromResult(Fail<bool>());

    public Task<AeroCode.Core.Common.Result<IReadOnlyList<ChatMessage>>> GetMessagesAsync(string sessionId)
        => Task.FromResult(Fail<IReadOnlyList<ChatMessage>>());

    public Task<AeroCode.Core.Common.Result<ChatMessage>> AppendMessageAsync(ChatMessage message)
        => Task.FromResult(Fail<ChatMessage>());

    public Task<AeroCode.Core.Common.Result<ChatMessage>> UpdateMessageAsync(ChatMessage message)
        => Task.FromResult(Fail<ChatMessage>());
}

/// <summary>事件投影测试不需要编排门面——如实报不可用。</summary>
internal sealed class UnusedFacade : IChatOrchestrationFacade
{
    public IAsyncEnumerable<ChatEvent> SendAsync(
        string sessionId, string userText, CancellationToken ct = default)
        => throw new NotSupportedException("事件投影测试不走门面");
}

/// <summary>
/// S4 工具事件投影回归：ToolCallStarted/Completed 的行投影、徽标与拒绝态，
/// 以及跨会话守卫对工具事件同样生效（工具行不得串进别的会话）。
/// </summary>
public sealed class ChatViewModelToolProjectionTests
{
    private static ChatViewModel MakeSelectedViewModel(string sessionId = "session-B")
    {
        var vm = new ChatViewModel(
            new NullSessionService(), new UnusedFacade(), new TestProviderRegistry(), new MoaOptions());
        vm.SelectedSession = new SessionItemViewModel { Id = sessionId };
        return vm;
    }

    [Fact]
    public void ToolCallStarted_AddsStreamingToolRow_WithArgumentsAndParent()
    {
        var vm = MakeSelectedViewModel();

        vm.HandleEvent(new ToolCallStartedEvent
        {
            SessionId = "session-B",
            MessageId = "tool-msg-1",
            ToolCallId = "call-1",
            ToolName = "notes_add",
            ArgumentsJson = "{\"title\":\"hello\"}",
            ParentMessageId = "turn-1",
        });

        var row = Assert.Single(vm.Messages);
        Assert.Equal(ChatRole.Tool, row.Role);
        Assert.True(row.IsTool);
        Assert.Equal(MessageStatus.Streaming, row.Status);
        Assert.Equal("notes_add", row.ToolName);
        Assert.Equal("call-1", row.ToolCallId);
        Assert.Equal("{\"title\":\"hello\"}", row.ToolArguments);
        Assert.Equal("turn-1", row.ParentMessageId);
        Assert.Equal("工具 · notes_add", row.ToolBadge);
        Assert.Contains("notes_add", vm.StatusText);
    }

    [Fact]
    public void ToolCallCompleted_Success_MarksCompletedWithPreview()
    {
        var vm = MakeSelectedViewModel();
        vm.HandleEvent(new ToolCallStartedEvent
        {
            SessionId = "session-B",
            MessageId = "tool-msg-1",
            ToolCallId = "call-1",
            ToolName = "notes_add",
        });

        vm.HandleEvent(new ToolCallCompletedEvent
        {
            SessionId = "session-B",
            MessageId = "tool-msg-1",
            ToolCallId = "call-1",
            ToolName = "notes_add",
            Success = true,
            OutputPreview = "saved: hello",
            LatencyMs = 42,
        });

        var row = Assert.Single(vm.Messages);
        Assert.Equal(MessageStatus.Completed, row.Status);
        Assert.False(row.ToolDenied);
        Assert.Equal("saved: hello", row.Content);
        Assert.Null(row.StatusGlyph);
        Assert.Contains("42ms", vm.StatusText);
    }

    [Fact]
    public void ToolCallCompleted_Denied_DegradedRowShowsRejectedGlyph()
    {
        var vm = MakeSelectedViewModel();
        vm.HandleEvent(new ToolCallStartedEvent
        {
            SessionId = "session-B",
            MessageId = "tool-msg-1",
            ToolCallId = "call-1",
            ToolName = "run_shell",
        });

        vm.HandleEvent(new ToolCallCompletedEvent
        {
            SessionId = "session-B",
            MessageId = "tool-msg-1",
            ToolCallId = "call-1",
            ToolName = "run_shell",
            Success = false,
            Denied = true,
            OutputPreview = "Permission denied: user declined",
        });

        var row = Assert.Single(vm.Messages);
        Assert.Equal(MessageStatus.Degraded, row.Status);
        Assert.True(row.ToolDenied);
        Assert.Equal("已拒绝", row.StatusGlyph);
        Assert.Contains("拒绝", vm.StatusText);
    }

    [Fact]
    public void ToolCallCompleted_Failure_DegradedWithoutDeniedGlyph()
    {
        var vm = MakeSelectedViewModel();
        vm.HandleEvent(new ToolCallStartedEvent
        {
            SessionId = "session-B",
            MessageId = "tool-msg-1",
            ToolCallId = "call-1",
            ToolName = "notes_add",
        });

        vm.HandleEvent(new ToolCallCompletedEvent
        {
            SessionId = "session-B",
            MessageId = "tool-msg-1",
            ToolCallId = "call-1",
            ToolName = "notes_add",
            Success = false,
            OutputPreview = "boom",
        });

        var row = Assert.Single(vm.Messages);
        Assert.Equal(MessageStatus.Degraded, row.Status);
        Assert.False(row.ToolDenied);
        Assert.Equal("降级", row.StatusGlyph);
        Assert.Contains("失败", vm.StatusText);
    }

    [Fact]
    public void AssistantMessageStarted_WithToolCalls_SetsToolCallsBadge()
    {
        var vm = MakeSelectedViewModel();

        vm.HandleEvent(new AssistantMessageStarted
        {
            SessionId = "session-B",
            MessageId = "turn-1",
            ProviderId = "p",
            ModelId = "m",
            OrchestrationRole = StrategyRole.None,
            HasToolCalls = true,
        });

        var row = Assert.Single(vm.Messages);
        Assert.True(row.HasToolCalls);
        Assert.Equal("工具调用", row.ToolCallsBadge);
    }

    [Fact]
    public void ToolEvents_FromOtherSession_AreDiscarded()
    {
        var vm = MakeSelectedViewModel(); // 选中 session-B

        vm.HandleEvent(new ToolCallStartedEvent
        {
            SessionId = "session-A",
            MessageId = "tool-msg-1",
            ToolCallId = "call-1",
            ToolName = "notes_add",
        });
        vm.HandleEvent(new ToolCallCompletedEvent
        {
            SessionId = "session-A",
            MessageId = "tool-msg-1",
            ToolCallId = "call-1",
            ToolName = "notes_add",
            Success = true,
        });

        Assert.Empty(vm.Messages);
    }

    [Fact]
    public void ToolCallCompleted_WithoutMatchingRow_OnlyUpdatesStatusText()
    {
        var vm = MakeSelectedViewModel();

        // 补达事件（对应行不存在，例如切会话后被守卫丢弃）：不得崩溃，也不凭空造行。
        vm.HandleEvent(new ToolCallCompletedEvent
        {
            SessionId = "session-B",
            MessageId = "ghost",
            ToolCallId = "call-x",
            ToolName = "notes_add",
            Success = true,
            LatencyMs = 5,
        });

        Assert.Empty(vm.Messages);
        Assert.Contains("notes_add", vm.StatusText);
    }
}

/// <summary>
/// S8 热重载链回归：设置保存 → ProviderFactory.Reload → ProvidersChanged →
/// ChatViewModel 的 provider 下拉就地刷新（仍存在的选中项保留；被删则回退第一个/空）。
/// </summary>
public sealed class ChatViewModelProviderReloadTests
{
    private static ChatViewModel MakeViewModel(TestProviderRegistry registry) =>
        new(new NullSessionService(), new UnusedFacade(), registry, new MoaOptions());

    [Fact]
    public void ProvidersChanged_NewProviderAdded_ListRefreshed_SelectionKept()
    {
        var registry = new TestProviderRegistry();
        registry.Add(new ScriptedProvider { ProviderId = "alpha" });
        var vm = MakeViewModel(registry);
        vm.SelectedProviderId = "alpha";

        registry.Add(new ScriptedProvider { ProviderId = "beta" });
        registry.RaiseProvidersChanged();

        Assert.Equal(2, vm.ProviderIds.Count);
        Assert.Contains("alpha", vm.ProviderIds);
        Assert.Contains("beta", vm.ProviderIds);
        Assert.Equal("alpha", vm.SelectedProviderId); // 仍存在 → 选中不丢
    }

    [Fact]
    public void ProvidersChanged_SelectedProviderRemoved_FallsBackToFirst()
    {
        var registry = new TestProviderRegistry();
        registry.Add(new ScriptedProvider { ProviderId = "alpha" });
        registry.Add(new ScriptedProvider { ProviderId = "beta" });
        var vm = MakeViewModel(registry);
        vm.SelectedProviderId = "alpha";

        registry.Remove("alpha");
        registry.RaiseProvidersChanged();

        var remaining = Assert.Single(vm.ProviderIds);
        Assert.Equal("beta", remaining);
        Assert.Equal("beta", vm.SelectedProviderId);
    }

    [Fact]
    public void ProvidersChanged_AllProvidersRemoved_SelectionBecomesEmpty()
    {
        var registry = new TestProviderRegistry();
        registry.Add(new ScriptedProvider { ProviderId = "alpha" });
        var vm = MakeViewModel(registry);
        Assert.Equal("alpha", vm.SelectedProviderId);

        registry.Remove("alpha");
        registry.RaiseProvidersChanged();

        Assert.Empty(vm.ProviderIds);
        Assert.Equal(string.Empty, vm.SelectedProviderId);
    }
}

/// <summary>
/// 记录型会话服务：只关心 CreateSessionAsync 收到的策略参数（新会话默认策略验收），
/// 其余成员如实返回空/成功最小值。
/// </summary>
internal sealed class RecordingSessionService : AeroAgent.Conversation.Services.ISessionService
{
    private static AeroCode.Core.Common.Result<T> Ok<T>(T v) => AeroCode.Core.Common.Result<T>.Ok(v);

    /// <summary>最近一次 CreateSessionAsync 收到的策略。</summary>
    public OrchestrationStrategy? LastCreatedStrategy { get; private set; }

    public Task<AeroCode.Core.Common.Result<ChatSession>> CreateSessionAsync(
        OrchestrationStrategy strategy = OrchestrationStrategy.Single,
        string? preferredProviderId = null,
        string? preferredModel = null,
        string? title = null)
    {
        LastCreatedStrategy = strategy;
        return Task.FromResult(Ok(new ChatSession { Strategy = strategy }));
    }

    public Task<AeroCode.Core.Common.Result<IReadOnlyList<ChatSessionSummary>>> ListSessionsAsync(
        bool includeDeleted = false)
        => Task.FromResult(Ok<IReadOnlyList<ChatSessionSummary>>(Array.Empty<ChatSessionSummary>()));

    public Task<AeroCode.Core.Common.Result<ChatSession>> GetSessionAsync(string id)
        => Task.FromResult(Ok(new ChatSession { Id = id }));

    public Task<AeroCode.Core.Common.Result<ChatSession>> RenameSessionAsync(string id, string title)
        => Task.FromResult(Ok(new ChatSession { Id = id, Title = title }));

    public Task<AeroCode.Core.Common.Result<ChatSession>> SetStrategyAsync(
        string id,
        OrchestrationStrategy strategy,
        string? preferredProviderId,
        string? preferredModel)
        => Task.FromResult(Ok(new ChatSession { Id = id, Strategy = strategy }));

    public Task<AeroCode.Core.Common.Result<ChatSession>> TogglePinAsync(string id)
        => Task.FromResult(Ok(new ChatSession { Id = id }));

    public Task<AeroCode.Core.Common.Result<bool>> DeleteSessionAsync(string id)
        => Task.FromResult(Ok(true));

    public Task<AeroCode.Core.Common.Result<bool>> RestoreSessionAsync(string id)
        => Task.FromResult(Ok(true));

    public Task<AeroCode.Core.Common.Result<IReadOnlyList<ChatMessage>>> GetMessagesAsync(string sessionId)
        => Task.FromResult(Ok<IReadOnlyList<ChatMessage>>(Array.Empty<ChatMessage>()));

    public Task<AeroCode.Core.Common.Result<ChatMessage>> AppendMessageAsync(ChatMessage message)
        => Task.FromResult(Ok(message));

    public Task<AeroCode.Core.Common.Result<ChatMessage>> UpdateMessageAsync(ChatMessage message)
        => Task.FromResult(Ok(message));
}

/// <summary>
/// S9 新会话默认策略回归：MoaOptions.DefaultStrategy 是新建会话的策略来源；
/// 设置页保存 → RaiseOptionsChanged → 无选中会话时工具条下拉同步刷新；
/// 有选中会话时下拉代表会话自身策略，不得被设置页改写。
/// </summary>
public sealed class ChatViewModelDefaultStrategyTests
{
    private static (ChatViewModel vm, RecordingSessionService sessions, MoaOptions options) MakeViewModel(
        OrchestrationStrategy defaultStrategy)
    {
        var sessions = new RecordingSessionService();
        var options = new MoaOptions { DefaultStrategy = defaultStrategy };
        var vm = new ChatViewModel(sessions, new UnusedFacade(), new TestProviderRegistry(), options);
        return (vm, sessions, options);
    }

    [Fact]
    public void Ctor_SelectedStrategy_SeedsFromMoaOptionsDefault()
    {
        var (vm, _, _) = MakeViewModel(OrchestrationStrategy.Ensemble);
        Assert.Equal(OrchestrationStrategy.Ensemble, vm.SelectedStrategy);
    }

    [Fact]
    public async Task NewSession_UsesMoaDefaultStrategy()
    {
        // 计划验收项：新会话默认策略生效——创建请求携带 DefaultStrategy 而非恒定 Single
        var (vm, sessions, _) = MakeViewModel(OrchestrationStrategy.Router);

        await vm.NewSessionCommand.ExecuteAsync(null);

        Assert.Equal(OrchestrationStrategy.Router, sessions.LastCreatedStrategy);
    }

    [Fact]
    public void OptionsChanged_NoSessionSelected_DropdownFollowsNewDefault()
    {
        var (vm, _, options) = MakeViewModel(OrchestrationStrategy.Single);
        Assert.Null(vm.SelectedSession);

        options.DefaultStrategy = OrchestrationStrategy.Pipeline;
        options.RaiseOptionsChanged();

        Assert.Equal(OrchestrationStrategy.Pipeline, vm.SelectedStrategy);
    }

    [Fact]
    public void OptionsChanged_SessionSelected_SessionStrategyUntouched()
    {
        var (vm, _, options) = MakeViewModel(OrchestrationStrategy.Single);
        vm.SelectedSession = new SessionItemViewModel
        {
            Id = "session-A",
            Strategy = OrchestrationStrategy.Decompose,
        };
        Assert.Equal(OrchestrationStrategy.Decompose, vm.SelectedStrategy);

        options.DefaultStrategy = OrchestrationStrategy.Ensemble;
        options.RaiseOptionsChanged();

        // 下拉与该会话同步，代表会话自身选择：设置页不得改写
        Assert.Equal(OrchestrationStrategy.Decompose, vm.SelectedStrategy);
    }

    [Fact]
    public void Ctor_NullMoaOptions_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new ChatViewModel(
            new NullSessionService(), new UnusedFacade(), new TestProviderRegistry(), null!));
    }
}
