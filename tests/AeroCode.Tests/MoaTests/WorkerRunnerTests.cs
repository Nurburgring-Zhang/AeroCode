using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AeroAgent.Conversation.Models;
using AeroAgent.Conversation.Orchestration;
using AeroAgent.Moa.Assignment;
using AeroAgent.Moa.Profiles;
using AeroAgent.Moa.Strategies;
using Xunit;
using AiChatMessage = AeroCode.AI.Models.ChatMessage;

namespace AeroCode.Tests.MoaTests;

/// <summary>
/// WorkerRunner 行为回归：取消与失败在画像统计中的语义必须分开——
/// 用户取消不是模型质量问题，不得污染失败率打分；真实失败照常计入。
/// </summary>
public sealed class WorkerRunnerTests : MoaTestBase
{
    private async Task<(OrchestrationContext Ctx, ModelAssignment Assignment)> SetupAsync(
        string providerId, Action<AeroCode.Tests.ConversationTests.ScriptedProvider> configure)
    {
        var provider = AddProvider(providerId);
        configure(provider);
        var profile = SetProfile(providerId, new[] { ModelStrength.General });
        var session = await NewSessionAsync(OrchestrationStrategy.Single);
        var ctx = new OrchestrationContext
        {
            Session = session,
            History = Array.Empty<ChatMessage>(),
            UserMessageId = "msg-user",
            Providers = Registry,
        };
        var assignment = new ModelAssignment(providerId, string.Empty, profile);
        return (ctx, assignment);
    }

    private static readonly IReadOnlyList<AiChatMessage> Prompt = new List<AiChatMessage>
    {
        new() { Role = "user", Content = "你好" },
    };

    [Fact]
    public async Task CancelledStream_NotCountedInProfileStats()
    {
        var (ctx, assignment) = await SetupAsync("slow", p =>
        {
            p.Deltas = new[] { "一", "二", "三", "四", "五" };
            p.DelayMsPerChunk = 300; // 取消必然落在第一个 chunk 的等待期间
        });

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(50);

        var outcome = await Runner.RunAsync(
            ctx, assignment, StrategyRole.Worker,
            parentMessageId: null, label: null,
            Prompt, stream: true, isFinal: true,
            sink: null, budget: null, cts.Token);

        Assert.True(outcome.Cancelled);
        Assert.False(outcome.Succeeded);
        Assert.Equal("cancelled by user", outcome.Error);
        Assert.Equal(0.0, outcome.CostUsd);

        // 取消不计入画像统计：Calls/Failures 保持零，失败率打分不被污染
        var profile = Catalog.Find("slow", string.Empty);
        Assert.NotNull(profile);
        Assert.Equal(0, profile!.Stats.Calls);
        Assert.Equal(0, profile.Stats.Failures);

        // 落库终态如实为 Cancelled，无内容残留
        var messages = (await Sessions.GetMessagesAsync(ctx.Session.Id)).Value!;
        var msg = Assert.Single(messages);
        Assert.Equal(MessageStatus.Cancelled, msg.Status);
        Assert.Equal(string.Empty, msg.Content);
    }

    [Fact]
    public async Task FailedStream_CountedInProfileStats()
    {
        // 对照组：真实失败照常计入（保证"取消不计"不是把整条统计链路砍断）
        var (ctx, assignment) = await SetupAsync("flaky", p =>
        {
            p.Deltas = new[] { "开头" };
            p.ThrowDuringStream = new InvalidOperationException("流中断");
        });

        var outcome = await Runner.RunAsync(
            ctx, assignment, StrategyRole.Worker,
            parentMessageId: null, label: null,
            Prompt, stream: true, isFinal: true,
            sink: null, budget: null, CancellationToken.None);

        Assert.False(outcome.Cancelled);
        Assert.False(outcome.Succeeded);
        Assert.Contains("流中断", outcome.Error);

        var profile = Catalog.Find("flaky", string.Empty);
        Assert.NotNull(profile);
        Assert.Equal(1, profile!.Stats.Calls);
        Assert.Equal(1, profile.Stats.Failures);

        // 已产出的部分如实保留
        var messages = (await Sessions.GetMessagesAsync(ctx.Session.Id)).Value!;
        var msg = Assert.Single(messages);
        Assert.Equal(MessageStatus.Failed, msg.Status);
        Assert.Equal("开头", msg.Content);
    }
}
