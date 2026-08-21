using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using AeroAgent.Conversation.Models;
using AeroAgent.Conversation.Orchestration;
using AeroAgent.Conversation.Services;
using AeroAgent.Moa.Gateway;

namespace AeroCode.Tests.MoaTests;

/// <summary>
/// Gateway 测试共享替身：脚本化 HTTP 传输（手写 HttpMessageHandler，真实记录
/// 请求方法/URL/鉴权头/正文）、脚本化网关进程句柄与拉起器、脚本化回退策略。
/// 全部为合法测试替身——不访问网络、不启动进程，但严格遵守真实契约接口。
/// </summary>
internal static class GatewayTestData
{
    public const string HealthJson = """
        {
          "status": "ok",
          "version": "3.1.1",
          "endpoints_total": 3,
          "endpoints_enabled": 3,
          "endpoints_healthy": 3,
          "mock_endpoints_count": 3,
          "real_endpoints_count": 0,
          "mock_mode": "auto"
        }
        """;

    /// <summary>
    /// 与 moa-gateway-pro v3.1.1 <c>MoAResult.to_dict()</c>（routes/moa.py 实测字段）
    /// 逐字段对齐的 execute 响应信封。
    /// </summary>
    public static string ExecuteEnvelope(bool mock = true, bool fallbackUsed = false)
    {
        var envelope = new
        {
            request_id = "req-0001",
            query = "写一个快速排序",
            preset = "fast",
            strategy = "compose",
            references = new object[]
            {
                new { model_id = "mock/qwen3-mock", role = "reference", success = true, latency_ms = 12.3, cost = 0.0, tokens = 42, preview = "参考 A" },
                new { model_id = "mock/glm-mock", role = "reference", success = false, latency_ms = 8.1, cost = 0.0, tokens = 0, preview = "" },
            },
            critics = new[]
            {
                new { model_id = "mock/critic-mock", success = true, issues_count = 2, suggestions_count = 1, latency_ms = 5.5, cost = 0.0 },
            },
            chain_steps = new[]
            {
                new { step = 1, strategy = "compose", preset = "fast", latency_ms = 3.2, cost = 0.0, preview = "step-1" },
            },
            aggregator_model = "mock/aggregator-mock",
            winner_model = (string?)null,
            ranker_output = (object?)null,
            layers_count = 0,
            layer_outputs = (object?)null,
            consensus_score = 0.87,
            iterations = 1,
            total_latency_ms = 42.1,
            total_cost = 0.000123,
            fallback_used = fallbackUsed,
            mock = mock,
            pipeline_stages = (object?)null,
            final_content = "网关聚合出的最终答复",
        };
        return JsonSerializer.Serialize(envelope);
    }

    /// <summary>
    /// 与 moa-gateway-pro v3.1.1 <c>GET /v1/moa/presets</c> 真实返回形状对齐
    /// （扁平字段；注意：没有嵌套 "config" 对象）。
    /// </summary>
    public const string PresetsJson = """
        {
          "presets": [
            { "name": "fast", "strategy": "compose", "description": "最快", "reference_count": 2,
              "aggregator": null, "aggregator_tier": null, "critic_rounds": 0,
              "reference_temperature": 0.6, "aggregator_temperature": 0.3,
              "layer_count": 0, "stages": null, "reference_models": null },
            { "name": "balanced", "strategy": "compose", "description": "均衡", "reference_count": 3,
              "aggregator": null, "aggregator_tier": null, "critic_rounds": 1,
              "reference_temperature": 0.6, "aggregator_temperature": 0.3,
              "layer_count": 0, "stages": null, "reference_models": null }
          ],
          "default": "balanced"
        }
        """;

    public static HttpResponseMessage JsonResponse(
        string json,
        HttpStatusCode status = HttpStatusCode.OK,
        bool mockHeader = false)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        if (mockHeader)
        {
            response.Headers.TryAddWithoutValidation(MoaGatewayClient.MockHeaderName, "true");
        }

        return response;
    }
}

/// <summary>
/// 脚本化 HTTP 传输：真实实现 <see cref="HttpMessageHandler"/> 契约，
/// 完整记录每个请求（方法/绝对 URL/Authorization/Content-Type/正文）供断言。
/// </summary>
internal sealed class GatewayFakeHttpHandler : HttpMessageHandler
{
    internal sealed record RecordedRequest(
        HttpMethod Method,
        Uri Uri,
        string? AuthorizationHeader,
        string? ContentType,
        string? Body);

    private readonly Func<HttpRequestMessage, string?, CancellationToken, Task<HttpResponseMessage>> _responder;

    public List<RecordedRequest> Requests { get; } = new();

    public GatewayFakeHttpHandler(Func<HttpRequestMessage, string?, HttpResponseMessage> responder)
        : this((request, body, _) => Task.FromResult(responder(request, body)))
    {
    }

