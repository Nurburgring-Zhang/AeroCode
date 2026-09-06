// Copyright (c) AeroCode
// L2（R2 波次 γ）ContextCurator 真异步链路验证：CurateAsync await LLM 摘要（不再同步阻塞）、
// 与既有同步入口行为等价、取消传播不吞、摘要器故障确定性降级。
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AeroAgent.Moa.Curation;
using Xunit;
using AiChatMessage = AeroCode.AI.Models.ChatMessage;

namespace AeroCode.Tests.MoaTests;

public sealed class CuratorAsyncTests
{
    private static List<AiChatMessage> LongConversation(int dialogueCount = 20, int charsPerMessage = 200)
    {
        var messages = new List<AiChatMessage> { new() { Role = "system", Content = "stable system prefix" } };
        for (var i = 0; i < dialogueCount; i++)
        {
            messages.Add(new AiChatMessage
            {
                Role = "user",
                Content = $"第 {i} 轮对话内容：" + new string('x', charsPerMessage),
            });
        }

        return messages;
    }

    private static CurationInput Input(IReadOnlyList<AiChatMessage> conversation)
        => new()
        {
            Conversation = conversation,
            FrozenPrefixCount = 1, // system 前缀冻结
            WatermarkThresholdTokens = 500, // 20×50 token 对话区 ≫ 500 → 触发策展
        };

    private static ContextCurator NewCurator(CurationSummarizer? summarizer = null)
        => new(new CurationOptions
        {
            WatermarkThresholdTokens = 8000, // 由 input.WatermarkThresholdTokens 覆盖
            KeepRecentMessages = 2,
            Summarizer = summarizer,
        });

    [Fact]
    public async Task CurateAsync_AsyncSummarizer_AwaitsAndEmbedsSummary()
    {
        var summarizerCalled = 0;
        var curator = NewCurator(async _ =>
        {
            summarizerCalled++;
            await Task.Delay(50); // 真异步：模拟 LLM 往返
            return "LLM 摘要：对话概述";
        });
        var conversation = LongConversation();

        var result = await curator.CurateAsync(Input(conversation), CancellationToken.None);

        Assert.True(result.DidCurate);
        Assert.Equal(1, summarizerCalled);
        Assert.Contains("[摘要] LLM 摘要：对话概述", result.ExternalStateBlock);

        // 冻结前缀硬语义不因 L2 改造而变：system 实例逐字原样复用
        Assert.Same(conversation[0], result.Conversation[0]);
        Assert.Equal(1 + 1 + 2, result.Conversation.Count); // 冻结前缀 + 状态块 + 保留窗口
    }

    [Fact]
    public async Task CurateAsync_ParityWithLegacySyncCurate()
    {
        var curator = NewCurator(_ => Task.FromResult("确定摘要"));
        var conversation = LongConversation();

        var sync = curator.Curate(Input(conversation));
        var asyncResult = await curator.CurateAsync(Input(conversation), CancellationToken.None);

        // 同一实现核心（CurateCoreAsync）：两个入口行为等价（L2 只是消除同步阻塞）
        Assert.Equal(sync.ExternalStateBlock, asyncResult.ExternalStateBlock);
        Assert.Equal(sync.OverflowedFacts, asyncResult.OverflowedFacts);
        Assert.Equal(sync.DidCurate, asyncResult.DidCurate);
        Assert.Equal(sync.Conversation.Count, asyncResult.Conversation.Count);
        Assert.Equal(sync.CuratedTokens, asyncResult.CuratedTokens);
    }

    [Fact]
    public async Task CurateAsync_CancellationDuringSummary_PropagatesNotSwallowed()
    {
        // 摘要永不完成 → 取消落在 WaitAsync 上：OperationCanceledException 必须向上传播（不吞）。
        var neverCompletes = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var curator = NewCurator(_ => neverCompletes.Task);
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(150);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => curator.CurateAsync(Input(LongConversation()), cts.Token).AsTask());
    }

    [Fact]
    public async Task CurateAsync_SummarizerThrows_DegradesToDeterministicTemplate()
    {
        var curator = NewCurator(_ => throw new InvalidOperationException("llm endpoint down"));

        var result = await curator.CurateAsync(Input(LongConversation()), CancellationToken.None);

        // 摘要器故障不打断策展：降级为确定性模板（无 [摘要] 段，[最近动作] 摘要素材仍在）
        Assert.True(result.DidCurate);
        Assert.DoesNotContain("[摘要]", result.ExternalStateBlock);
        Assert.Contains("[最近动作]", result.ExternalStateBlock);
        Assert.Contains("<curated-state>", result.ExternalStateBlock);
    }

    [Fact]
    public async Task CurateAsync_BelowWatermark_SameInstanceNoCurate()
    {
        var curator = NewCurator(_ => Task.FromResult("不应被调用"));
        var conversation = new List<AiChatMessage>
        {
            new() { Role = "system", Content = "system" },
            new() { Role = "user", Content = "短消息" },
        };

        var result = await curator.CurateAsync(new CurationInput
        {
            Conversation = conversation,
            FrozenPrefixCount = 1,
            WatermarkThresholdTokens = 500,
        }, CancellationToken.None);

        Assert.False(result.DidCurate);
        Assert.Same(conversation, result.Conversation); // 未触发：与输入同实例
        Assert.Equal(string.Empty, result.ExternalStateBlock);
    }
}
