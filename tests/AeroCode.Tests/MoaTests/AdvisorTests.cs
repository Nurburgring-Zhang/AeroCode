// Copyright (c) AeroCode
// PermissionAdvisor 测试（builder-β）：容错（围栏/废话包裹/坏 JSON/缺字段）、
// 独立超时、provider 故障、未配置 → 全部诚实返回 Unknown，绝不抛异常阻塞审批主链。
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AeroAgent.Moa.Safety;
using AeroCode.AI.Models;
using AeroCode.AI.Providers;
using Xunit;

namespace AeroCode.Tests.MoaTests;

/// <summary>
/// 建议器专用测试 provider：可编程返回内容/延迟/抛错，并记录收到的请求
/// （与 ConversationTests.ScriptedProvider 的差异：ChatAsync 支持延迟，超时测试需要）。
/// </summary>
internal sealed class AdvisorScriptedProvider : IAiProvider
{
    public string ProviderId { get; init; } = "advisor-scripted";
    public string DisplayName => "AdvisorScripted";
    public ProviderKind Kind => ProviderKind.OpenAICompatible;
    public bool SupportsStreaming => true;
    public bool SupportsToolCalling => false;
    public bool SupportsThinking => false;

    public Queue<string> ContentQueue { get; } = new();
    public string DefaultContent { get; set; } = string.Empty;
    public Exception? ThrowOnChat { get; set; }
    public int DelayMs { get; set; }
    public int CallCount { get; private set; }
    public ChatRequest? LastRequest { get; private set; }

    public async Task<ChatResponse> ChatAsync(ChatRequest request, CancellationToken ct = default)
    {
        CallCount++;
        LastRequest = request;
        if (DelayMs > 0)
        {
            await Task.Delay(DelayMs, ct);
        }

        if (ThrowOnChat is not null)
        {
            throw ThrowOnChat;
        }

        return new ChatResponse
        {
            Id = "resp",
            Model = request.Model,
            Content = ContentQueue.Count > 0 ? ContentQueue.Dequeue() : DefaultContent,
            FinishReason = "stop",
        };
    }

    public async IAsyncEnumerable<ChatChunk> StreamChatAsync(ChatRequest request, CancellationToken ct = default)
    {
        await Task.CompletedTask;
        yield break;
    }

    public Task<bool> HealthCheckAsync(CancellationToken ct = default) => Task.FromResult(true);
}

/// <summary>
/// 建议器钉子：契约"任何失败返回 Unknown"必须钉死——advisor 故障绝不影响审批主链；
/// IsAvailable=false 时零调用、行为不变。
/// </summary>
public sealed class AdvisorTests
{
    private static PermissionAdvice Advise(PermissionAdvisor advisor) =>
        advisor.AdviseAsync("run_shell", new Dictionary<string, object?> { ["command"] = "git status" }, CancellationToken.None)
            .GetAwaiter().GetResult();

    [Fact]
    public void NotConfigured_NullProvider_Unknown_WithoutCallingAnything()
    {
        var advisor = new PermissionAdvisor(null, "cheap-model");
        Assert.False(advisor.IsAvailable);
        Assert.Equal(PermissionAdvice.Unknown(), Advise(advisor));
    }

    [Fact]
    public void NotConfigured_BlankModel_Unknown_WithoutCallingProvider()
    {
        var provider = new AdvisorScriptedProvider();
        var advisor = new PermissionAdvisor(provider, "  ");
        Assert.False(advisor.IsAvailable);
        Assert.Equal(PermissionAdvice.Unknown(), Advise(advisor));
        Assert.Equal(0, provider.CallCount); // 未配置 = 零网络调用
    }

    [Fact]
    public void ValidJson_MapsAdvice_AndSendsMinimalPrompt()
    {
        var provider = new AdvisorScriptedProvider
        {
            DefaultContent = "{\"recommend\":\"allow\",\"risk\":\"low\",\"reason\":\"read-only git status\"}",
        };
        var advisor = new PermissionAdvisor(provider, "cheap-model");

        var advice = Advise(advisor);

        Assert.Equal(("allow", "low", "read-only git status"), (advice.Recommend, advice.Risk, advice.Reason));
        Assert.Equal(1, provider.CallCount);
        var request = provider.LastRequest!;
        Assert.Equal("cheap-model", request.Model);
        Assert.Equal(2, request.Messages.Count); // system + user
        Assert.Equal(256, request.MaxTokens); // 判定请求保持小而快
        Assert.False(request.EnableThinking);
        Assert.Contains("run_shell", request.Messages[1].Content);
        Assert.Contains("git status", request.Messages[1].Content);
    }