    public GatewayFakeHttpHandler(
        Func<HttpRequestMessage, string?, CancellationToken, Task<HttpResponseMessage>> responder)
    {
        _responder = responder;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        Requests.Add(new RecordedRequest(
            request.Method,
            request.RequestUri!,
            request.Headers.Authorization?.ToString(),
            request.Content?.Headers.ContentType?.MediaType,
            body));
        return await _responder(request, body, ct);
    }
}

/// <summary>脚本化网关进程句柄：可控退出/退出码/输出尾部，记录 Kill 次数。</summary>
internal sealed class FakeGatewayProcessHandle : IGatewayProcessHandle
{
    private readonly TaskCompletionSource _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _exitCode;
    private int _killCount;

    public int ProcessId { get; init; } = Random.Shared.Next(10_000, 99_999);

    public bool HasExited => _exit.Task.IsCompleted;

    public int? ExitCode => HasExited ? _exitCode : null;

    public string? OutputTail { get; init; }

    public int KillCount => Volatile.Read(ref _killCount);

    public void SimulateExit(int exitCode)
    {
        _exitCode = exitCode;
        _exit.TrySetResult();
    }

    public Task WaitForExitAsync(CancellationToken ct) => _exit.Task.WaitAsync(ct);

    public void Kill()
    {
        Interlocked.Increment(ref _killCount);
        SimulateExit(-1);
    }
}

/// <summary>脚本化拉起器：按入队顺序返回预设结果，并真实记录收到的 LaunchSpec。</summary>
internal sealed class FakeGatewayLauncher : IGatewayProcessLauncher
{
    private readonly Queue<Func<GatewayLaunchResult>> _scripted = new();

    public List<GatewayLaunchSpec> Specs { get; } = new();

    public void Enqueue(Func<GatewayLaunchResult> next) => _scripted.Enqueue(next);

    public Task<GatewayLaunchResult> LaunchAsync(GatewayLaunchSpec spec, CancellationToken ct)
    {
        Specs.Add(spec);
        var next = _scripted.Count > 0
            ? _scripted.Dequeue()
            : () => GatewayLaunchResult.Fail("no scripted launch left");
        return Task.FromResult(next());
    }
}

/// <summary>
/// 脚本化回退编排策略：模拟既有自研策略的真实行为——产出事件流，
/// 且（如同真实策略）自行把助手消息持久化到会话库。
/// </summary>
internal sealed class ScriptedFallbackStrategy : IOrchestrationStrategy
{
    private readonly ISessionService? _sessions;
    private int _executeCount;

    public ScriptedFallbackStrategy(ISessionService? sessions = null) => _sessions = sessions;

    public OrchestrationStrategy Kind { get; init; } = OrchestrationStrategy.Ensemble;

    public string[] Deltas { get; init; } = ["回退", "编排", "产出"];

    public bool ThrowOnExecute { get; init; }

    public int ExecuteCount => Volatile.Read(ref _executeCount);

    public async IAsyncEnumerable<ChatEvent> ExecuteAsync(OrchestrationContext context)
    {
        Interlocked.Increment(ref _executeCount);
        if (ThrowOnExecute)
        {
            throw new InvalidOperationException("fallback strategy exploded");
        }

        var messageId = Guid.NewGuid().ToString("N");
        if (_sessions is not null)
        {
            var message = new ChatMessage
            {
                SessionId = context.Session.Id,
                Role = ChatRole.Assistant,
                ProviderId = "fallback-prov",
                ModelId = "fallback-model",
                OrchestrationRole = StrategyRole.Synthesizer,
                Content = string.Concat(Deltas),
                Status = MessageStatus.Completed,
            };
            var appended = await _sessions.AppendMessageAsync(message);
            if (appended.IsSuccess)
            {
                messageId = message.Id;
            }
        }

        yield return new AssistantMessageStarted
        {
            SessionId = context.Session.Id,
            MessageId = messageId,
            ProviderId = "fallback-prov",
            ModelId = "fallback-model",
            OrchestrationRole = StrategyRole.Synthesizer,
        };
        foreach (var delta in Deltas)
        {
            yield return new TextDeltaEvent
            {
                SessionId = context.Session.Id,
                MessageId = messageId,
                Delta = delta,
            };
        }

        yield return new MessageCompletedEvent
        {
            SessionId = context.Session.Id,
            MessageId = messageId,
        };
    }
}

/// <summary>sidecar 状态观察器：记录 StateChanged 事件的完整序列。</summary>
internal sealed class SidecarStateObserver
{
    private readonly ConcurrentQueue<GatewaySidecarState> _states = new();

    public IReadOnlyList<GatewaySidecarState> States => _states.ToArray();

    public void OnStateChanged(GatewaySidecarState state) => _states.Enqueue(state);
}
