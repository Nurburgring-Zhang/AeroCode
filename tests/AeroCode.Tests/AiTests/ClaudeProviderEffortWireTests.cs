// Copyright (c) AeroCode
// R2 修复 HIGH-2 effort 峰值档 wire 语义测试：
// 1. 默认请求（EnableThinking=true, ThinkingEffort="high"）的 Claude 请求体必须与基线逐字节一致
//    ——thinking 块结构 + budget_tokens=5000 精确钉住（字节兼容铁律）；
// 2. 峰值档（ThinkingEffort="extended-thinking"）必须仍发射 thinking 块（wire 效果存在），
//    峰值预算刻意复用基线常量 5000（Anthropic 约束 budget_tokens < max_tokens，默认 MaxTokens=4096，
//    抬高必 400——按档抬升留 R3），故峰值请求体与基线逐字节一致；
// 3. 其他取值（含默认 "high"）一律走基线预算槽，不改变发射；
// 4. O 族（OpenAICompatible）xhigh 与基线 "high" 走同一 reasoning_effort 发射点（同点消费，无需新发射面）。
using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AeroCode.AI.Configuration;
using AeroCode.AI.Models;
using AeroCode.AI.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AeroCode.Tests.Ai;

public sealed class ClaudeProviderEffortWireTests
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        public HttpResponseMessage Response { get; set; } = new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"id\":\"msg_1\",\"model\":\"claude-5\",\"content\":[{\"type\":\"text\",\"text\":\"ok\"}],\"stop_reason\":\"end_turn\",\"usage\":{\"input_tokens\":5,\"output_tokens\":3}}",
                Encoding.UTF8, "application/json")
        };

        public string? LastRequestBody { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            if (req.Content is not null) LastRequestBody = req.Content.ReadAsStringAsync(ct).GetAwaiter().GetResult();
            return Task.FromResult(Response);
        }
    }

    private static ClaudeProvider MakeClaude(FakeHandler handler)
    {
        var cfg = new ProviderConfig
        {
            Id = "claude", DisplayName = "Claude", Kind = "AnthropicMessages",
            BaseUrl = "https://api.anthropic.com", DefaultModel = "claude-5",
            RequiresApiKey = false
        };
        return new ClaudeProvider(new HttpClient(handler), cfg, NullLogger<ClaudeProvider>.Instance);
    }

    private static async Task<string> SendAndGetBodyAsync(ChatRequest request)
    {
        var handler = new FakeHandler();
        var p = MakeClaude(handler);
        await p.ChatAsync(request);
        return handler.LastRequestBody ?? "";
    }

    [Fact]
    public async Task Claude_DefaultRequest_ThinkingBlockByteCompatBaseline()
    {
        // 默认态（不显式设置任何 effort 字段）：EnableThinking=true、ThinkingEffort="high" 均为模型默认值。
        // 字节兼容铁律：请求体必须含基线形态的 thinking 块，且 budget_tokens 精确等于 5000。
        var body = await SendAndGetBodyAsync(new ChatRequest
        {
            Messages = new[] { new ChatMessage { Role = "user", Content = "hi" } }
        });
        Assert.Contains("\"thinking\":{\"type\":\"enabled\",\"budget_tokens\":5000}", body);
        Assert.DoesNotContain("reasoning_effort", body);
    }

    [Fact]
    public async Task Claude_ExplicitHighEffort_BodyIdenticalToDefault()
    {
        // 显式 "high"（其他值口径）：发射结构不变——与默认态请求体逐字节一致。
        var defaultBody = await SendAndGetBodyAsync(new ChatRequest
        {
            Messages = new[] { new ChatMessage { Role = "user", Content = "hi" } }
        });
        var highBody = await SendAndGetBodyAsync(new ChatRequest
        {
            Messages = new[] { new ChatMessage { Role = "user", Content = "hi" } },
            ThinkingEffort = "high"
        });
        Assert.Equal(defaultBody, highBody);
    }

    [Fact]
    public async Task Claude_PeakEffort_StillEmitsThinkingBlock()
    {
        // 峰值档（A 族 extended-thinking）：wire 效果存在——thinking 块照常发射；
        // 峰值预算复用基线常量 5000（预算抬高受 max_tokens 约束，留 R3），
        // 故请求体与基线逐字节一致（发射与否、发射结构均与基线完全一致）。
        var defaultBody = await SendAndGetBodyAsync(new ChatRequest
        {
            Messages = new[] { new ChatMessage { Role = "user", Content = "hi" } }
        });
        var peakBody = await SendAndGetBodyAsync(new ChatRequest
        {
            Messages = new[] { new ChatMessage { Role = "user", Content = "hi" } },
            ThinkingEffort = "extended-thinking"
        });
        Assert.Contains("\"thinking\":{\"type\":\"enabled\",\"budget_tokens\":5000}", peakBody);
        Assert.Equal(defaultBody, peakBody);
    }

    [Fact]
    public async Task Claude_ThinkingDisabled_NoThinkingBlockRegardlessOfEffort()
    {
        // 发射与否只由 EnableThinking 决定：关掉后任何 effort 取值都不得发射 thinking 块。
        var body = await SendAndGetBodyAsync(new ChatRequest
        {
            Messages = new[] { new ChatMessage { Role = "user", Content = "hi" } },
            EnableThinking = false,
            ThinkingEffort = "extended-thinking"
        });
        Assert.DoesNotContain("\"thinking\"", body);
    }

    [Fact]
    public async Task OpenAi_XhighEffort_EmitsReasoningEffortThroughBaselinePoint()
    {
        // O 族（xhigh）：与基线 "high" 走同一 reasoning_effort 发射点（OpenAICompatibleProvider 已消费
        // ThinkingEffort 字段），无需新增发射面——R2 修复 HIGH-2 的 O 族结论钉住。
        var handler = new FakeHandler
        {
            Response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"id\":\"c1\",\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"hi\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":2,\"total_tokens\":12}}",
                    Encoding.UTF8, "application/json")
            }
        };
        var cfg = new ProviderConfig
        {
            Id = "openai", DisplayName = "OpenAI", Kind = "OpenAICompatible",
            BaseUrl = "https://api.openai.com/v1", DefaultModel = "gpt-5.2",
            RequiresApiKey = false
        };
        var p = new OpenAIProvider(new HttpClient(handler), cfg, NullLogger<OpenAIProvider>.Instance);
        await p.ChatAsync(new ChatRequest
        {
            Messages = new[] { new ChatMessage { Role = "user", Content = "hi" } },
            ThinkingEffort = "xhigh"
        });
        var body = handler.LastRequestBody ?? "";
        Assert.Contains("\"reasoning_effort\":\"xhigh\"", body);
        Assert.Contains("\"thinking\":{\"type\":\"enabled\"}", body);
    }
}