    [Fact]
    public void FencedJsonWithSurroundingProse_Tolerated()
    {
        var provider = new AdvisorScriptedProvider
        {
            DefaultContent = "Here is my judgment:\n```json\n{\"recommend\":\"ask\",\"risk\":\"medium\",\"reason\":\"modifies files\"}\n```\nHope it helps.",
        };
        var advisor = new PermissionAdvisor(provider, "cheap-model");

        var advice = Advise(advisor);

        Assert.Equal(("ask", "medium", "modifies files"), (advice.Recommend, advice.Risk, advice.Reason));
    }

    [Fact]
    public void CaseInsensitiveKeys_AndSynonymRecommend_Accepted()
    {
        var provider = new AdvisorScriptedProvider
        {
            DefaultContent = "{\"RECOMMEND\":\"Deny\",\"Risk\":\"High\",\"REASON\":\"destructive\"}",
        };
        var advisor = new PermissionAdvisor(provider, "cheap-model");

        var advice = Advise(advisor);

        Assert.Equal(("deny", "high", "destructive"), (advice.Recommend, advice.Risk, advice.Reason));
    }

    [Fact]
    public void GarbageText_Unknown()
    {
        var provider = new AdvisorScriptedProvider { DefaultContent = "I think this is fine, go ahead!" };
        var advisor = new PermissionAdvisor(provider, "cheap-model");
        Assert.Equal(PermissionAdvice.Unknown(), Advise(advisor));
    }

    [Fact]
    public void MalformedJson_Unknown()
    {
        var provider = new AdvisorScriptedProvider { DefaultContent = "{\"recommend\": allow}" }; // 非法 JSON（键值未引号）
        var advisor = new PermissionAdvisor(provider, "cheap-model");
        Assert.Equal(PermissionAdvice.Unknown(), Advise(advisor));
    }

    [Fact]
    public void MissingRecommendField_Unknown()
    {
        var provider = new AdvisorScriptedProvider { DefaultContent = "{\"risk\":\"low\",\"reason\":\"ok\"}" };
        var advisor = new PermissionAdvisor(provider, "cheap-model");
        Assert.Equal(PermissionAdvice.Unknown(), Advise(advisor));
    }

    [Fact]
    public void UnrecognizableRecommend_Unknown()
    {
        var provider = new AdvisorScriptedProvider { DefaultContent = "{\"recommend\":\"maybe\",\"risk\":\"low\"}" };
        var advisor = new PermissionAdvisor(provider, "cheap-model");
        Assert.Equal(PermissionAdvice.Unknown(), Advise(advisor));
    }

    [Fact]
    public void InvalidRiskValue_NormalizedToUnknown_AdviceKept()
    {
        var provider = new AdvisorScriptedProvider { DefaultContent = "{\"recommend\":\"allow\",\"risk\":\"EXTREME\",\"reason\":\"fine\"}" };
        var advisor = new PermissionAdvisor(provider, "cheap-model");

        var advice = Advise(advisor);

        Assert.Equal(("allow", "unknown", "fine"), (advice.Recommend, advice.Risk, advice.Reason));
    }

    [Fact]
    public void Timeout_ReturnsUnknown_Quickly()
    {
        var provider = new AdvisorScriptedProvider { DelayMs = 30_000 }; // 远超判定超时
        var advisor = new PermissionAdvisor(provider, "cheap-model", TimeSpan.FromMilliseconds(150));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var advice = Advise(advisor);
        sw.Stop();

        Assert.Equal(PermissionAdvice.Unknown(), advice);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"advisor must not hang; took {sw.Elapsed}");
        Assert.Equal(1, provider.CallCount);
    }

    [Fact]
    public void ProviderThrows_Unknown()
    {
        var provider = new AdvisorScriptedProvider { ThrowOnChat = new InvalidOperationException("boom") };
        var advisor = new PermissionAdvisor(provider, "cheap-model");
        Assert.Equal(PermissionAdvice.Unknown(), Advise(advisor));
    }

    [Fact]
    public void ParseAdvice_UnitBoundaries()
    {
        Assert.Equal(PermissionAdvice.Unknown(), PermissionAdvisor.ParseAdvice(null));
        Assert.Equal(PermissionAdvice.Unknown(), PermissionAdvisor.ParseAdvice(""));
        Assert.Equal(PermissionAdvice.Unknown(), PermissionAdvisor.ParseAdvice("no braces here"));
        // 非对象根 → Unknown
        Assert.Equal(PermissionAdvice.Unknown(), PermissionAdvisor.ParseAdvice("[1,2,3]"));
        // recommend 缺失 reason 兜底为空串
        var partial = PermissionAdvisor.ParseAdvice("{\"recommend\":\"allow\"}");
        Assert.Equal(("allow", "unknown", string.Empty), (partial.Recommend, partial.Risk, partial.Reason));
    }
}
