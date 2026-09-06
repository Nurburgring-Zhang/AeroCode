// Copyright (c) AeroCode
// R3-δ O 家族 xhigh probe-gated 发射测试（wire 层）：仅当 probe 证实端点 EffortTiers 才发射
// reasoning_effort=xhigh；离线/未证实（Missing）→ 维持现行为（回落基线档 "high"，绝不发射未证实的
// xhigh）；probe 未注入（null = 现行为）→ 请求原样透传（R2 钉死的发射点不变）。
using System.Net;
using System.Text;
using AeroCode.AI.Capabilities;
using AeroCode.AI.Configuration;
using AeroCode.AI.Models;
using AeroCode.AI.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AeroCode.Tests.Ai;

public sealed class OpenAiXhighProbeGateTests
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        public HttpResponseMessage Response { get; set; } = new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"id\":\"c1\",\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"hi\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":2,\"total_tokens\":12}}",
                Encoding.UTF8, "application/json")
        };

        public string? LastRequestBody { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            if (req.Content is not null) LastRequestBody = req.Content.ReadAsStringAsync(ct).GetAwaiter().GetResult();
            return Task.FromResult(Response);
        }
    }

    /// <summary>脚本化探测替身：按脚本返回三态并记录调用（零网络）。</summary>
    private sealed class StubProbe : IVendorCapabilityProbe
    {
        private readonly VendorCapabilityState _state;
        private readonly bool _throw;

        public StubProbe(VendorCapabilityState state, bool @throw = false)
        {
            _state = state;
            _throw = @throw;
        }

        public List<(string ProviderId, VendorCapability Capability)> Calls { get; } = new();

        public Task<VendorCapabilityState> ProbeAsync(string providerId, VendorCapability capability, CancellationToken ct = default)
        {
            Calls.Add((providerId, capability));
            if (_throw)
            {
                throw new InvalidOperationException("probe transport exploded");
            }

            return Task.FromResult(_state);
        }
    }

    private static ProviderConfig MakeConfig() => new()
    {
        Id = "openai", DisplayName = "OpenAI", Kind = "OpenAICompatible",
        BaseUrl = "https://api.openai.com/v1", DefaultModel = "gpt-5.2",
        RequiresApiKey = false
    };

    private static async Task<string> SendAndGetBodyAsync(IVendorCapabilityProbe? probe)
    {
        var handler = new FakeHandler();
        var p = new OpenAIProvider(new HttpClient(handler), MakeConfig(), NullLogger<OpenAIProvider>.Instance, capabilityProbe: probe);
        await p.ChatAsync(new ChatRequest
        {
            Messages = new[] { new ChatMessage { Role = "user", Content = "hi" } },
            ThinkingEffort = "xhigh"
        });
        return handler.LastRequestBody ?? "";
    }

    [Fact]
    public async Task ProbeSupported_EmitsXhigh()
    {
        var probe = new StubProbe(VendorCapabilityState.Supported);
        var body = await SendAndGetBodyAsync(probe);
        Assert.Contains("\"reasoning_effort\":\"xhigh\"", body);
        Assert.Single(probe.Calls); // 恰好一次探测
        Assert.Equal(("openai", VendorCapability.EffortTiers), probe.Calls[0]);
    }

    [Fact]
    public async Task ProbeDowngraded_EmitsXhigh()
    {
        // Downgraded = 文档化降级路径（有据），允许峰值档发射（与 EffortProfile fail-closed 语义一致）。
        var probe = new StubProbe(VendorCapabilityState.Downgraded);
        var body = await SendAndGetBodyAsync(probe);
        Assert.Contains("\"reasoning_effort\":\"xhigh\"", body);
    }

    [Fact]
    public async Task ProbeMissing_FallsBackToBaselineHigh_NeverEmitsXhigh()
    {
        // 钉死分支：未证实（离线/探测失败收敛 Missing）→ 绝不发射 xhigh，维持现行为（基线档 high）。
        var probe = new StubProbe(VendorCapabilityState.Missing);
        var body = await SendAndGetBodyAsync(probe);
        Assert.DoesNotContain("xhigh", body);
        Assert.Contains("\"reasoning_effort\":\"high\"", body);
    }

    [Fact]
    public async Task ProbeThrows_FailsClosed_FallsBackToBaselineHigh()
    {
        // 探测通道故障：fail-closed，绝不发射未证实的 xhigh。
        var probe = new StubProbe(VendorCapabilityState.Missing, @throw: true);
        var body = await SendAndGetBodyAsync(probe);
        Assert.DoesNotContain("xhigh", body);
        Assert.Contains("\"reasoning_effort\":\"high\"", body);
    }

    [Fact]
    public async Task ProbeNotInjected_RequestPassedThroughUnchanged()
    {
        // probe 未注入（null = 现行为）：xhigh 按请求发射（R2 ClaudeProviderEffortWireTests 钉住的发射点不变）。
        var body = await SendAndGetBodyAsync(probe: null);
        Assert.Contains("\"reasoning_effort\":\"xhigh\"", body);
    }

    [Fact]
    public async Task NonXhighEffort_ProbeNeverConsulted()
    {
        // 门控只针对峰值档 token "xhigh"：基线档 "high" 请求不触发探测（零探测调用）。
        var probe = new StubProbe(VendorCapabilityState.Supported);
        var handler = new FakeHandler();
        var p = new OpenAIProvider(new HttpClient(handler), MakeConfig(), NullLogger<OpenAIProvider>.Instance, capabilityProbe: probe);
        await p.ChatAsync(new ChatRequest
        {
            Messages = new[] { new ChatMessage { Role = "user", Content = "hi" } },
            ThinkingEffort = "high"
        });
        Assert.Empty(probe.Calls);
        Assert.Contains("\"reasoning_effort\":\"high\"", handler.LastRequestBody ?? "");
    }
}
