using System.Net;
using AeroAgent.Conversation.Models;
using AeroAgent.Conversation.Orchestration;
using AeroAgent.Moa.Gateway;
using Xunit;

namespace AeroCode.Tests.MoaTests;

/// <summary>
/// <see cref="GatewayOrchestrationFacade"/> 择路与诚实性测试：
/// 网关可用 → 真实网关路径（信封内容 + D6 mock 标注 + 落库 provider/状态）；
/// 网关不可用（探活失败/execute 失败/sidecar 不可用）→ 回退自研编排且
/// <c>Degraded=true</c> + 原因可见（验收门禁"断网回退标注可见"），回退产物在库中标记 Degraded。
/// </summary>
public sealed class GatewayOrchestrationFacadeTests : MoaTestBase
{
    private static readonly Uri BaseUrl = new("http://127.0.0.1:18912");

    private static MoaGatewayClientOptions ClientOptions => new()
    {
        BaseUrl = BaseUrl,
        ApiKey = "facade-key",
        Timeout = TimeSpan.FromSeconds(5),
        HealthTimeout = TimeSpan.FromSeconds(2),
    };

    private static GatewayFakeHttpHandler GatewayUpHandler(bool mock = true, bool mockHeader = true) =>
        new((req, _) =>
        {
            return req.RequestUri!.AbsolutePath switch
            {
                "/health" => GatewayTestData.JsonResponse(GatewayTestData.HealthJson),
                "/v1/moa/execute" => GatewayTestData.JsonResponse(
                    GatewayTestData.ExecuteEnvelope(mock: mock), mockHeader: mockHeader),
                _ => GatewayTestData.JsonResponse("""{"detail":"Not Found"}""", HttpStatusCode.NotFound),
            };
        });

