// Copyright (c) AeroCode V3.0
// Provider subclass tests — covers Qwen, Glm, Kimi, OpenAI, OpenRouter, Ollama, LmStudio, CustomProvider.
// All use a shared FakeHandler that returns the standard OpenAI chat completions payload.
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

[Collection("EnvMutators")]
public class ProviderSubclassTests
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        public HttpResponseMessage Response { get; set; } = new(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"id\":\"x\",\"model\":\"m\",\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"hi\"},\"finish_reason\":\"stop\"}]}", Encoding.UTF8, "application/json")
        };
        public string? LastRequestBody { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            if (req.Content is not null) LastRequestBody = req.Content.ReadAsStringAsync(ct).GetAwaiter().GetResult();
            return Task.FromResult(Response);
        }
    }

    private static ProviderConfig MakeCfg(string id, string kind = "OpenAICompatible") => new()
    {
        Id = id, DisplayName = id, Kind = kind, BaseUrl = "https://example.com/v1",
        DefaultModel = "test-model", ApiKeyEnvVar = null, RequiresApiKey = false
    };

    [Fact] public async Task Qwen_ChatAsync_ParsesContent()
    {
        var p = new QwenProvider(new HttpClient(new FakeHandler()), MakeCfg("qwen"), NullLogger<QwenProvider>.Instance);
        var r = await p.ChatAsync(new ChatRequest { Messages = new[] { new ChatMessage { Role = "user", Content = "hi" } } });
        Assert.Equal("hi", r.Content);
    }

    [Fact] public async Task Kimi_ChatAsync_ParsesContent()
    {
        var p = new KimiProvider(new HttpClient(new FakeHandler()), MakeCfg("kimi"), NullLogger<KimiProvider>.Instance);
        var r = await p.ChatAsync(new ChatRequest { Messages = new[] { new ChatMessage { Role = "user", Content = "hi" } } });
        Assert.Equal("hi", r.Content);
    }

    [Fact] public async Task Glm_ChatAsync_ParsesContent()
    {
        var p = new GlmProvider(new HttpClient(new FakeHandler()), MakeCfg("glm"), NullLogger<GlmProvider>.Instance);
        var r = await p.ChatAsync(new ChatRequest { Messages = new[] { new ChatMessage { Role = "user", Content = "hi" } } });
        Assert.Equal("hi", r.Content);
    }

    [Fact] public async Task OpenAI_ChatAsync_ParsesContent()
    {
        var p = new OpenAIProvider(new HttpClient(new FakeHandler()), MakeCfg("openai"), NullLogger<OpenAIProvider>.Instance);
        var r = await p.ChatAsync(new ChatRequest { Messages = new[] { new ChatMessage { Role = "user", Content = "hi" } } });
        Assert.Equal("hi", r.Content);
    }

    [Fact] public async Task OpenRouter_ChatAsync_ParsesContent()
    {
        var p = new OpenRouterProvider(new HttpClient(new FakeHandler()), MakeCfg("openrouter"), NullLogger<OpenRouterProvider>.Instance);
        var r = await p.ChatAsync(new ChatRequest { Messages = new[] { new ChatMessage { Role = "user", Content = "hi" } } });
        Assert.Equal("hi", r.Content);
    }

    [Fact] public async Task Ollama_NoApiKey_StillWorks()
    {
        var p = new OllamaProvider(new HttpClient(new FakeHandler()), MakeCfg("ollama"), NullLogger<OllamaProvider>.Instance);
        var r = await p.ChatAsync(new ChatRequest { Messages = new[] { new ChatMessage { Role = "user", Content = "hi" } } });
        Assert.Equal("hi", r.Content);
    }

    [Fact] public async Task LmStudio_NoApiKey_StillWorks()
    {
        var p = new LmStudioProvider(new HttpClient(new FakeHandler()), MakeCfg("lmstudio"), NullLogger<LmStudioProvider>.Instance);
        var r = await p.ChatAsync(new ChatRequest { Messages = new[] { new ChatMessage { Role = "user", Content = "hi" } } });
        Assert.Equal("hi", r.Content);
    }

    [Fact] public async Task DeepSeek_BuildRequest_AlwaysEnablesThinking()
    {
        var handler = new FakeHandler();
        var p = new DeepSeekProvider(new HttpClient(handler), MakeCfg("deepseek"), NullLogger<DeepSeekProvider>.Instance);
        await p.ChatAsync(new ChatRequest
        {
            Messages = new[] { new ChatMessage { Role = "user", Content = "hi" } }
        });
        Assert.NotNull(handler.LastRequestBody);
        Assert.Contains("\"thinking\"", handler.LastRequestBody!);
    }

    [Fact] public async Task DeepSeek_OmitsThinking_When_Disabled()
    {
        var handler = new FakeHandler();
        var p = new DeepSeekProvider(new HttpClient(handler), MakeCfg("deepseek"), NullLogger<DeepSeekProvider>.Instance);
        await p.ChatAsync(new ChatRequest
        {
            Messages = new[] { new ChatMessage { Role = "user", Content = "hi" } },
            EnableThinking = false
        });
        Assert.NotNull(handler.LastRequestBody);
        Assert.DoesNotContain("\"thinking\"", handler.LastRequestBody!);
    }

    [Fact]
    public void ProviderFactory_ResolveAndCache()
    {
        var cfg = new AIOptions
        {
            DefaultProviderId = "ollama",
            Providers = new() { MakeCfg("ollama"), MakeCfg("custom-test") }
        };
        var lf = NullLoggerFactory.Instance;
        var f = new ProviderFactory(cfg, lf);
        var a = f.Get("ollama");
        var b = f.Get("ollama");
        Assert.Same(a, b);
        var c = f.Get("custom-test");
        Assert.NotSame(a, c);
    }

    [Fact]
    public async Task StreamChatAsync_ParsesSSEChunks()
    {
        var sse = new StringBuilder();
        sse.Append("data: {\"id\":\"x\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"hello \"}}]}\n\n");
        sse.Append("data: {\"id\":\"x\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"world\"}}]}\n\n");
        sse.Append("data: [DONE]\n\n");
        var handler = new StreamHandler(sse.ToString());
        var p = new OllamaProvider(new HttpClient(handler), MakeCfg("ollama"), NullLogger<OllamaProvider>.Instance);
        var sb = new StringBuilder();
        await foreach (var chunk in p.StreamChatAsync(new ChatRequest { Messages = new[] { new ChatMessage { Role = "user", Content = "x" } } }))
        {
            if (chunk.DeltaContent is { Length: > 0 } c) sb.Append(c);
        }
        Assert.Contains("hello", sb.ToString());
        Assert.Contains("world", sb.ToString());
    }

    private sealed class StreamHandler : HttpMessageHandler
    {
        private readonly string _sse;
        public StreamHandler(string sse) { _sse = sse; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_sse, Encoding.UTF8, "text/event-stream")
            });
    }
}
