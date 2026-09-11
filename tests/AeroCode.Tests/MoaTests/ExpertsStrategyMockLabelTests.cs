// Copyright (c) AeroCode
// #68 X-MOA-Mock 展示面：ExpertsStrategy（用户显式选择的专家团路径）mock 标注回归。
// 语义钉死（与 GatewayOrchestrationFacade 的静默回退路径互补，此处是显式路径）：
// - execute 信封 mock=true 或 X-MOA-Mock 头为真 → 产物消息 Label 含 "[Mock]" 且 Status=Degraded；
// - 非 mock → Label 无 "[Mock]" 且 Status=Completed。
// 用 GatewayFakeHttpHandler 脚本化 /health + /v1/moa/execute（不访问真实网络）。
using System.Net;
using AeroAgent.Conversation.Models;
using AeroAgent.Conversation.Orchestration;
using AeroAgent.Moa.Gateway;
using AeroAgent.Moa.Strategies;
using Xunit;

namespace AeroCode.Tests.MoaTests;

public sealed class ExpertsStrategyMockLabelTests : MoaTestBase
{
    private static MoaGatewayClientOptions ClientOptions => new()
    {
        BaseUrl = new Uri("http://127.0.0.1:18933"),
        ApiKey = "experts-key",
        Timeout = TimeSpan.FromSeconds(5),
        HealthTimeout = TimeSpan.FromSeconds(2),
    };

    private static GatewayFakeHttpHandler GatewayHandler(bool mock, bool mockHeader) => new((req, _) =>
        req.RequestUri!.AbsolutePath switch
        {
            "/health" => GatewayTestData.JsonResponse(GatewayTestData.HealthJson),
            "/v1/moa/execute" => GatewayTestData.JsonResponse(
                GatewayTestData.ExecuteEnvelope(mock: mock), mockHeader: mockHeader),
            _ => GatewayTestData.JsonResponse("""{"detail":"Not Found"}""", HttpStatusCode.NotFound),
        });

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

    private async Task<ChatMessage> RunAndFetchAssistantMessageAsync(bool mock, bool mockHeader)
    {
        using var handler = GatewayHandler(mock, mockHeader);
        using var client = new MoaGatewayClient(ClientOptions, handler);
        var strategy = new ExpertsStrategy(client, Sessions);
        var context = await NewContextAsync();

        var events = await CollectAsync(strategy.ExecuteAsync(context));
        Assert.Contains(events, e => e is MessageCompletedEvent);

        var messages = (await Sessions.GetMessagesAsync(context.Session.Id)).Value!;
        return messages.Single(m =>
            m.Role == ChatRole.Assistant && m.ProviderId == ExpertsStrategy.GatewayProviderId);
    }

    [Fact]
    public async Task MockExecute_LabelHasMockTag_StatusDegraded()
    {
        var message = await RunAndFetchAssistantMessageAsync(mock: true, mockHeader: true);

        Assert.Contains("MOA 专家团", message.Label);
        Assert.Contains("[Mock]", message.Label); // X-MOA-Mock 显式标注随消息进入 UI
        Assert.Equal(MessageStatus.Degraded, message.Status); // mock 如实标 Degraded
        Assert.Equal("网关聚合出的最终答复", message.Content);
    }

    [Fact]
    public async Task NonMockExecute_NoMockTag_StatusCompleted()
    {
        var message = await RunAndFetchAssistantMessageAsync(mock: false, mockHeader: false);

        Assert.Contains("MOA 专家团", message.Label);
        Assert.DoesNotContain("[Mock]", message.Label ?? string.Empty);
        Assert.Equal(MessageStatus.Completed, message.Status);
    }
}
