// Copyright (c) AeroCode
// R3 修复（F-HIGH-1 / S-MED-5）：DeprecationMonitor 生产可达消费点测试。
// 挂点 = ExpertsStrategy（生产专家团编排直连 MoaGatewayClient 的真实 execute 路径）。
// 语义钉死：
// - monitor 未注入 / IsEnabled=false（默认）→ 零外呼零开销（fake handler 计数 0）；
// - monitor=true（双真）→ execute 成功后调 CheckDeprecationsMarkOnlyAsync：
//   命中 deprecated → WARN 可观测 + 结果不拦截；绝不阻塞执行路径；
// - S-MED-5 外呼总闸：enabled=false + monitor=true → 组合根映射口径（enabled && monitor）
//   → 不外呼（0 请求）；enabled=true + monitor=false 同样 0；双真才 1。
using System.Net;
using AeroAgent.Conversation.Models;
using AeroAgent.Conversation.Orchestration;
using AeroAgent.Moa.Gateway;
using AeroAgent.Moa.Strategies;
using AeroCode.AI.Capabilities;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AeroCode.Tests.MoaTests;

public sealed class ExpertsStrategyDeprecationTests : MoaTestBase
{
    private const string ChangelogUrl = "http://aerocode.test/changelog";

    private static MoaGatewayClientOptions ClientOptions => new()
    {
        BaseUrl = new Uri("http://127.0.0.1:18932"),
        ApiKey = "experts-key",
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

    private static MonitorFixture MakeMonitor(bool enabled, HttpStatusCode status = HttpStatusCode.OK,
        string body = "Model v1 is deprecated since 2026-01; migrate to v2.")
    {
        var handler = new GatewayFakeHttpHandler((_, _) => GatewayTestData.JsonResponse(body, status));
        var monitor = new DeprecationMonitor(
            enabled: enabled,
            urlAllowlist: new[] { ChangelogUrl },
            http: new HttpClient(handler));
        return new MonitorFixture(handler, monitor);
    }

    /// <summary>组合根映射口径（R3 修复 S-MED-5）：双真才构造启用实例，否则 null。</summary>
    private static DeprecationMonitor? CompositionRootMonitor(bool enabled, bool monitor, GatewayFakeHttpHandler handler) =>
        enabled && monitor
            ? new DeprecationMonitor(enabled: true, urlAllowlist: new[] { ChangelogUrl }, http: new HttpClient(handler))
            : null;

    private async Task<OrchestrationContext> NewContextAsync()
    {
        var session = await NewSessionAsync(OrchestrationStrategy.Experts);
        var userMessage = new ChatMessage
        {
            SessionId = session.Id,
            Role = ChatRole.User,
            Content = "写一个快速排序",
        };
        var appended = await Sessions.AppendMessageAsync(userMessage);
        Assert.True(appended.IsSuccess);
        return new OrchestrationContext
        {
            Session = session,
            History = new[] { userMessage },
            UserMessageId = userMessage.Id,
            Providers = Registry,
        };
    }

    [Fact]
    public async Task MonitorEnabled_ChangelogHit_CheckCalled_WarnLogged_ResultNotBlocked()
    {
        var fixture = MakeMonitor(enabled: true, body: "deprecated endpoint A; deprecated endpoint B; deprecat");
        var logger = new CapturingLogger<ExpertsStrategy>();
        using var handler = GatewayUpHandler();
        using var client = new MoaGatewayClient(ClientOptions, handler);
        var strategy = new ExpertsStrategy(client, Sessions, logger, fixture.Monitor);
        var context = await NewContextAsync();

        var events = await CollectAsync(strategy.ExecuteAsync(context));

        // MarkOnly：编排结果原样完成，绝不拦截。
        Assert.Contains(events, e => e is MessageCompletedEvent);
        Assert.DoesNotContain(events, e => e is MessageFailedEvent);

        // monitor 真实外呼恰好一次（changelog GET）——生产挂点确实消费了 monitor。
        Assert.Equal(1, fixture.RequestCount);

        // 命中 deprecated → WARN 可观测（不拦截语义随文标注）。
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning);
        var warn = logger.Entries.First(e => e.Level == LogLevel.Warning);
        Assert.Contains("[DeprecationMonitor]", warn.Message, StringComparison.Ordinal);
        Assert.Contains("MarkOnly", warn.Message, StringComparison.Ordinal);
        // URL 脱敏：只保留 host+path（query 可能含 key）。
        Assert.Contains("http://aerocode.test/changelog", warn.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MonitorGateOff_ZeroOutboundCalls_DefaultBehavior()
    {
        // IsEnabled=false（默认态）→ 一次 CheckAsync 都不调：fake handler 计数 0。
        var fixture = MakeMonitor(enabled: false);
        var logger = new CapturingLogger<ExpertsStrategy>();
        using var handler = GatewayUpHandler();
        using var client = new MoaGatewayClient(ClientOptions, handler);
        var strategy = new ExpertsStrategy(client, Sessions, logger, fixture.Monitor);
        var context = await NewContextAsync();

        var events = await CollectAsync(strategy.ExecuteAsync(context));

        Assert.Contains(events, e => e is MessageCompletedEvent);
        Assert.Equal(0, fixture.RequestCount); // monitor 零外呼
        Assert.DoesNotContain(logger.Entries, e =>
            e.Level == LogLevel.Warning && e.Message.Contains("[DeprecationMonitor]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MonitorNotInjected_ZeroOutboundCalls_DefaultBehavior()
    {
        var logger = new CapturingLogger<ExpertsStrategy>();
        using var handler = GatewayUpHandler();
        using var client = new MoaGatewayClient(ClientOptions, handler);
        var strategy = new ExpertsStrategy(client, Sessions, logger, deprecationMonitor: null);
        var context = await NewContextAsync();

        var events = await CollectAsync(strategy.ExecuteAsync(context));

        Assert.Contains(events, e => e is MessageCompletedEvent);
        // 网关自身请求照常（health + execute），无任何额外外呼面。
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task MonitorEnabled_ChangelogUnreachable_ZeroMentions_StillNotBlocking()
    {
        // changelog 不可达（HTTP 500）：如实 = 已检查、0 提及；编排结果不受影响。
        var fixture = MakeMonitor(enabled: true, status: HttpStatusCode.InternalServerError);
        var logger = new CapturingLogger<ExpertsStrategy>();
        using var handler = GatewayUpHandler();
        using var client = new MoaGatewayClient(ClientOptions, handler);
        var strategy = new ExpertsStrategy(client, Sessions, logger, fixture.Monitor);
        var context = await NewContextAsync();

        var events = await CollectAsync(strategy.ExecuteAsync(context));

        Assert.Contains(events, e => e is MessageCompletedEvent);
        Assert.Equal(1, fixture.RequestCount); // 检查确实发生
        Assert.DoesNotContain(logger.Entries, e =>
            e.Level == LogLevel.Warning && e.Message.Contains("[DeprecationMonitor]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GatewayHealthFails_DeprecationCheckNotCalled()
    {
        // 探活失败路径不消费 monitor（execute 未发生）。
        var fixture = MakeMonitor(enabled: true);
        using var handler = new GatewayFakeHttpHandler((_, _) =>
            GatewayTestData.JsonResponse("""{"detail":"down"}""", HttpStatusCode.ServiceUnavailable));
        using var client = new MoaGatewayClient(ClientOptions, handler);
        var strategy = new ExpertsStrategy(client, Sessions, deprecationMonitor: fixture.Monitor);
        var context = await NewContextAsync();

        var events = await CollectAsync(strategy.ExecuteAsync(context));

        Assert.Contains(events, e => e is MessageFailedEvent); // 诚实失败
        Assert.Equal(0, fixture.RequestCount); // monitor 零外呼
    }

    // ---- S-MED-5 外呼总闸旁路修复钉死 ----

    [Theory]
    [InlineData(false, true, 0)]   // enabled=false 总闸：monitor=true 也绝不外呼
    [InlineData(false, false, 0)]
    [InlineData(true, false, 0)]   // monitor=false：执行路径不消费
    [InlineData(true, true, 1)]    // 双真：恰好一次 changelog GET
    public async Task CompositionRootGating_BothTrueRequired_ForOutboundCall(
        bool enabled, bool monitor, int expectedOutbound)
    {
        var monitorHandler = new GatewayFakeHttpHandler((_, _) =>
            GatewayTestData.JsonResponse("deprecated", HttpStatusCode.OK));
        // 组合根映射口径：Enabled && Monitor 双真才构造启用实例（App.axaml.cs B6 节）。
        var injected = CompositionRootMonitor(enabled, monitor, monitorHandler);
        using var handler = GatewayUpHandler();
        using var client = new MoaGatewayClient(ClientOptions, handler);
        var strategy = new ExpertsStrategy(client, Sessions, deprecationMonitor: injected);
        var context = await NewContextAsync();

        var events = await CollectAsync(strategy.ExecuteAsync(context));

        Assert.Contains(events, e => e is MessageCompletedEvent);
        Assert.Equal(expectedOutbound, monitorHandler.Requests.Count);
    }
}
