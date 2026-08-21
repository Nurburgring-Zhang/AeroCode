using System.Net;
using System.Text.Json;
using AeroAgent.Moa.Gateway;
using Xunit;

namespace AeroCode.Tests.MoaTests;

/// <summary>
/// <see cref="MoaGatewayClient"/> 契约测试：请求构造、响应解析、D6 mock 标注透传、
/// 401/502/超时/网络中断如实失败（绝不伪造成功）、健康探活。
/// 传输层为手写 <see cref="GatewayFakeHttpHandler"/>（真实 HttpMessageHandler 契约的测试替身），
/// 响应形状逐字段对齐 moa-gateway-pro v3.1.1 源码（routes/moa.py 的 MoAResult.to_dict()）。
/// </summary>
public sealed class MoaGatewayClientTests
{
    private static readonly MoaGatewayClientOptions DefaultOptions = new()
    {
        BaseUrl = new Uri("http://127.0.0.1:18910"),
        ApiKey = "test-key-abc",
        Timeout = TimeSpan.FromSeconds(5),
        HealthTimeout = TimeSpan.FromSeconds(2),
    };

    private static MoaGatewayExecuteRequest SimpleRequest(string query = "写一个快速排序") =>
        new() { Query = query };

    [Fact]
    public async Task ExecuteAsync_BuildsRealRequest_PostMethodAbsoluteUrlBearerAndMessages()
    {
        using var handler = new GatewayFakeHttpHandler((_, _) => GatewayTestData.JsonResponse(GatewayTestData.ExecuteEnvelope()));
        using var client = new MoaGatewayClient(DefaultOptions, handler);

        var result = await client.ExecuteAsync(SimpleRequest());

        Assert.True(result.IsSuccess, result.Error);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("http://127.0.0.1:18910/v1/moa/execute", request.Uri.ToString());
        Assert.Equal("Bearer test-key-abc", request.AuthorizationHeader);
        Assert.Equal("application/json", request.ContentType);

        using var doc = JsonDocument.Parse(request.Body!);
        var root = doc.RootElement;
        Assert.Equal("auto", root.GetProperty("model").GetString());
        var messages = root.GetProperty("messages");
        Assert.Equal(1, messages.GetArrayLength());
        Assert.Equal("user", messages[0].GetProperty("role").GetString());
        Assert.Equal("写一个快速排序", messages[0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task ExecuteAsync_ContextMessages_PrependedBeforeQueryInOriginalOrder()
    {
        using var handler = new GatewayFakeHttpHandler((_, _) => GatewayTestData.JsonResponse(GatewayTestData.ExecuteEnvelope()));
        using var client = new MoaGatewayClient(DefaultOptions, handler);

        await client.ExecuteAsync(new MoaGatewayExecuteRequest
        {
            Query = "继续",
            Context = new[]
            {
                new MoaGatewayChatMessage("system", "你是严谨的助手"),
                new MoaGatewayChatMessage("assistant", "上一轮答复"),
            },
        });

        var request = Assert.Single(handler.Requests);
        using var doc = JsonDocument.Parse(request.Body!);
        var messages = doc.RootElement.GetProperty("messages");
        Assert.Equal(3, messages.GetArrayLength());
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("你是严谨的助手", messages[0].GetProperty("content").GetString());
        Assert.Equal("assistant", messages[1].GetProperty("role").GetString());
        Assert.Equal("上一轮答复", messages[1].GetProperty("content").GetString());
        // 本轮查询必须是最后一条 user 消息（网关 routes/moa.py 以 messages[-1] 为 query）
        Assert.Equal("user", messages[2].GetProperty("role").GetString());
        Assert.Equal("继续", messages[2].GetProperty("content").GetString());
    }

    [Fact]
    public async Task ExecuteAsync_MoaExtensionFields_SerializedWhenSet()
    {
        using var handler = new GatewayFakeHttpHandler((_, _) => GatewayTestData.JsonResponse(GatewayTestData.ExecuteEnvelope()));
        using var client = new MoaGatewayClient(DefaultOptions, handler);

        await client.ExecuteAsync(new MoaGatewayExecuteRequest
        {
            Query = "q",
            Preset = "quality",
            Strategy = "judge",
            ReferenceCount = 4,
            CriticRounds = 2,
            Temperature = 0.25,
            MaxTokens = 1024,
        });

        var request = Assert.Single(handler.Requests);
        using var doc = JsonDocument.Parse(request.Body!);
        var root = doc.RootElement;
        Assert.Equal("quality", root.GetProperty("preset").GetString());
        Assert.Equal("judge", root.GetProperty("strategy").GetString());
        Assert.Equal(4, root.GetProperty("reference_count").GetInt32());
        Assert.Equal(2, root.GetProperty("critic_rounds").GetInt32());
        Assert.Equal(0.25, root.GetProperty("temperature").GetDouble());
        Assert.Equal(1024, root.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public async Task ExecuteAsync_NullOptionalFields_OmittedFromWireBody()
    {
        using var handler = new GatewayFakeHttpHandler((_, _) => GatewayTestData.JsonResponse(GatewayTestData.ExecuteEnvelope()));
        using var client = new MoaGatewayClient(DefaultOptions, handler);

        await client.ExecuteAsync(SimpleRequest("q"));

        var request = Assert.Single(handler.Requests);
        using var doc = JsonDocument.Parse(request.Body!);
        var root = doc.RootElement;
        Assert.False(root.TryGetProperty("preset", out _));
        Assert.False(root.TryGetProperty("strategy", out _));
        Assert.False(root.TryGetProperty("reference_count", out _));
        Assert.False(root.TryGetProperty("critic_rounds", out _));
        Assert.False(root.TryGetProperty("temperature", out _));
        Assert.False(root.TryGetProperty("max_tokens", out _));
        // 必填字段仍在
        Assert.True(root.TryGetProperty("model", out _));
        Assert.True(root.TryGetProperty("messages", out _));
    }

    [Fact]
    public async Task ExecuteAsync_ParsesFullEnvelope_AllContractFields()
    {
        using var handler = new GatewayFakeHttpHandler((_, _) => GatewayTestData.JsonResponse(GatewayTestData.ExecuteEnvelope()));
        using var client = new MoaGatewayClient(DefaultOptions, handler);

        var result = await client.ExecuteAsync(SimpleRequest());

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(200, result.StatusCode);
        var value = result.Value!;
        Assert.Equal("req-0001", value.RequestId);
        Assert.Equal("写一个快速排序", value.Query);
        Assert.Equal("fast", value.Preset);
        Assert.Equal("compose", value.Strategy);
        Assert.Equal("网关聚合出的最终答复", value.FinalContent);
        Assert.Equal("mock/aggregator-mock", value.AggregatorModel);
        Assert.Null(value.WinnerModel);
        Assert.Equal(0.87, value.ConsensusScore, precision: 5);
        Assert.Equal(1, value.Iterations);
        Assert.Equal(42.1, value.TotalLatencyMs, precision: 5);
        Assert.Equal(0.000123, value.TotalCost, precision: 9);
        Assert.False(value.FallbackUsed);
        Assert.True(value.Mock);

        Assert.Equal(2, value.References.Count);
        Assert.Equal("mock/qwen3-mock", value.References[0].ModelId);
        Assert.True(value.References[0].Success);
        Assert.Equal(42, value.References[0].Tokens);
        Assert.Equal("参考 A", value.References[0].Preview);
        Assert.False(value.References[1].Success); // 逐模型失败如实呈现

        var critic = Assert.Single(value.Critics);
        Assert.Equal("mock/critic-mock", critic.ModelId);
        Assert.Equal(2, critic.IssuesCount);
        Assert.Equal(1, critic.SuggestionsCount);

        var step = Assert.Single(value.ChainSteps);
        Assert.Equal(1, step.Step);
        Assert.Equal("compose", step.Strategy);
        Assert.Equal("step-1", step.Preview);

        // 无响应头时，信封 mock=true 单独就足以让 IsMock 为真（D6 双通道）
        Assert.True(result.IsMock);
    }

    [Fact]
    public async Task ExecuteAsync_MockHeaderTrue_SetsIsMock_EvenWhenBodyMockFalse()
    {
        using var handler = new GatewayFakeHttpHandler(
            (_, _) => GatewayTestData.JsonResponse(GatewayTestData.ExecuteEnvelope(mock: false), mockHeader: true));
        using var client = new MoaGatewayClient(DefaultOptions, handler);

        var result = await client.ExecuteAsync(SimpleRequest());

        Assert.True(result.IsSuccess, result.Error);
        Assert.False(result.Value!.Mock); // 信封字段如实为 false
        Assert.True(result.IsMock);      // X-MOA-Mock 头通道命中
    }

    [Fact]
    public async Task ExecuteAsync_NoMockSignals_IsMockFalse()
    {
        using var handler = new GatewayFakeHttpHandler(
            (_, _) => GatewayTestData.JsonResponse(GatewayTestData.ExecuteEnvelope(mock: false), mockHeader: false));
        using var client = new MoaGatewayClient(DefaultOptions, handler);

        var result = await client.ExecuteAsync(SimpleRequest());

        Assert.True(result.IsSuccess, result.Error);
        Assert.False(result.IsMock);
        Assert.False(result.Value!.Mock);
    }

    [Fact]
    public async Task ExecuteAsync_Unauthorized401_FailsHonestly_WithDetail()
    {
        using var handler = new GatewayFakeHttpHandler(
            (_, _) => GatewayTestData.JsonResponse(
                """{"detail":"Invalid or missing API key. Use Authorization: Bearer <key>"}""",
                HttpStatusCode.Unauthorized));
        using var client = new MoaGatewayClient(DefaultOptions, handler);

        var result = await client.ExecuteAsync(SimpleRequest());

        Assert.False(result.IsSuccess);
        Assert.Equal(401, result.StatusCode);
        Assert.Null(result.Value);
        Assert.False(result.IsTimeout);
        Assert.Contains("401", result.Error);
        Assert.Contains("Invalid or missing API key", result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_BadGateway502_FailsHonestly_WithProviderEvidence()
    {
        using var handler = new GatewayFakeHttpHandler(
            (_, _) => GatewayTestData.JsonResponse(
                """{"detail":"MoA execution failed: all reference models failed"}""",
                HttpStatusCode.BadGateway));
        using var client = new MoaGatewayClient(DefaultOptions, handler);

        var result = await client.ExecuteAsync(SimpleRequest());

        Assert.False(result.IsSuccess);
        Assert.Equal(502, result.StatusCode);
        Assert.Null(result.Value);
        Assert.Contains("502", result.Error);
        Assert.Contains("all reference models failed", result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_TimedOut_ReturnsTimeoutFailure_NotSuccess()
    {
        var options = DefaultOptions with { Timeout = TimeSpan.FromMilliseconds(150) };
        using var handler = new GatewayFakeHttpHandler(
            async (_, _, ct) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(10), ct); // 远超客户端超时
                return GatewayTestData.JsonResponse(GatewayTestData.ExecuteEnvelope());
            });
        using var client = new MoaGatewayClient(options, handler);

        var result = await client.ExecuteAsync(SimpleRequest());

        Assert.False(result.IsSuccess);
        Assert.True(result.IsTimeout);
        Assert.Null(result.StatusCode); // 没收到任何响应
        Assert.Null(result.Value);
        Assert.Contains("timed out", result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_NetworkUnreachable_FailsHonestly()
    {
        using var handler = new GatewayFakeHttpHandler(
            (_, _) => throw new HttpRequestException("connection refused"));
        using var client = new MoaGatewayClient(DefaultOptions, handler);

        var result = await client.ExecuteAsync(SimpleRequest());

        Assert.False(result.IsSuccess);
        Assert.Null(result.StatusCode);
        Assert.False(result.IsTimeout);
        Assert.Contains("unreachable", result.Error);
        Assert.Contains("connection refused", result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_EmptyQuery_FailsWithoutAnyHttpCall()
    {
        using var handler = new GatewayFakeHttpHandler(
            (_, _) => throw new InvalidOperationException("transport must not be touched"));
        using var client = new MoaGatewayClient(DefaultOptions, handler);

        var result = await client.ExecuteAsync(new MoaGatewayExecuteRequest { Query = "   " });

        Assert.False(result.IsSuccess);
        Assert.Contains("Query", result.Error);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ExecuteAsync_ResponseNotJson_FailsHonestly()
    {
        using var handler = new GatewayFakeHttpHandler(
            (_, _) => GatewayTestData.JsonResponse("<html>proxy error</html>")); // 200 但非 JSON
        using var client = new MoaGatewayClient(DefaultOptions, handler);

        var result = await client.ExecuteAsync(SimpleRequest());

        Assert.False(result.IsSuccess);
        Assert.Equal(200, result.StatusCode);
        Assert.Null(result.Value);
        Assert.Contains("not valid JSON", result.Error);
    }

    [Fact]
    public async Task HealthAsync_ParsesRealEnvelope_NoAuthHeaderWhenNoKey()
    {
        using var handler = new GatewayFakeHttpHandler((_, _) => GatewayTestData.JsonResponse(GatewayTestData.HealthJson));
        using var client = new MoaGatewayClient(DefaultOptions with { ApiKey = null }, handler);

        var result = await client.HealthAsync();

        Assert.True(result.IsSuccess, result.Error);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("http://127.0.0.1:18910/health", request.Uri.ToString());
        Assert.Null(request.AuthorizationHeader); // 无 key 时不伪造鉴权头

        var health = result.Value!;
        Assert.Equal("ok", health.Status);
        Assert.Equal("3.1.1", health.Version);
        Assert.Equal(3, health.EndpointsTotal);
        Assert.Equal(3, health.EndpointsEnabled);
        Assert.Equal(3, health.EndpointsHealthy);
        Assert.Equal(3, health.MockEndpointsCount); // D6：mock 端点数量显式可见
        Assert.Equal(0, health.RealEndpointsCount);
        Assert.Equal("auto", health.MockMode);
    }

    [Fact]
    public async Task IsReadyAsync_TrueOn200_FalseOn503()
    {
        var ready = true;
        using var handler = new GatewayFakeHttpHandler((req, _) =>
        {
            Assert.Equal("/health/ready", req.RequestUri!.AbsolutePath);
            return ready
                ? GatewayTestData.JsonResponse("""{"status":"ready"}""")
                : GatewayTestData.JsonResponse("""{"status":"not_ready"}""", HttpStatusCode.ServiceUnavailable);
        });
        using var client = new MoaGatewayClient(DefaultOptions, handler);

        Assert.True(await client.IsReadyAsync());
        ready = false;
        Assert.False(await client.IsReadyAsync());
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task GetPresetsAsync_ParsesRealGatewayShape_FlatFields()
    {
        using var handler = new GatewayFakeHttpHandler((_, _) => GatewayTestData.JsonResponse(GatewayTestData.PresetsJson));
        using var client = new MoaGatewayClient(DefaultOptions, handler);

        var result = await client.GetPresetsAsync();

        Assert.True(result.IsSuccess, result.Error);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("/v1/moa/presets", request.Uri.AbsolutePath);
        Assert.Equal("Bearer test-key-abc", request.AuthorizationHeader);

        Assert.Equal("balanced", result.Value!.Default);
        Assert.Equal(2, result.Value.Presets.Count);
        Assert.Equal("fast", result.Value.Presets[0].Name);
        Assert.Equal("balanced", result.Value.Presets[1].Name);
        // 真实网关返回扁平字段（strategy/reference_count/…），没有嵌套 "config" 对象——
        // 因此 MoaPresetInfo.Config 对 v3.1.1 恒为 null（契约差异已记录在案）。
        Assert.Null(result.Value.Presets[0].Config);
    }

    [Fact]
    public async Task GetReferencesAsync_ProjectsEnvelopeReferences_KeepsMockAnnotation()
    {
        using var handler = new GatewayFakeHttpHandler(
            (_, _) => GatewayTestData.JsonResponse(GatewayTestData.ExecuteEnvelope(), mockHeader: true));
        using var client = new MoaGatewayClient(DefaultOptions, handler);

        var result = await client.GetReferencesAsync(SimpleRequest());

        Assert.True(result.IsSuccess, result.Error);
        Assert.True(result.IsMock);
        Assert.Equal(2, result.Value!.Count);
        Assert.Equal("mock/qwen3-mock", result.Value[0].ModelId);
        Assert.False(result.Value[1].Success);
    }

    [Fact]
    public async Task GetCriticsAsync_ProjectsEnvelopeCritics()
    {
        using var handler = new GatewayFakeHttpHandler(
            (_, _) => GatewayTestData.JsonResponse(GatewayTestData.ExecuteEnvelope(mock: false)));
        using var client = new MoaGatewayClient(DefaultOptions, handler);

        var result = await client.GetCriticsAsync(SimpleRequest());

        Assert.True(result.IsSuccess, result.Error);
        Assert.False(result.IsMock);
        var critic = Assert.Single(result.Value!);
        Assert.Equal("mock/critic-mock", critic.ModelId);
        Assert.Equal(2, critic.IssuesCount);
    }

    [Fact]
    public async Task CallerCancellation_PropagatesAsOperationCanceledException_NotSwallowed()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        using var handler = new GatewayFakeHttpHandler(
            async (_, _, ct) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                return GatewayTestData.JsonResponse(GatewayTestData.ExecuteEnvelope());
            });
        using var client = new MoaGatewayClient(DefaultOptions, handler);

        // 调用方取消必须如实上抛（区别于"网关超时"的失败结果）
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.ExecuteAsync(SimpleRequest(), cts.Token));
    }
}
