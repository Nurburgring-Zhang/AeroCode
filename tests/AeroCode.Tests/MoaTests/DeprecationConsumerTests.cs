// Copyright (c) AeroCode
// R3-δ DeprecationMonitor 生产消费方测试：Moa 网关执行路径（GatewayOrchestrationFacade）
// 的 MarkOnly 弃用检查——门控默认 false = 完全不调 monitor；启用后命中 deprecated →
// 结果遥测字段 + WARN（不拦截）；monitor 自身故障不阻塞执行路径。
using System.Net;
using AeroAgent.Moa.Gateway;
using AeroCode.AI.Capabilities;
using Xunit;

namespace AeroCode.Tests.MoaTests;

public sealed class DeprecationConsumerTests
{
    private const string ChangelogUrl = "http://aerocode.test/changelog";

    private static MoaGatewayClientOptions ClientOptions => new()
    {
        BaseUrl = new Uri("http://127.0.0.1:18931"),
        ApiKey = "facade-key",
        Timeout = TimeSpan.FromSeconds(5),
        HealthTimeout = TimeSpan.FromSeconds(2),
    };

    private static GatewayFakeHttpHandler GatewayUpHandler() => new((req, _) =>
        req.RequestUri!.AbsolutePath switch
        {
            "/health" => GatewayTestData.JsonResponse(GatewayTestData.HealthJson),
            "/v1/moa/execute" => GatewayTestData.JsonResponse(GatewayTestData.ExecuteEnvelope(mock: false), mockHeader: false),
            _ => GatewayTestData.JsonResponse("""{"detail":"Not Found"}""", HttpStatusCode.NotFound),
        });

    private sealed record MonitorFixture(GatewayFakeHttpHandler Handler, DeprecationMonitor Monitor)
    {
        public int RequestCount => Handler.Requests.Count;
    }

    private static MonitorFixture MakeMonitor(bool enabled, HttpStatusCode status = HttpStatusCode.OK, string body = "Model v1 is deprecated since 2026-01; migrate to v2.")
    {
        var handler = new GatewayFakeHttpHandler((_, _) => GatewayTestData.JsonResponse(body, status));
        var monitor = new DeprecationMonitor(
            enabled: enabled,
            urlAllowlist: new[] { ChangelogUrl },
            http: new HttpClient(handler));
        return new MonitorFixture(handler, monitor);
    }

    private static async Task<GatewayOrchestrationOutcome> ExecuteViaGatewayAsync(DeprecationMonitor? monitor)
    {
        using var handler = GatewayUpHandler();
        using var client = new MoaGatewayClient(ClientOptions, handler);
        var facade = new GatewayOrchestrationFacade(client, new ScriptedFallbackStrategy(), deprecationMonitor: monitor);
        return await facade.ExecuteAsync(
            context: null,
            gatewayRequest: new MoaGatewayExecuteRequest { Query = "写一个快速排序" });
    }

    [Fact]
    public async Task MonitorEnabled_ChangelogHit_TelemetrySet_ResultNotBlocked()
    {
        var fixture = MakeMonitor(enabled: true, body: "deprecated endpoint A; deprecated endpoint B; deprecat");

        var outcome = await ExecuteViaGatewayAsync(fixture.Monitor);

        // MarkOnly：网关结果原样呈现，绝不拦截。
        Assert.True(outcome.UsedGateway);
        Assert.False(outcome.Degraded);
        Assert.Equal("网关聚合出的最终答复", outcome.Content);

        // 命中 deprecated → 遥测字段（提及数 + 命中 URL）。
        Assert.NotNull(outcome.DeprecationMentions);
        Assert.True(outcome.DeprecationMentions >= 3);
        Assert.Contains(ChangelogUrl, outcome.DeprecationHitUrls);

        // monitor 真实外呼恰好一次（changelog GET）。
        Assert.Equal(1, fixture.RequestCount);
    }

    [Fact]
    public async Task MonitorEnabled_UrlWithQuery_TelemetryRedactedToHostPathOnly()
    {
        // R4 δ-3：命中 URL 的 query 可能含 key 参数——遥测字段只留 host+path（RedactUrl 同一口径）。
        const string fullUrl = "http://aerocode.test/changelog?token=secret-key-123";
        var handler = new GatewayFakeHttpHandler(
            (_, _) => GatewayTestData.JsonResponse("deprecated model v1", HttpStatusCode.OK));
        var monitor = new DeprecationMonitor(
            enabled: true, urlAllowlist: new[] { fullUrl }, http: new HttpClient(handler));

        var outcome = await ExecuteViaGatewayAsync(monitor);

        Assert.NotNull(outcome.DeprecationMentions);
        Assert.True(outcome.DeprecationMentions >= 1);
        var hit = Assert.Single(outcome.DeprecationHitUrls);
        Assert.Equal("http://aerocode.test/changelog", hit); // query 已剥离
        Assert.DoesNotContain("secret-key-123", hit, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MonitorGateOff_Default_CompletelySkipsMonitor()
    {
        // deprecation.monitor 默认 false = 现行为：完全不调 monitor（零外呼、零遥测）。
        var fixture = MakeMonitor(enabled: false);

        var outcome = await ExecuteViaGatewayAsync(fixture.Monitor);

        Assert.True(outcome.UsedGateway);
        Assert.Null(outcome.DeprecationMentions);
        Assert.Empty(outcome.DeprecationHitUrls);
        Assert.Equal(0, fixture.RequestCount); // monitor 一次请求都没发
    }

    [Fact]
    public async Task MonitorNotInjected_TelemetryNull_DefaultBehavior()
    {
        var outcome = await ExecuteViaGatewayAsync(monitor: null);

        Assert.True(outcome.UsedGateway);
        Assert.Equal("网关聚合出的最终答复", outcome.Content);
        Assert.Null(outcome.DeprecationMentions);
        Assert.Empty(outcome.DeprecationHitUrls);
    }

    [Fact]
    public async Task MonitorEnabled_ChangelogUnreachable_ZeroMentions_StillNotBlocking()
    {
        // changelog 不可达（HTTP 500）：如实 = 已检查、0 提及；结果不受影响。
        var fixture = MakeMonitor(enabled: true, status: HttpStatusCode.InternalServerError);

        var outcome = await ExecuteViaGatewayAsync(fixture.Monitor);

        Assert.True(outcome.UsedGateway);
        Assert.Equal("网关聚合出的最终答复", outcome.Content);
        Assert.Equal(0, outcome.DeprecationMentions);
        Assert.Empty(outcome.DeprecationHitUrls);
    }
}
