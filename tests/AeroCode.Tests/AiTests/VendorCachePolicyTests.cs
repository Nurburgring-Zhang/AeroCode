// B2 缓存适配器测试：三厂商 cache 政策字段值 + CacheBreakpoints 基线兼容 + provider 侧应用。
using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AeroCode.AI.Capabilities;
using AeroCode.AI.Configuration;
using AeroCode.AI.Models;
using AeroCode.AI.Providers;
using AeroAgent.Moa.Gateway;
using AeroCode.Tests.MoaTests;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AeroCode.Tests.Ai;

public class VendorCachePolicyTests
{
    // ---------- 政策字段值（A/G/O 三家数据模型） ----------

    [Fact]
    public void AnthropicPolicy_TtlAndWriteMultipliers_AreCorrect()
    {
        var p = VendorCachePolicies.For("A");
        Assert.NotNull(p);
        Assert.Equal(VendorCachePolicyKind.ExplicitBreakpointTtl, p!.Kind);
        Assert.Equal(new[] { "5m", "1h" }, p.SupportedTtls);
        Assert.Equal(1.25, p.CacheWriteMultiplier5m);
        Assert.Equal(2.0, p.CacheWriteMultiplier1h);
    }

    [Fact]
    public void GeminiPolicy_ImplicitDiscountAndMinPrefix_AreCorrect()
    {
        var p = VendorCachePolicies.For("G");
        Assert.NotNull(p);
        Assert.Equal(VendorCachePolicyKind.ImplicitSharedPrefix, p!.Kind);
        Assert.Equal(0.75, p.ImplicitCacheDiscount);
        Assert.Equal(1024, p.MinPrefixTokensFlash);
        Assert.Equal(2048, p.MinPrefixTokensPro);
    }

    [Fact]
    public void OpenAIPolicy_OffPeakDiscount_AreCorrect()
    {
        var p = VendorCachePolicies.For("O");
        Assert.NotNull(p);
        Assert.Equal(VendorCachePolicyKind.OffPeakDiscount, p!.Kind);
        Assert.Equal(0.5, p.OffPeakMultiplier);
        Assert.Equal("~1h", p.OffPeakWindow);
    }

    [Fact]
    public void For_AliasesAndUnknown_MapCorrectly()
    {
        Assert.NotNull(VendorCachePolicies.For("Anthropic"));
        Assert.NotNull(VendorCachePolicies.For("claude-sonnet-provider"));
        Assert.NotNull(VendorCachePolicies.For("Gemini"));
        Assert.NotNull(VendorCachePolicies.For("OpenAI"));
        Assert.Null(VendorCachePolicies.For("unknown-vendor"));
        Assert.Null(VendorCachePolicies.For(null));
        Assert.Null(VendorCachePolicies.For(""));
    }

    // ---------- ClaudeProvider：CacheBreakpoints=null = 基线请求形态 ----------

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

    [Fact]
    public async Task Claude_NullCacheBreakpoints_RequestBodyUnchangedFromBaseline()
    {
        var handler = new FakeHandler();
        var p = MakeClaude(handler);
        await p.ChatAsync(new ChatRequest
        {
            Messages = new[]
            {
                new ChatMessage { Role = "system", Content = "sys" },
                new ChatMessage { Role = "user", Content = "msg1" },
                new ChatMessage { Role = "user", Content = "msg2" }
            }
        });
        var body = handler.LastRequestBody ?? "";
        // 基线形态：无 cache_control、system 为纯字符串、消息 content 为纯字符串。
        Assert.DoesNotContain("cache_control", body);
        Assert.Contains("\"system\":\"sys\"", body);
        Assert.Contains("\"content\":\"msg1\"", body);
        Assert.Contains("\"content\":\"msg2\"", body);
    }

    [Fact]
    public async Task Claude_BreakpointOnMessage_AppliesCacheControlWithPolicyTtl()
    {
        var handler = new FakeHandler();
        var p = MakeClaude(handler);
        await p.ChatAsync(new ChatRequest
        {
            Messages = new[]
            {
                new ChatMessage { Role = "system", Content = "sys" },
                new ChatMessage { Role = "user", Content = "msg1" },
                new ChatMessage { Role = "user", Content = "msg2" }
            },
            CacheBreakpoints = new[] { 1 }
        });
        var body = handler.LastRequestBody ?? "";
        // 断点消息 content 升级为 block 数组并携带 cache_control（TTL 取自政策字段，默认 5m）。
        Assert.Contains("\"text\":\"msg1\",\"cache_control\":{\"type\":\"ephemeral\",\"ttl\":\"5m\"}", body);
        // 未命中消息保持基线形态；system 未命中保持纯字符串。
        Assert.Contains("\"system\":\"sys\"", body);
        Assert.Contains("\"content\":\"msg2\"", body);
    }

    [Fact]
    public async Task Claude_BreakpointOnSystem_SystemBecomesBlockWithCacheControl()
    {
        var handler = new FakeHandler();
        var p = MakeClaude(handler);
        await p.ChatAsync(new ChatRequest
        {
            Messages = new[]
            {
                new ChatMessage { Role = "system", Content = "sys" },
                new ChatMessage { Role = "user", Content = "msg1" }
            },
            CacheBreakpoints = new[] { 0 }
        });
        var body = handler.LastRequestBody ?? "";
        Assert.Contains("\"system\":[{\"type\":\"text\",\"text\":\"sys\",\"cache_control\":{\"type\":\"ephemeral\",\"ttl\":\"5m\"}}]", body);
        Assert.Contains("\"content\":\"msg1\"", body);
    }

