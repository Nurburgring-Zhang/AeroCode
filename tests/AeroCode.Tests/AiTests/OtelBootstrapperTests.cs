// Copyright (c) AeroCode V3.2
// OtelBootstrapper + MeaiAdapters tests — smoke tests verifying real OTel SDK + MEAI calls.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AeroCode.AI.Capabilities;
using AeroCode.AI.Integration;
using AeroCode.AI.Models;
using AeroCode.AI.Providers;
using AeroCode.AI.Telemetry;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AeroCode.Tests.AiTests;

public class OtelBootstrapperTests
{
    [Fact]
    public void Otel_Real_Bootstrap_BuildsProviders()
    {
        using var otel = new OtelBootstrapper(new OtelOptions
        {
            ServiceName = "AeroCode.Tests",
            EnableConsoleExporter = false, // don't spam test output
            EnableHttpClientInstrumentation = true,
            EnableRuntimeInstrumentation = true
        });
        Assert.NotNull(otel.TracerProvider);
        Assert.NotNull(otel.MeterProvider);
        Assert.NotNull(otel.ActivitySource);
        Assert.NotNull(otel.Meter);
        Assert.NotNull(otel.Metrics);
        Assert.NotNull(otel.LoggerFactory);
        Assert.Equal("AeroCode", OtelBootstrapper.MeterName);
        Assert.Equal("AeroCode.Harness", OtelBootstrapper.ActivitySourceName);
    }

    [Fact]
    public void Otel_Metrics_RealCounters_Exposed()
    {
        using var otel = new OtelBootstrapper(new OtelOptions { EnableConsoleExporter = false });
        Assert.NotNull(otel.Metrics.ChatRequests);
        Assert.NotNull(otel.Metrics.ChatErrors);
        Assert.NotNull(otel.Metrics.ChatLatencyMs);
        Assert.NotNull(otel.Metrics.EmbeddingRequests);
        Assert.NotNull(otel.Metrics.CacheHits);
        // Real records (these would be picked up by the OTel exporter if any were attached).
        otel.Metrics.ChatRequests.Add(1);
        otel.Metrics.CacheHits.Add(1);
    }

    [Fact]
    public void Otel_StartActivity_CreatesRealActivity()
    {
        using var otel = new OtelBootstrapper(new OtelOptions { EnableConsoleExporter = false });
        using var act = otel.StartActivity("test-op");
        Assert.NotNull(act);
        Assert.Equal("test-op", act!.OperationName);
    }

    [Fact]
    public void Otel_LoggerFactory_CreatesRealLogger()
    {
        using var otel = new OtelBootstrapper(new OtelOptions { EnableConsoleExporter = false });
        var logger = otel.LoggerFactory.CreateLogger("AeroCode.Tests.Logger");
        Assert.NotNull(logger);
        logger.LogInformation("test message from OtelBootstrapper test");
    }
}

/// <summary>Fake provider that returns deterministic text. Used for MEAI adapter tests.</summary>
internal sealed class FakeProvider : IAiProvider
{
    public string ProviderId => "fake";
    public string DisplayName => "Fake";
    public ProviderKind Kind => ProviderKind.OpenAICompatible;
    public bool SupportsStreaming => true;
    public bool SupportsToolCalling => false;
    public bool SupportsThinking => false;
    public Task<AeroCode.AI.Models.ChatResponse> ChatAsync(AeroCode.AI.Models.ChatRequest request, CancellationToken ct = default)
        => Task.FromResult(new AeroCode.AI.Models.ChatResponse { Content = $"echo: {request.Messages.LastOrDefault()?.Content}" });
    public IAsyncEnumerable<AeroCode.AI.Models.ChatChunk> StreamChatAsync(AeroCode.AI.Models.ChatRequest request, CancellationToken ct = default)
        => AsyncEnumerableFromChunks(new[] { "echo: ", request.Messages.LastOrDefault()?.Content ?? "" });
    public Task<bool> HealthCheckAsync(CancellationToken ct = default) => Task.FromResult(true);
    private static async IAsyncEnumerable<ChatChunk> AsyncEnumerableFromChunks(IEnumerable<string> chunks)
    {
        foreach (var c in chunks)
        {
            await Task.Yield();
            yield return new ChatChunk { DeltaContent = c };
        }
    }
}

public class MeaiAdaptersTests
{
    [Fact]
    public async Task Meai_Real_Adapter_TranslatesMessages_AndReturnsResponse()
    {
        var provider = new FakeProvider();
        var client = new MeaiChatClient(provider);
        var messages = new List<Microsoft.Extensions.AI.ChatMessage>
        {
            new(ChatRole.System, "You are a helper."),
            new(ChatRole.User, "hello world")
        };
        var resp = await client.GetResponseAsync(messages);
        Assert.NotNull(resp);
        Assert.Contains("echo: hello world", resp.Text);
    }

    [Fact]
    public async Task Meai_Real_Adapter_Streaming_YieldsTextChunks()
    {
        var provider = new FakeProvider();
        var client = new MeaiChatClient(provider);
        var messages = new List<Microsoft.Extensions.AI.ChatMessage>
        {
            new(ChatRole.User, "streaming test")
        };
        var collected = new List<string>();
        await foreach (var u in client.GetStreamingResponseAsync(messages))
        {
            var t = u.Contents.OfType<TextContent>().FirstOrDefault()?.Text;
            if (!string.IsNullOrEmpty(t)) collected.Add(t);
        }
        Assert.Contains("echo: ", collected);
        Assert.Contains("streaming test", collected);
    }

    [Fact]
    public void Meai_AsMeaiChatClient_Extension_Wraps()
    {
        var provider = new FakeProvider();
        IChatClient c = provider.AsMeaiChatClient();
        Assert.IsType<MeaiChatClient>(c);
    }

    [Fact]
    public void Meai_GetService_Returns_Provider_For_TypeIAiProvider()
    {
        var provider = new FakeProvider();
        var c = new MeaiChatClient(provider);
        var got = c.GetService(typeof(IAiProvider));
        Assert.Same(provider, got);
    }
}
