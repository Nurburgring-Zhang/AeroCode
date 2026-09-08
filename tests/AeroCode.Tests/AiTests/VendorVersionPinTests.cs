// B6 版本 pinning / 弃用监控测试：pin 值来自配置、DeprecationMonitor 默认关闭 + allowlist + 不阻塞。
using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AeroCode.AI.Capabilities;
using AeroCode.AI.Configuration;
using AeroCode.AI.Models;
using AeroCode.AI.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AeroCode.Tests.Ai;

public class VendorVersionPinTests
{
    private sealed class HeaderCaptureHandler : HttpMessageHandler
    {
        public HttpResponseMessage Response { get; set; } = new(HttpStatusCode.OK)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };

        public string? LastRequestBody { get; private set; }
        public System.Net.Http.Headers.HttpRequestHeaders? LastHeaders { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            LastHeaders = req.Headers;
            if (req.Content is not null) LastRequestBody = req.Content.ReadAsStringAsync(ct).GetAwaiter().GetResult();
            return Task.FromResult(Response);
        }
    }

    private static ProviderConfig AnthropicConfig() => new()
    {
        Id = "claude", DisplayName = "Claude", Kind = "AnthropicMessages",
        BaseUrl = "https://api.anthropic.com", DefaultModel = "claude-5",
        RequiresApiKey = false
    };

    // ---------- VendorVersionPin.From（值来自配置，不硬编码） ----------

    [Fact]
    public void From_UnsetConfig_ReturnsNull()
    {
        var cfg = AnthropicConfig();
        Assert.Null(VendorVersionPin.From(cfg));
        cfg.ApiVersionHeaders = new System.Collections.Generic.Dictionary<string, string>();
        Assert.Null(VendorVersionPin.From(cfg));
        Assert.Null(VendorVersionPin.From(null!));
    }

    [Fact]
    public void From_ConfiguredHeaders_BuildsPinFromConfig()
    {
        var cfg = AnthropicConfig();
        cfg.ApiVersionHeaders = new System.Collections.Generic.Dictionary<string, string>
        {
            ["anthropic-version"] = "2099-01-01"
        };
        var pin = VendorVersionPin.From(cfg);
        Assert.NotNull(pin);
        Assert.Equal("claude", pin!.ProviderId);
        Assert.Equal("2099-01-01", pin.Headers["anthropic-version"]);
        // pin 值与配置一致（配置改 → pin 改，证明非硬编码）。
        cfg.ApiVersionHeaders["anthropic-version"] = "2099-02-02";
        Assert.Equal("2099-02-02", VendorVersionPin.From(cfg)!.Headers["anthropic-version"]);
    }

    // ---------- ClaudeProvider：pin 随请求头发送；null = 基线默认 ----------

    [Fact]
    public async Task Claude_WithConfiguredPin_SendsConfiguredVersionHeader()
    {
        var handler = new HeaderCaptureHandler();
        var cfg = AnthropicConfig();
        cfg.ApiVersionHeaders = new System.Collections.Generic.Dictionary<string, string>
        {
            ["anthropic-version"] = "2099-01-01"
        };
        var p = new ClaudeProvider(new HttpClient(handler), cfg, NullLogger<ClaudeProvider>.Instance);
        await p.ChatAsync(new ChatRequest { Messages = new[] { new ChatMessage { Role = "user", Content = "hi" } } });
        Assert.NotNull(handler.LastHeaders);
        Assert.True(handler.LastHeaders!.TryGetValues("anthropic-version", out var vals));
        Assert.Equal("2099-01-01", Assert.Single(vals!));
    }

    [Fact]
    public async Task Claude_WithoutPin_KeepsBaselineDefaultHeader()
    {
        var handler = new HeaderCaptureHandler();
        var p = new ClaudeProvider(new HttpClient(handler), AnthropicConfig(), NullLogger<ClaudeProvider>.Instance);
        await p.ChatAsync(new ChatRequest { Messages = new[] { new ChatMessage { Role = "user", Content = "hi" } } });
        Assert.True(handler.LastHeaders!.TryGetValues("anthropic-version", out var vals));
        Assert.Equal("2023-06-01", Assert.Single(vals!));
    }

    [Fact]
    public async Task OpenAiCompatible_WithConfiguredPin_SendsVersionHeader()
    {
        var handler = new HeaderCaptureHandler();
        var cfg = new ProviderConfig
        {
            Id = "openai", DisplayName = "OpenAI", Kind = "OpenAICompatible",
            BaseUrl = "https://api.openai.com/v1", DefaultModel = "gpt-5.2",
            RequiresApiKey = false,
            ApiVersionHeaders = new System.Collections.Generic.Dictionary<string, string>
            {
                ["x-api-version"] = "v9-final"
            }
        };
        var p = new OpenAIProvider(new HttpClient(handler), cfg, NullLogger<OpenAIProvider>.Instance);
        await p.ChatAsync(new ChatRequest
        {
            Messages = new[] { new ChatMessage { Role = "user", Content = "hi" } },
            EnableThinking = false
        });
        Assert.True(handler.LastHeaders!.TryGetValues("x-api-version", out var vals));
        Assert.Equal("v9-final", Assert.Single(vals!));
    }

    // ---------- DeprecationMonitor：默认关闭 / allowlist / 不阻塞 ----------

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public System.Collections.Generic.List<string> RequestedUrls { get; } = new();

        public ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            RequestedUrls.Add(req.RequestUri?.ToString() ?? "");
            return Task.FromResult(_responder(req));
        }
    }

    [Fact]
    public async Task DefaultDisabled_CheckAsync_DoesNoOutboundCall()
    {
        var handler = new ScriptedHandler(_ => throw new InvalidOperationException("must not be called"));
        var monitor = new DeprecationMonitor(enabled: false, urlAllowlist: new[] { "https://example.com/changelog" }, http: new HttpClient(handler));
        Assert.False(monitor.IsEnabled);
        var results = await monitor.CheckAsync();
        var r = Assert.Single(results);
        Assert.False(r.Enabled);
        Assert.False(r.Attempted);
        Assert.Empty(handler.RequestedUrls);
    }

    [Fact]
    public async Task EnabledWithEmptyAllowlist_DoesNoOutboundCall()
    {
        var handler = new ScriptedHandler(_ => throw new InvalidOperationException("must not be called"));
        var monitor = new DeprecationMonitor(enabled: true, urlAllowlist: Array.Empty<string>(), http: new HttpClient(handler));
        var results = await monitor.CheckAsync();
        Assert.All(results, r => Assert.False(r.Attempted));
        Assert.Empty(handler.RequestedUrls);
    }

    [Fact]
    public async Task EnabledWithAllowlist_OnlyAllowlistedUrlFetched_MentionsCounted()
    {
        var body = "Model X is deprecated; endpoint Y deprecated too.";
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/plain")
        });
        var monitor = new DeprecationMonitor(
            enabled: true,
            urlAllowlist: new[] { "https://example.com/changelog" },
            http: new HttpClient(handler));
        Assert.True(monitor.IsEnabled);
        var results = await monitor.CheckAsync();
        var r = Assert.Single(results);
        Assert.True(r.Attempted);
        Assert.True(r.Success);
        Assert.Equal(200, r.StatusCode);
        Assert.Equal(2, r.DeprecationMentions);
        var url = Assert.Single(handler.RequestedUrls);
        Assert.Equal("https://example.com/changelog", url);
        // 结果不落地响应体原文（脱敏）。
        Assert.DoesNotContain("deprecated; endpoint", r.ToString() ?? "");
    }

    [Fact]
    public async Task NetworkFailure_RecordedNotThrown()
    {
        var handler = new ScriptedHandler(_ => throw new HttpRequestException("dns fail"));
        var monitor = new DeprecationMonitor(
            enabled: true,
            urlAllowlist: new[] { "https://unreachable.invalid/changelog" },
            http: new HttpClient(handler));
        var results = await monitor.CheckAsync(); // 不抛
        var r = Assert.Single(results);
        Assert.True(r.Attempted);
        Assert.False(r.Success);
        Assert.Null(r.StatusCode);
        Assert.NotNull(r.Error);
    }

    [Fact]
    public async Task Non2xx_RecordedAsFailure()
    {
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var monitor = new DeprecationMonitor(
            enabled: true,
            urlAllowlist: new[] { "https://example.com/changelog" },
            http: new HttpClient(handler));
        var results = await monitor.CheckAsync();
        var r = Assert.Single(results);
        Assert.False(r.Success);
        Assert.Equal(503, r.StatusCode);
        Assert.Equal(0, r.DeprecationMentions);
    }

    [Fact]
    public void RedactUrl_StripsQueryFragmentAndUserinfo_KeepsHostPath()
    {
        // R4 审查加固：query（可能含 key）、fragment、userinfo（user:pass@）全部剥离；
        // 实测 GetLeftPart(Path) 原样携带 userinfo，RedactUrl 必须自行剥掉。
        var redacted = DeprecationMonitor.RedactUrl(
            "https://user:pass@example.com:8443/changelog?token=secret-key#frag");

        Assert.Equal("https://example.com:8443/changelog", redacted);
        Assert.DoesNotContain("pass", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-key", redacted, StringComparison.Ordinal);

        // 无 query/userinfo 的普通 URL 原样保留 host+path。
        Assert.Equal("http://aerocode.test/changelog",
            DeprecationMonitor.RedactUrl("http://aerocode.test/changelog"));

        // 不可解析 URL：诚实标记。
        Assert.Equal("<unparsed-url>", DeprecationMonitor.RedactUrl("not a url"));
    }
}