    [Fact]
    public async Task Claude_OutOfRangeBreakpoint_IgnoredGracefully()
    {
        var handler = new FakeHandler();
        var p = MakeClaude(handler);
        await p.ChatAsync(new ChatRequest
        {
            Messages = new[] { new ChatMessage { Role = "user", Content = "only" } },
            CacheBreakpoints = new[] { 99 }
        });
        var body = handler.LastRequestBody ?? "";
        Assert.DoesNotContain("cache_control", body);
    }

    // ---------- OpenAICompatibleProvider：OpenAI 官方 cached_tokens 解析 ----------

    private static OpenAIProvider MakeOpenAi(FakeHandler handler)
    {
        var cfg = new ProviderConfig
        {
            Id = "openai", DisplayName = "OpenAI", Kind = "OpenAICompatible",
            BaseUrl = "https://api.openai.com/v1", DefaultModel = "gpt-5.2",
            RequiresApiKey = false
        };
        return new OpenAIProvider(new HttpClient(handler), cfg, NullLogger<OpenAIProvider>.Instance);
    }

    [Fact]
    public async Task OpenAi_PromptTokensDetailsCachedTokens_ParsedIntoUsage()
    {
        var handler = new FakeHandler
        {
            Response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"id\":\"c1\",\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"hi\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":2,\"total_tokens\":12,\"prompt_tokens_details\":{\"cached_tokens\":7}}}",
                    Encoding.UTF8, "application/json")
            }
        };
        var p = MakeOpenAi(handler);
        var resp = await p.ChatAsync(new ChatRequest
        {
            Messages = new[] { new ChatMessage { Role = "user", Content = "hi" } },
            EnableThinking = false
        });
        Assert.NotNull(resp.Usage);
        Assert.Equal(7, resp.Usage!.CachedTokens);
    }

    [Fact]
    public async Task OpenAi_DeepSeekStyleCacheHitTokens_StillParsed()
    {
        var handler = new FakeHandler
        {
            Response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"id\":\"c1\",\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"hi\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":2,\"total_tokens\":12,\"prompt_cache_hit_tokens\":9}}",
                    Encoding.UTF8, "application/json")
            }
        };
        var p = MakeOpenAi(handler);
        var resp = await p.ChatAsync(new ChatRequest
        {
            Messages = new[] { new ChatMessage { Role = "user", Content = "hi" } },
            EnableThinking = false
        });
        Assert.Equal(9, resp.Usage!.CachedTokens);
    }

    // ---------- MoaGatewayExecuteRequest：CacheBreakpoints=null 序列化与基线逐字节一致 ----------

    [Fact]
    public async Task GatewayExecute_NullCacheBreakpoints_SerializedBodyHasNoField()
    {
        using var handler = new GatewayFakeHttpHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(GatewayTestData.ExecuteEnvelope(), Encoding.UTF8, "application/json")
        });
        using var client = new MoaGatewayClient(
            new MoaGatewayClientOptions { BaseUrl = new Uri("http://127.0.0.1:8910"), ApiKey = null },
            handler);
        var result = await client.ExecuteAsync(new MoaGatewayExecuteRequest { Query = "q" });
        Assert.True(result.IsSuccess);
        var body = Assert.Single(handler.Requests).Body;
        Assert.NotNull(body);
        // 基线逐字节一致：null 字段被序列化器忽略，请求体不出现该字段（任何命名形式）。
        Assert.DoesNotContain("cacheBreakpoints", body);
        Assert.DoesNotContain("CacheBreakpoints", body);
        Assert.Contains("\"content\":\"q\"", body);
    }

    [Fact]
    public async Task GatewayExecute_WithCacheBreakpoints_WireContractUnchanged()
    {
        // CacheBreakpoints 是请求模型层字段（承载 Curation/A3 冻结前缀边界值，传值归缝合波次）；
        // moa-gateway-pro v3.1.1 的 wire 契约（ExecuteWireRequest）不受影响——β 只落字段。
        using var handler = new GatewayFakeHttpHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(GatewayTestData.ExecuteEnvelope(), Encoding.UTF8, "application/json")
        });
        using var client = new MoaGatewayClient(
            new MoaGatewayClientOptions { BaseUrl = new Uri("http://127.0.0.1:8910"), ApiKey = null },
            handler);
        var result = await client.ExecuteAsync(new MoaGatewayExecuteRequest { Query = "q", CacheBreakpoints = new[] { 0, 2 } });
        Assert.True(result.IsSuccess);
        var body = Assert.Single(handler.Requests).Body;
        Assert.DoesNotContain("CacheBreakpoints", body);
        Assert.DoesNotContain("cacheBreakpoints", body);
        Assert.Contains("\"content\":\"q\"", body);
    }

    [Fact]
    public void GatewayExecuteRecord_NullBreakpoint_DirectSerializationOmitsField()
    {
        // 直接对 record 序列化（默认选项）验证 [JsonIgnore(WhenWritingNull)]：null 字段省略。
        var json = JsonSerializer.Serialize(new MoaGatewayExecuteRequest { Query = "q" });
        Assert.DoesNotContain("CacheBreakpoints", json);
        Assert.DoesNotContain("cacheBreakpoints", json);
    }
}
