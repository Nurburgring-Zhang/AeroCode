// AgentExpertExecutor direct execution tests.
// Drives the real HarnessHost sub-agent factory with a scripted IAiProvider so the
// executor's RunAsync loop is exercised without network calls or external services.
using AeroAgent.Autonomy.Cluster;
using AeroCode.AI.Models;
using AeroCode.AI.Providers;
using AeroCode.Harness;
using Xunit;

namespace AeroCode.Tests.Autonomy.Cluster;

/// <summary>
/// Scripted AI provider for AgentExpertExecutor tests: returns deterministic assistant
/// content based on the test scenario, while still honoring the real ChatAsync contract.
/// </summary>
internal sealed class AgentTestAiProvider : IAiProvider
{
    private readonly Func<ChatRequest, ChatResponse> _respond;

    public AgentTestAiProvider(Func<ChatRequest, ChatResponse> respond) => _respond = respond;

    public string ProviderId => "test-provider";
    public string DisplayName => "Test Provider";
    public ProviderKind Kind => ProviderKind.OpenAICompatible;
    public bool SupportsStreaming => false;
    public bool SupportsToolCalling => false;
    public bool SupportsThinking => false;

    public List<ChatRequest> Requests { get; } = new();

    public Task<ChatResponse> ChatAsync(ChatRequest request, CancellationToken ct = default)
    {
        Requests.Add(request);
        return Task.FromResult(_respond(request));
    }

    public IAsyncEnumerable<ChatChunk> StreamChatAsync(ChatRequest request, CancellationToken ct = default)
        => throw new NotSupportedException();

    public Task<bool> HealthCheckAsync(CancellationToken ct = default) => Task.FromResult(true);
}

public sealed class ClusterAgentExpertExecutorRunTests
{
    private static ExpertExecutionContext Context(
        string expertId = "expert-1",
        string sessionId = "",
        string role = "测试工程师",
        string task = "为订单服务编写单元测试",
        string memory = "") => new(
            ExpertId: expertId,
            ExpertSessionId: sessionId,
            Role: role,
            NodeId: "n9",
            NodeName: "单元测试",
            TaskText: task,
            MemorySnapshot: memory,
            AttemptKind: ExpertAttemptKind.Primary,
            FanOutIndex: 0);

    [Fact]
    public async Task ExecuteAsync_Success_ReturnsAgentOutput()
    {
        using var host = new HarnessHost();
        var provider = new AgentTestAiProvider(_ => new ChatResponse
        {
            Content = "已编写订单服务单元测试：下单/支付/退款。",
        });
        var executor = new AgentExpertExecutor(host, provider);

        var outcome = await executor.ExecuteAsync(Context(), CancellationToken.None);

        Assert.True(outcome.Succeeded);
        Assert.False(outcome.Cancelled);
        Assert.Equal("已编写订单服务单元测试：下单/支付/退款。", outcome.Output);
        Assert.Null(outcome.Error);
        Assert.True(provider.Requests.Count >= 1);
        var lastRequest = provider.Requests[^1];
        Assert.Contains("为订单服务编写单元测试", lastRequest.Messages[^1].Content);
    }

    [Fact]
    public async Task ExecuteAsync_EmptyResponse_ReturnsFailedOutcome()
    {
        using var host = new HarnessHost();
        var provider = new AgentTestAiProvider(_ => new ChatResponse { Content = "   " });
        var executor = new AgentExpertExecutor(host, provider);

        var outcome = await executor.ExecuteAsync(Context(), CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.False(outcome.Cancelled);
        Assert.Equal("agent produced no output", outcome.Error);
    }

    [Fact]
    public async Task ExecuteAsync_Cancelled_ReturnsCancelledOutcome()
    {
        using var host = new HarnessHost();
        var provider = new AgentTestAiProvider(_ => new ChatResponse { Content = "partial" });
        var executor = new AgentExpertExecutor(host, provider);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var outcome = await executor.ExecuteAsync(Context(), cts.Token);

        Assert.False(outcome.Succeeded);
        Assert.True(outcome.Cancelled);
    }

    [Fact]
    public async Task ExecuteAsync_AgentThrows_ReturnsFailedOutcomeWithExceptionInfo()
    {
        using var host = new HarnessHost();
        var provider = new AgentTestAiProvider(_ => throw new InvalidOperationException("llm loop boom"));
        var executor = new AgentExpertExecutor(host, provider);

        var outcome = await executor.ExecuteAsync(Context(), CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Contains("InvalidOperationException", outcome.Error);
        Assert.Contains("llm loop boom", outcome.Error);
    }

    [Fact]
    public async Task ExecuteAsync_StableSessionId_EachAttemptRunsFreshSubAgentContext()
    {
        // HarnessHost.CreateAgent 为每次调用构建独立 Agent 实例：即使 ExpertSessionId
        // 稳定，每个 attempt 也从一个干净的子代理上下文开始（无跨 attempt 历史累积）。
        // 本测试锁定该语义，防止未来无意改动导致上下文跨 attempt 串扰。
        using var host = new HarnessHost();
        var provider = new AgentTestAiProvider(req =>
        {
            var userTurns = req.Messages.Count(m => m.Role == "user");
            return new ChatResponse { Content = $"turns-{userTurns}" };
        });
        var executor = new AgentExpertExecutor(host, provider);
        var context = Context(sessionId: "sess-reused-42");

        var first = await executor.ExecuteAsync(context, CancellationToken.None);
        var second = await executor.ExecuteAsync(context, CancellationToken.None);

        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);
        Assert.Equal("turns-1", first.Output);
        Assert.Equal("turns-1", second.Output);
    }

    [Fact]
    public async Task ExecuteAsync_MemorySnapshot_IsInjectedIntoPrompt()
    {
        using var host = new HarnessHost();
        string? capturedPrompt = null;
        var provider = new AgentTestAiProvider(req =>
        {
            capturedPrompt = req.Messages[^1].Content;
            return new ChatResponse { Content = "ok" };
        });
        var executor = new AgentExpertExecutor(host, provider);
        var memory = "- [2026-01-01T00:00:00Z] (cluster) 上次结论：必须幂等";

        var outcome = await executor.ExecuteAsync(Context(memory: memory), CancellationToken.None);

        Assert.True(outcome.Succeeded);
        Assert.NotNull(capturedPrompt);
        Assert.Contains("上次结论：必须幂等", capturedPrompt);
        Assert.Contains("你的持久记忆", capturedPrompt);
    }
}
