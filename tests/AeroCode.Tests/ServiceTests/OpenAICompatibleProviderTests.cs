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
public class OpenAICompatibleProviderTests
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        public string ResponseBody { get; set; } = string.Empty;
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public string? LastRequestBody { get; private set; }
        public System.Collections.Generic.Dictionary<string, string>? LastRequestHeaders { get; private set; }
        public string? LastRequestUrl { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequestUrl = request.RequestUri?.ToString();
            LastRequestHeaders = new System.Collections.Generic.Dictionary<string, string>();
            foreach (var h in request.Headers) LastRequestHeaders[h.Key] = string.Join(",", h.Value);
            if (request.Content is not null) LastRequestBody = await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(Status)
            {
                Content = new StringContent(ResponseBody, Encoding.UTF8, "application/json")
            };
        }
    }

    private static OpenAICompatibleProvider MakeProvider(FakeHandler handler, ProviderConfig? cfg = null)
    {
        var http = new HttpClient(handler);
        var c = cfg ?? new ProviderConfig
        {
            Id = "deepseek",
            DisplayName = "DeepSeek",
            Kind = "OpenAICompatible",
            BaseUrl = "https://api.deepseek.com/v1",
            DefaultModel = "deepseek-v4-flash",
            ApiKeyEnvVar = "DEEPSEEK_API_KEY",
            RequiresApiKey = true
        };
        return new DeepSeekProvider(http, c, NullLogger<DeepSeekProvider>.Instance);
    }

    [Fact]
    public async Task ChatAsync_ParsesContentAndReasoning()
    {
        Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", "sk-test-123");
        var handler = new FakeHandler
        {
            ResponseBody = "{\"id\":\"chatcmpl-1\",\"model\":\"deepseek-v4-flash\",\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"hi\",\"reasoning_content\":\"greet\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":5,\"total_tokens\":15,\"prompt_cache_hit_tokens\":0}}"
        };
        var provider = MakeProvider(handler);
        var resp = await provider.ChatAsync(new ChatRequest
        {
            Model = "deepseek-v4-flash",
            Messages = new[] { new ChatMessage { Role = "user", Content = "hi" } }
        });
        Assert.Equal("hi", resp.Content);
        Assert.Equal("greet", resp.ReasoningContent);
        Assert.Equal("stop", resp.FinishReason);
        Assert.NotNull(resp.Usage);
        Assert.Equal(15, resp.Usage!.TotalTokens);
        Assert.Equal("https://api.deepseek.com/v1/chat/completions", handler.LastRequestUrl);
    }

    [Fact]
    public async Task ChatAsync_SendsBearerAuth()
    {
        // Use a unique env value to avoid cross-test interference.
        var key = "sk-test-" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", key);
        var handler = new FakeHandler
        {
            ResponseBody = "{\"id\":\"x\",\"model\":\"m\",\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}"
        };
        var provider = MakeProvider(handler);
        await provider.ChatAsync(new ChatRequest
        {
            Messages = new[] { new ChatMessage { Role = "user", Content = "hi" } }
        });
        Assert.NotNull(handler.LastRequestHeaders);
        Assert.True(handler.LastRequestHeaders!.ContainsKey("Authorization"));
        Assert.Equal("Bearer " + key, handler.LastRequestHeaders["Authorization"]);
    }

    [Fact]
    public async Task ChatAsync_IncludesThinkingFields()
    {
        Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", "sk-test-123");
        var handler = new FakeHandler
        {
            ResponseBody = "{\"id\":\"x\",\"model\":\"m\",\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}"
        };
        var provider = MakeProvider(handler);
        await provider.ChatAsync(new ChatRequest
        {
            Messages = new[] { new ChatMessage { Role = "user", Content = "hi" } },
            EnableThinking = true, ThinkingEffort = "high"
        });
        Assert.NotNull(handler.LastRequestBody);
        Assert.Contains("\"thinking\"", handler.LastRequestBody!);
        Assert.Contains("\"enabled\"", handler.LastRequestBody!);
        Assert.Contains("\"reasoning_effort\"", handler.LastRequestBody!);
        Assert.Contains("\"high\"", handler.LastRequestBody!);
    }

    [Fact]
    public async Task ChatAsync_4xx_ThrowsAiProviderException()
    {
        Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", "sk-test-123");
        var handler = new FakeHandler { Status = HttpStatusCode.BadRequest, ResponseBody = "{\"error\":\"bad\"}" };
        var provider = MakeProvider(handler);
        await Assert.ThrowsAsync<AiProviderException>(async () => await provider.ChatAsync(new ChatRequest
        {
            Messages = new[] { new ChatMessage { Role = "user", Content = "hi" } }
        }));
    }

    [Fact]
    public async Task StreamChatAsync_ParsesSSEChunks()
    {
        Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", "sk-test-123");
        var handler = new FakeHandler
        {
            ResponseBody = "data: {\"id\":\"1\",\"choices\":[{\"delta\":{\"content\":\"a\"},\"index\":0}]}\n\n" +
                          "data: {\"id\":\"1\",\"choices\":[{\"delta\":{\"content\":\"b\"},\"index\":0}]}\n\n" +
                          "data: [DONE]\n\n"
        };
        var provider = MakeProvider(handler);
        var chunks = new System.Collections.Generic.List<string?>();
        await foreach (var c in provider.StreamChatAsync(new ChatRequest
        {
            Stream = true,
            Messages = new[] { new ChatMessage { Role = "user", Content = "hi" } }
        }))
        {
            if (c.DeltaContent is not null) chunks.Add(c.DeltaContent);
        }
        Assert.Equal(new[] { "a", "b" }, chunks);
    }

    [Fact]
    public void ProviderFactory_ReadsDefaultProviderId()
    {
        var opts = new AIOptions
        {
            DefaultProviderId = "deepseek",
            DefaultModel = "deepseek-v4-flash",
            Providers = new()
            {
                new() { Id = "deepseek", DisplayName = "DeepSeek", Kind = "OpenAICompatible",
                    BaseUrl = "https://api.deepseek.com/v1", DefaultModel = "deepseek-v4-flash", ApiKeyEnvVar = "DEEPSEEK_API_KEY" }
            }
        };
        var f = new ProviderFactory(opts, NullLoggerFactory.Instance);
        Assert.Contains("deepseek", f.ListConfiguredIds());
    }

    [Fact]
    public async Task VisionEnabled_SerializesImageAsContentParts()
    {
        Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", "sk-test-123");
        var handler = new FakeHandler
        {
            ResponseBody = "{\"id\":\"1\",\"model\":\"m\",\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}"
        };
        var cfg = new ProviderConfig
        {
            Id = "deepseek", DisplayName = "DeepSeek", Kind = "OpenAICompatible",
            BaseUrl = "https://api.deepseek.com/v1", DefaultModel = "deepseek-v4-flash",
            ApiKeyEnvVar = "DEEPSEEK_API_KEY", RequiresApiKey = true,
            SupportsVision = true,
        };
        var provider = MakeProvider(handler, cfg);

        await provider.ChatAsync(new ChatRequest
        {
            Model = "deepseek-v4-flash",
            Messages = new[]
            {
                new ChatMessage
                {
                    Role = "user",
                    Content = "describe this image",
                    Images = new[] { new ImageContent { Mime = "image/png", DataBase64 = "QUJD" } },
                },
            },
        });

        var body = handler.LastRequestBody ?? string.Empty;
        // content 应为 parts 数组：含 text 与 image_url（data URL 内联 base64）。
        Assert.Contains("\"image_url\"", body);
        Assert.Contains("data:image/png;base64,QUJD", body);
        Assert.Contains("\"type\":\"text\"", body);
        Assert.Contains("describe this image", body);
    }

    [Fact]
    public async Task VisionDisabled_DoesNotInjectImage()
    {
        Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", "sk-test-123");
        var handler = new FakeHandler
        {
            ResponseBody = "{\"id\":\"1\",\"model\":\"m\",\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}"
        };
        // 默认 SupportsVision=false。
        var provider = MakeProvider(handler);

        await provider.ChatAsync(new ChatRequest
        {
            Model = "deepseek-v4-flash",
            Messages = new[]
            {
                new ChatMessage
                {
                    Role = "user",
                    Content = "hi",
                    Images = new[] { new ImageContent { Mime = "image/png", DataBase64 = "QUJD" } },
                },
            },
        });

        var body = handler.LastRequestBody ?? string.Empty;
        // 未启用 vision：content 保持纯文本，绝不出现 image_url（现行为不受影响）。
        Assert.DoesNotContain("\"image_url\"", body);
        Assert.Contains("\"content\":\"hi\"", body);
    }
}
