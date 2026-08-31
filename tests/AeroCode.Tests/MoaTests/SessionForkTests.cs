using System;
using System.Linq;
using System.Threading.Tasks;
using AeroAgent.Conversation.Models;
using AeroAgent.Conversation.Services;
using Xunit;

namespace AeroCode.Tests.MoaTests;

/// <summary>
/// 会话 fork（真实 SQLite）：分叉点消息集正确、父链重映射、归属/用量字段如实复制、
/// 源会话零改动、未知会话/未知消息诚实失败。
/// </summary>
public sealed class SessionForkTests : MoaTestBase
{
    private async Task<ChatSession> SeedAsync(int messageCount, Action<ChatMessage>? decorate = null)
    {
        var session = await NewSessionAsync(OrchestrationStrategy.Single);
        for (var i = 0; i < messageCount; i++)
        {
            var appended = await Sessions.AppendMessageAsync(new ChatMessage
            {
                SessionId = session.Id,
                Role = i % 2 == 0 ? ChatRole.User : ChatRole.Assistant,
                Content = $"消息 {i + 1}",
                ProviderId = i % 2 == 0 ? null : "sa",
                ModelId = i % 2 == 0 ? null : string.Empty,
                OrchestrationRole = i % 2 == 0 ? StrategyRole.None : StrategyRole.Worker,
                TokensIn = i * 10,
                TokensOut = i * 5,
                CostUsd = i * 0.001,
                Status = MessageStatus.Completed,
            });
            Assert.True(appended.IsSuccess);
            decorate?.Invoke(appended.Value!);
        }

        return session;
    }

    [Fact]
    public async Task Fork_FullCopy_MessageSetCorrect_OriginalUntouched()
    {
        var source = await SeedAsync(4);
        var sourceBefore = await Sessions.GetMessagesAsync(source.Id);
        var sourceIdsBefore = sourceBefore.Value!.Select(m => m.Id).ToList();

        var forked = await ((ISessionFork)Sessions).ForkAsync(source.Id);
        Assert.True(forked.IsSuccess);
        var fork = forked.Value!;

        Assert.NotEqual(source.Id, fork.Id);
        Assert.EndsWith("（fork）", fork.Title);
        Assert.Equal(source.Strategy, fork.Strategy);
        Assert.False(fork.IsPinned);

        // 新会话消息集：数量、顺序、内容一致；Id 全部重新生成。
        var forkMessages = await Sessions.GetMessagesAsync(fork.Id);
        Assert.Equal(4, forkMessages.Value!.Count);
        Assert.Equal(new[] { "消息 1", "消息 2", "消息 3", "消息 4" },
            forkMessages.Value!.Select(m => m.Content).ToList());
        var forkIds = forkMessages.Value!.Select(m => m.Id).ToList();
        Assert.All(forkIds, id => Assert.DoesNotContain(id, sourceIdsBefore));

        // 源会话零改动：消息 Id 集与内容不变。
        var sourceAfter = await Sessions.GetMessagesAsync(source.Id);
        Assert.Equal(4, sourceAfter.Value!.Count);
        Assert.Equal(sourceIdsBefore, sourceAfter.Value!.Select(m => m.Id).ToList());

        // 源会话列表里仍是 2 个独立会话。
        var listed = await Sessions.ListSessionsAsync();
        Assert.Equal(2, listed.Value!.Count);
    }

    [Fact]
    public async Task Fork_AtMessage_CopiesPrefixOnly()
    {
        var source = await SeedAsync(4);
        var messages = await Sessions.GetMessagesAsync(source.Id);
        var upto = messages.Value![1].Id; // 复制到第 2 条（含）

        var forked = await ((ISessionFork)Sessions).ForkAsync(source.Id, upto);
        Assert.True(forked.IsSuccess);

        var forkMessages = await Sessions.GetMessagesAsync(forked.Value!.Id);
        Assert.Equal(2, forkMessages.Value!.Count);
        Assert.Equal(new[] { "消息 1", "消息 2" }, forkMessages.Value!.Select(m => m.Content).ToList());

        // 源会话仍是 4 条。
        Assert.Equal(4, (await Sessions.GetMessagesAsync(source.Id)).Value!.Count);
    }