    private async Task<OrchestrationContext> NewContextAsync()
    {
        var session = await NewSessionAsync(OrchestrationStrategy.Single);
        var userMessage = new ChatMessage
        {
            SessionId = session.Id,
            Role = ChatRole.User,
            Content = "测试问题",
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
    public async Task GatewayHealthy_UsesGatewayPath_FinalContentAndEnvelope()
    {
        using var handler = GatewayUpHandler();
        using var client = new MoaGatewayClient(ClientOptions, handler);
        var fallback = new ScriptedFallbackStrategy();
        var facade = new GatewayOrchestrationFacade(client, fallback);

        var outcome = await facade.ExecuteAsync(
            context: null,
            gatewayRequest: new MoaGatewayExecuteRequest { Query = "写一个快速排序" });

        Assert.True(outcome.UsedGateway);
        Assert.False(outcome.Degraded);
        Assert.Null(outcome.DegradedReason);
        Assert.Equal("网关聚合出的最终答复", outcome.Content);
        Assert.NotNull(outcome.GatewayResult);
        Assert.Equal("req-0001", outcome.GatewayResult!.RequestId);
        Assert.True(outcome.Mock); // X-MOA-Mock 头 + 信封 mock 字段双通道
        Assert.Equal(0, fallback.ExecuteCount); // 网关可用时绝不触碰回退
        Assert.Equal(2, handler.Requests.Count); // /health + /v1/moa/execute
    }

    [Fact]
    public async Task GatewayPath_WithSessions_PersistsMessage_WithMockLabel()
    {
        using var handler = GatewayUpHandler(mock: true, mockHeader: true);
        using var client = new MoaGatewayClient(ClientOptions, handler);
        var facade = new GatewayOrchestrationFacade(client, new ScriptedFallbackStrategy(Sessions), Sessions);
        var context = await NewContextAsync();

        var outcome = await facade.ExecuteAsync(
            context, new MoaGatewayExecuteRequest { Query = "写一个快速排序" });

        Assert.True(outcome.UsedGateway);
        Assert.NotNull(outcome.MessageId);

        var messages = (await Sessions.GetMessagesAsync(context.Session.Id)).Value!;
        var gatewayMessage = messages.Single(m => m.Id == outcome.MessageId);
        Assert.Equal(GatewayOrchestrationFacade.GatewayProviderId, gatewayMessage.ProviderId);
        Assert.Equal("网关聚合出的最终答复", gatewayMessage.Content);
        Assert.Equal(MessageStatus.Completed, gatewayMessage.Status); // fallback_used=false
        Assert.Contains("MOA 网关", gatewayMessage.Label);
        Assert.Contains("[Mock]", gatewayMessage.Label); // D6：mock 标注随消息进入 UI
        Assert.Equal("mock/aggregator-mock", gatewayMessage.ModelId);
    }

    [Fact]
    public async Task GatewayPath_FallbackUsedTrue_MessageMarkedDegraded_NoMockLabel()
    {
        using var handler = new GatewayFakeHttpHandler((req, _) => req.RequestUri!.AbsolutePath switch
        {
            "/health" => GatewayTestData.JsonResponse(GatewayTestData.HealthJson),
            "/v1/moa/execute" => GatewayTestData.JsonResponse(
                GatewayTestData.ExecuteEnvelope(mock: false, fallbackUsed: true)),
            _ => GatewayTestData.JsonResponse("""{"detail":"Not Found"}""", HttpStatusCode.NotFound),
        });
        using var client = new MoaGatewayClient(ClientOptions, handler);
        var facade = new GatewayOrchestrationFacade(client, new ScriptedFallbackStrategy(Sessions), Sessions);
        var context = await NewContextAsync();

        var outcome = await facade.ExecuteAsync(
            context, new MoaGatewayExecuteRequest { Query = "写一个快速排序" });

        Assert.True(outcome.UsedGateway);
        Assert.False(outcome.Mock);
        Assert.True(outcome.GatewayResult!.FallbackUsed);

        var messages = (await Sessions.GetMessagesAsync(context.Session.Id)).Value!;
        var gatewayMessage = messages.Single(m => m.Id == outcome.MessageId);
        Assert.Equal(MessageStatus.Degraded, gatewayMessage.Status); // 网关内部兜底如实标注
        Assert.DoesNotContain("[Mock]", gatewayMessage.Label ?? string.Empty);
    }

    [Fact]
    public async Task HealthProbeFails_FallsBack_DegradedTrue_ReasonVisible()
    {
        // 断网：探活直接抛网络异常。
        using var handler = new GatewayFakeHttpHandler(
            (_, _) => throw new HttpRequestException("network is unreachable"));
        using var client = new MoaGatewayClient(ClientOptions, handler);
        var fallback = new ScriptedFallbackStrategy { Deltas = ["断网", "回退", "内容"] };
        var facade = new GatewayOrchestrationFacade(client, fallback);
        var context = await NewContextAsync();

        var outcome = await facade.ExecuteAsync(
            context, new MoaGatewayExecuteRequest { Query = "测试问题" });

        Assert.False(outcome.UsedGateway);
        Assert.True(outcome.Degraded);
        Assert.NotNull(outcome.DegradedReason);
        Assert.Contains("health probe failed", outcome.DegradedReason);
        Assert.Contains("network is unreachable", outcome.DegradedReason);
        Assert.Equal("断网回退内容", outcome.Content); // 回退产物真实呈现
        Assert.NotEmpty(outcome.FallbackEvents);
        Assert.Equal(1, fallback.ExecuteCount);
        Assert.Null(outcome.GatewayResult);
    }

    [Fact]
    public async Task ExecuteFails502_FallsBack_DegradedReasonCarriesHttpCode()
    {
        using var handler = new GatewayFakeHttpHandler((req, _) => req.RequestUri!.AbsolutePath switch
        {
            "/health" => GatewayTestData.JsonResponse(GatewayTestData.HealthJson),
            "/v1/moa/execute" => GatewayTestData.JsonResponse(
                """{"detail":"MoA execution failed: all reference models failed"}""",
                HttpStatusCode.BadGateway),
            _ => GatewayTestData.JsonResponse("""{"detail":"Not Found"}""", HttpStatusCode.NotFound),
        });
        using var client = new MoaGatewayClient(ClientOptions, handler);
        var fallback = new ScriptedFallbackStrategy { Deltas = ["本地", "编排"] };
        var facade = new GatewayOrchestrationFacade(client, fallback);
        var context = await NewContextAsync();

        var outcome = await facade.ExecuteAsync(
            context, new MoaGatewayExecuteRequest { Query = "测试问题" });

        Assert.False(outcome.UsedGateway);
        Assert.True(outcome.Degraded);
        Assert.Contains("execute failed", outcome.DegradedReason);
        Assert.Contains("HTTP 502", outcome.DegradedReason);
        Assert.Equal("本地编排", outcome.Content);
        Assert.Equal(1, fallback.ExecuteCount);
    }

    [Fact]
    public async Task SidecarUnavailable_FallsBack_WithoutSendingAnyHttpRequest()
    {
        using var handler = new GatewayFakeHttpHandler(
            (_, _) => throw new InvalidOperationException("gateway must not be contacted"));
        using var client = new MoaGatewayClient(ClientOptions, handler);
        var sidecarOptions = new GatewaySidecarOptions { Port = 18912 };
        await using var sidecar = new GatewaySidecar(client, sidecarOptions, new FakeGatewayLauncher());
        // sidecar 从未启动（State=Stopped）→ 门面不得白发请求
        var fallback = new ScriptedFallbackStrategy { Deltas = ["离线", "回退"] };
        var facade = new GatewayOrchestrationFacade(client, fallback, sessions: null, sidecar: sidecar);
        var context = await NewContextAsync();

        var outcome = await facade.ExecuteAsync(
            context, new MoaGatewayExecuteRequest { Query = "测试问题" });

        Assert.False(outcome.UsedGateway);
        Assert.True(outcome.Degraded);
        Assert.Contains("sidecar unavailable", outcome.DegradedReason);
        Assert.Contains("state=Stopped", outcome.DegradedReason);
        Assert.Equal("离线回退", outcome.Content);
        Assert.Empty(handler.Requests); // 零网络调用，断言由替身守护
    }

    [Fact]
    public async Task Fallback_WithSessions_MessageIsMarkedDegradedInDatabase()
    {
        // 验收门禁：断网回退标注可见——回退产物在会话库中显式 Degraded。
        using var handler = new GatewayFakeHttpHandler(
            (_, _) => throw new HttpRequestException("connection refused"));
        using var client = new MoaGatewayClient(ClientOptions, handler);
        var fallback = new ScriptedFallbackStrategy(Sessions) { Deltas = ["降级", "产出"] };
        var facade = new GatewayOrchestrationFacade(client, fallback, Sessions);
        var context = await NewContextAsync();

        var outcome = await facade.ExecuteAsync(
            context, new MoaGatewayExecuteRequest { Query = "测试问题" });

        Assert.True(outcome.Degraded);
        Assert.Equal("降级产出", outcome.Content);
        Assert.NotNull(outcome.MessageId);

        var messages = (await Sessions.GetMessagesAsync(context.Session.Id)).Value!;
        var fallbackMessage = messages.Single(m => m.Id == outcome.MessageId);
        Assert.Equal(MessageStatus.Degraded, fallbackMessage.Status); // 库内标注可见
        Assert.Equal("fallback-prov", fallbackMessage.ProviderId);
    }

    [Fact]
    public async Task Fallback_WithoutContext_DegradedWithExplicitError_NoFabricatedContent()
    {
        using var handler = new GatewayFakeHttpHandler(
            (_, _) => throw new HttpRequestException("connection refused"));
        using var client = new MoaGatewayClient(ClientOptions, handler);
        var facade = new GatewayOrchestrationFacade(client, new ScriptedFallbackStrategy());

        var outcome = await facade.ExecuteAsync(
            context: null, gatewayRequest: new MoaGatewayExecuteRequest { Query = "测试问题" });

        Assert.False(outcome.UsedGateway);
        Assert.True(outcome.Degraded);
        Assert.Contains("health probe failed", outcome.DegradedReason);
        Assert.Equal(string.Empty, outcome.Content); // 无上下文可回退 → 不伪造内容
        Assert.NotNull(outcome.Error);
        Assert.Contains("no fallback context", outcome.Error);
    }

    [Fact]
    public async Task FallbackStrategyThrows_DegradedKeepsReasonAndFailureVisible()
    {
        using var handler = new GatewayFakeHttpHandler(
            (_, _) => throw new HttpRequestException("connection refused"));
        using var client = new MoaGatewayClient(ClientOptions, handler);
        var fallback = new ScriptedFallbackStrategy { ThrowOnExecute = true };
        var facade = new GatewayOrchestrationFacade(client, fallback);
        var context = await NewContextAsync();

        var outcome = await facade.ExecuteAsync(
            context, new MoaGatewayExecuteRequest { Query = "测试问题" });

        Assert.True(outcome.Degraded);
        Assert.Contains("health probe failed", outcome.DegradedReason);
        Assert.NotNull(outcome.Error);
        Assert.Contains("fallback strategy exploded", outcome.Error); // 回退失败也如实可见
        Assert.Equal(string.Empty, outcome.Content);
    }

    [Fact]
    public async Task NoQueryAnywhere_ThrowsArgumentException_AsProgrammingError()
    {
        using var handler = GatewayUpHandler();
        using var client = new MoaGatewayClient(ClientOptions, handler);
        var facade = new GatewayOrchestrationFacade(client, new ScriptedFallbackStrategy());

        await Assert.ThrowsAsync<ArgumentException>(
            () => facade.ExecuteAsync(context: null, gatewayRequest: null));
    }
}
