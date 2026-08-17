using System;
using System.Collections.Generic;
using System.Threading;
using AeroAgent.Conversation.Models;
using AeroAgent.Conversation.Orchestration;
using AeroAgent.Conversation.Services;
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
    private sealed class UnusedFacade : IChatOrchestrationFacade
    {
        public IAsyncEnumerable<ChatEvent> SendAsync(
            string sessionId, string userText, CancellationToken ct = default)
            => throw new NotSupportedException("事件路由测试不走门面");
    }

    private static ChatViewModel MakeViewModel() =>
        new(new NullSessionService(), new UnusedFacade(), new TestProviderRegistry());

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
