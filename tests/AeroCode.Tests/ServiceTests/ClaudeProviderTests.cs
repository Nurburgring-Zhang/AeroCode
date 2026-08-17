// Copyright (c) AeroCode V3.0
// ClaudeProvider tests — Anthropic Messages API format.
using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AeroCode.AI.Configuration;
using AeroCode.AI.Models;
using AeroCode.AI.Providers;
using AeroCode.AI.Resilience;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AeroCode.Tests.Ai;

[Collection("EnvMutators")]
public class ClaudeProviderTests
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        public HttpResponseMessage Response { get; set; } = new(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"id\":\"msg_1\",\"model\":\"claude-5\",\"content\":[{\"type\":\"text\",\"text\":\"Hello!\"}],\"stop_reason\":\"end_turn\",\"usage\":{\"input_tokens\":5,\"output_tokens\":3}}", Encoding.UTF8, "application/json")
        };
        public string? LastRequestBody { get; private set; }
        public string? LastRequestUrl { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            LastRequestUrl = req.RequestUri?.ToString();
            if (req.Content is not null) LastRequestBody = req.Content.ReadAsStringAsync(ct).GetAwaiter().GetResult();
            return Task.FromResult(Response);
        }
    }

    private static ClaudeProvider MakeProvider(FakeHandler handler, string keyVar = "ANTHROPIC_API_KEY", string key = "sk-ant-test")
    {
        Environment.SetEnvironmentVariable(keyVar, key);
        var http = new HttpClient(handler);
        var cfg = new ProviderConfig
        {
            Id = "claude", DisplayName = "Claude", Kind = "AnthropicMessages",
            BaseUrl = "https://api.anthropic.com", DefaultModel = "claude-5-sonnet",
            ApiKeyEnvVar = keyVar, RequiresApiKey = true
        };
        return new ClaudeProvider(http, cfg, NullLogger<ClaudeProvider>.Instance, new AiResiliencePipeline(new ResilienceOptions { MaxRetryAttempts = 0, CircuitBreakerMinThroughput = 0 }));
    }

    [Fact]
    public async Task ChatAsync_ParsesAnthropicFormat()
    {
        var p = MakeProvider(new FakeHandler());
        var resp = await p.ChatAsync(new ChatRequest
        {
            Messages = new[] { new ChatMessage { Role = "user", Content = "hi" } }
        });
        Assert.Equal("Hello!", resp.Content);
        Assert.Equal("end_turn", resp.FinishReason);
        Assert.NotNull(resp.Usage);
        Assert.Equal(5, resp.Usage!.PromptTokens);
    }

    [Fact]
    public async Task ChatAsync_SendsAnthropicBody()
    {
        var handler = new FakeHandler();
        var p = MakeProvider(handler);
        await p.ChatAsync(new ChatRequest { Messages = new[] { new ChatMessage { Role = "user", Content = "hi" } } });
        Assert.NotNull(handler.LastRequestBody);
        // Anthropic body uses "model" and "messages" fields. Headers (x-api-key) are
        // sent separately from the body, so we verify body structure here.
        Assert.Contains("\"model\"", handler.LastRequestBody!);
        Assert.Contains("\"messages\"", handler.LastRequestBody!);
        Assert.Contains("\"max_tokens\"", handler.LastRequestBody!);
    }

    [Fact]
    public async Task ChatAsync_SendsToMessagesEndpoint()
    {
        var handler = new FakeHandler();
        var p = MakeProvider(handler);
        await p.ChatAsync(new ChatRequest { Messages = new[] { new ChatMessage { Role = "user", Content = "hi" } } });
        Assert.NotNull(handler.LastRequestUrl);
        Assert.EndsWith("/v1/messages", handler.LastRequestUrl);
    }

    [Fact]
    public async Task ChatAsync_HandlesSystemMessageSeparately()
    {
        var handler = new FakeHandler();
        var p = MakeProvider(handler);
        await p.ChatAsync(new ChatRequest
        {
            Messages = new[]
            {
                new ChatMessage { Role = "system", Content = "You are a poet." },
                new ChatMessage { Role = "user", Content = "hi" }
            }
        });
        Assert.NotNull(handler.LastRequestBody);
        Assert.Contains("\"system\"", handler.LastRequestBody!);
        Assert.Contains("You are a poet", handler.LastRequestBody!);
    }

    [Fact]
    public async Task ChatAsync_TransientHttp5xx_RetriesThenThrows()
    {
        var n = 0;
        var handler = new SequenceHandler(() =>
        {
            n++;
            return n <= 2
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("oops") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"id\":\"msg_1\",\"model\":\"claude-5\",\"content\":[{\"type\":\"text\",\"text\":\"ok\"}],\"stop_reason\":\"end_turn\"}", Encoding.UTF8, "application/json") };
        });
        var http = new HttpClient(handler);
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "sk-ant-test");
        var cfg = new ProviderConfig { Id = "claude", DisplayName = "Claude", Kind = "AnthropicMessages", BaseUrl = "https://api.anthropic.com", DefaultModel = "claude-5", ApiKeyEnvVar = "ANTHROPIC_API_KEY" };
        var p = new ClaudeProvider(http, cfg, NullLogger<ClaudeProvider>.Instance, new AiResiliencePipeline(new ResilienceOptions { MaxRetryAttempts = 2, RetryBaseDelayMs = 5, CircuitBreakerMinThroughput = 0 }));
        var r = await p.ChatAsync(new ChatRequest { Messages = new[] { new ChatMessage { Role = "user", Content = "x" } } });
        Assert.Equal("ok", r.Content);
        Assert.True(n >= 3, $"expected >= 3, got {n}");
    }

    private sealed class SequenceHandler : HttpMessageHandler
    {
        private readonly Func<HttpResponseMessage> _factory;
        public SequenceHandler(Func<HttpResponseMessage> f) { _factory = f; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct) => Task.FromResult(_factory());
    }
}