    [Fact]
    public async Task Fork_ParentChain_IsRemappedNotDuplicated()
    {
        // 构造父链：第 1 条 user → 第 2 条 assistant（Parent=第1条）→ 第 3 条 tool 结果（Parent=第2条）。
        var source = await NewSessionAsync(OrchestrationStrategy.Single);
        var u = await Sessions.AppendMessageAsync(new ChatMessage { SessionId = source.Id, Role = ChatRole.User, Content = "问题" });
        var a = await Sessions.AppendMessageAsync(new ChatMessage
        {
            SessionId = source.Id, Role = ChatRole.Assistant, Content = "答",
            ParentMessageId = u.Value!.Id, IsFinal = false,
        });
        var t = await Sessions.AppendMessageAsync(new ChatMessage
        {
            SessionId = source.Id, Role = ChatRole.Tool, Content = "工具输出",
            ParentMessageId = a.Value!.Id, ToolCallId = "call-1", Name = "get_note",
        });

        // 分叉到 assistant（含）：父链完整 → fork 内重映射后仍互联。
        var forkAtAssistant = await ((ISessionFork)Sessions).ForkAsync(source.Id, a.Value!.Id);
        Assert.True(forkAtAssistant.IsSuccess);
        var fm = await Sessions.GetMessagesAsync(forkAtAssistant.Value!.Id);
        var forkUser = fm.Value!.Single(m => m.Role == ChatRole.User);
        var forkAssistant = fm.Value!.Single(m => m.Role == ChatRole.Assistant);
        Assert.Equal(forkUser.Id, forkAssistant.ParentMessageId);
        Assert.NotEqual(u.Value!.Id, forkUser.Id);

        // 分叉到 user（含）：assistant/tool 不在前缀内 → 只有 1 条。
        var forkAtUser = await ((ISessionFork)Sessions).ForkAsync(source.Id, u.Value!.Id);
        var fm2 = await Sessions.GetMessagesAsync(forkAtUser.Value!.Id);
        Assert.Single(fm2.Value!);
    }

    [Fact]
    public async Task Fork_CopiesUsageAndAttributionFields()
    {
        var source = await SeedAsync(2);
        var sourceMessages = await Sessions.GetMessagesAsync(source.Id);
        var assistant = sourceMessages.Value!.First(m => m.Role == ChatRole.Assistant);

        var forked = await ((ISessionFork)Sessions).ForkAsync(source.Id);
        var forkMessages = await Sessions.GetMessagesAsync(forked.Value!.Id);
        var forkAssistant = forkMessages.Value!.First(m => m.Role == ChatRole.Assistant);

        Assert.Equal(assistant.TokensIn, forkAssistant.TokensIn);
        Assert.Equal(assistant.TokensOut, forkAssistant.TokensOut);
        Assert.Equal(assistant.CostUsd, forkAssistant.CostUsd);
        Assert.Equal(assistant.ProviderId, forkAssistant.ProviderId);
        Assert.Equal(assistant.OrchestrationRole, forkAssistant.OrchestrationRole);
        Assert.Equal(assistant.Status, forkAssistant.Status);
    }

    [Fact]
    public async Task Fork_EmptySession_CreatesEmptyFork()
    {
        var source = await NewSessionAsync(OrchestrationStrategy.Single);
        var forked = await ((ISessionFork)Sessions).ForkAsync(source.Id);
        Assert.True(forked.IsSuccess);
        var forkMessages = await Sessions.GetMessagesAsync(forked.Value!.Id);
        Assert.Empty(forkMessages.Value!);
    }

    [Fact]
    public async Task Fork_UnknownSessionOrMessage_FailsHonest()
    {
        var unknownSession = await ((ISessionFork)Sessions).ForkAsync("ghost-session");
        Assert.False(unknownSession.IsSuccess);
        Assert.Contains("not found", unknownSession.Error);

        var source = await SeedAsync(2);
        var unknownMessage = await ((ISessionFork)Sessions).ForkAsync(source.Id, "ghost-message");
        Assert.False(unknownMessage.IsSuccess);
        Assert.Contains("ghost-message", unknownMessage.Error);

        // 失败路径不产生新会话。
        var listed = await Sessions.ListSessionsAsync();
        Assert.Single(listed.Value!);
    }
}
