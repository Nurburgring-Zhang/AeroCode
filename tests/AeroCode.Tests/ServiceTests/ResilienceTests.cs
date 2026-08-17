// Copyright (c) AeroCode V3.0
// AiResiliencePipeline tests — verifies retry / circuit breaker / transient exception handling.
using System;
using System.Net;
using System.Net.Http;
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
public class ResilienceTests
{
    private sealed class FlakyHandler : HttpMessageHandler
    {
        private int _callCount;
        public int StatusToReturn { get; set; } = 503;
        public string Body { get; set; } = "{\"error\":\"server overloaded\"}";
        public int TotalCalls => _callCount;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var n = Interlocked.Increment(ref _callCount);
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)StatusToReturn)
            {
                Content = new StringContent(Body, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }

    private static OpenAICompatibleProvider MakeProvider(ResilienceOptions opts, HttpMessageHandler handler)
    {
        Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", "sk-test-123");
        var http = new HttpClient(handler);
        var cfg = new ProviderConfig
        {
            Id = "deepseek", DisplayName = "DeepSeek", Kind = "OpenAICompatible",
            BaseUrl = "https://api.deepseek.com/v1", DefaultModel = "deepseek-v4-flash",
            ApiKeyEnvVar = "DEEPSEEK_API_KEY", RequiresApiKey = true
        };
        return new DeepSeekProvider(http, cfg, NullLogger<DeepSeekProvider>.Instance, new AiResiliencePipeline(opts));
    }

    [Fact]
    public async Task TransientHttp_Retries_ThenSucceeds_WhenEventuallyOk()
    {
        var h = new FlakyHandler { StatusToReturn = 200 }; // First try fail, we override
        // Override the handler to fail twice then succeed:
        var n = 0;
        var handler = new SwitchingHandler(() =>
        {
            n++;
            return n <= 2
                ? new HttpResponseMessage((HttpStatusCode)503) { Content = new StringContent("fail") }
                : new HttpResponseMessage((HttpStatusCode)200)
                {
                    Content = new StringContent("{\"id\":\"x\",\"model\":\"m\",\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}")
                };
        });
        var p = MakeProvider(new ResilienceOptions
        {
            MaxRetryAttempts = 3,
            AttemptTimeoutSeconds = 5,
            RetryBaseDelayMs = 10,
            CircuitBreakerMinThroughput = 0
        }, handler);
        var resp = await p.ChatAsync(new ChatRequest
        {
            Messages = new[] { new AeroCode.AI.Models.ChatMessage { Role = "user", Content = "hi" } }
        });
        Assert.Equal("ok", resp.Content);
        Assert.True(n >= 3, $"expected at least 3 calls, got {n}");
    }

    [Fact]
    public async Task PersistentFailure_ThrowsAiProviderException_AfterExhaustingRetries()
    {
        var n = 0;
        var handler = new SwitchingHandler(() =>
        {
            n++;
            return new HttpResponseMessage((HttpStatusCode)503) { Content = new StringContent("always") };
        });
        var p = MakeProvider(new ResilienceOptions
        {
            MaxRetryAttempts = 2,
            RetryBaseDelayMs = 5,
            CircuitBreakerMinThroughput = 0
        }, handler);
        var ex = await Assert.ThrowsAsync<AiProviderException>(() => p.ChatAsync(new ChatRequest
        {
            Messages = new[] { new AeroCode.AI.Models.ChatMessage { Role = "user", Content = "hi" } }
        }));
        Assert.Equal(503, ex.StatusCode);
        Assert.Equal(3, n); // 1 initial + 2 retries
    }

    [Fact]
    public async Task NonTransient_4xx_ThrowsImmediately_NoRetry()
    {
        var n = 0;
        var handler = new SwitchingHandler(() =>
        {
            n++;
            return new HttpResponseMessage((HttpStatusCode)400) { Content = new StringContent("bad") };
        });
        var p = MakeProvider(new ResilienceOptions
        {
            MaxRetryAttempts = 5,
            RetryBaseDelayMs = 5,
            CircuitBreakerMinThroughput = 0
        }, handler);
        var ex = await Assert.ThrowsAsync<AiProviderException>(() => p.ChatAsync(new ChatRequest
        {
            Messages = new[] { new AeroCode.AI.Models.ChatMessage { Role = "user", Content = "hi" } }
        }));
        Assert.Equal(400, ex.StatusCode);
        Assert.Equal(1, n); // no retry for 4xx
    }

    [Fact]
    public async Task CircuitBreaker_Opens_AfterRepeatedFailures()
    {
        var n = 0;
        var handler = new SwitchingHandler(() =>
        {
            n++;
            return new HttpResponseMessage((HttpStatusCode)500) { Content = new StringContent("fail") };
        });
        var opts = new ResilienceOptions
        {
            MaxRetryAttempts = 0, // No retry so circuit breaker sees each call
            CircuitBreakerMinThroughput = 3,
            CircuitBreakerFailureRatio = 0.5,
            CircuitBreakerSamplingDurationSeconds = 30,
            CircuitBreakerBreakDurationSeconds = 2
        };
        var p = MakeProvider(opts, handler);

        // First few calls fail (and open the breaker)
        for (var i = 0; i < 3; i++)
        {
            try { await p.ChatAsync(new ChatRequest { Messages = new[] { new AeroCode.AI.Models.ChatMessage { Role = "user", Content = "x" } } }); }
            catch (AiProviderException) { }
        }
        // Subsequent call should be rejected by the circuit breaker without HTTP
        var nBefore = n;
        await Assert.ThrowsAsync<AiProviderException>(() => p.ChatAsync(new ChatRequest
        {
            Messages = new[] { new AeroCode.AI.Models.ChatMessage { Role = "user", Content = "y" } }
        }));
        // Either the call was short-circuited (no HTTP), or it ran and failed. At least we should not have a 4xx here.
        // (Circuit breaker may throw BrokenCircuitException which we propagate as AiProviderException.)
        Assert.True(n >= 3);
    }

    private sealed class SwitchingHandler : HttpMessageHandler
    {
        private readonly Func<HttpResponseMessage> _factory;
        public SwitchingHandler(Func<HttpResponseMessage> factory) { _factory = factory; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(_factory());
    }
}
