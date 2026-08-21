using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AeroAgent.Conversation.Models;
using AeroAgent.Conversation.Orchestration;
using AeroAgent.Conversation.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AeroAgent.Moa.Gateway;

/// <summary>
/// 网关编排门面的一次执行结果。
/// 诚实性契约：
/// <list type="bullet">
/// <item><see cref="UsedGateway"/>=true 且 <see cref="Degraded"/>=false → 真实网关编排结果；
/// <see cref="Mock"/>=true 时内容是网关 MockProvider 的显式标注合成输出（D6），不得当作真实模型输出呈现。</item>
/// <item><see cref="Degraded"/>=true → 网关不可用，结果来自进程内自研编排回退，
/// <see cref="DegradedReason"/> 如实说明原因（"断网回退标注可见"的落点）。</item>
/// <item><see cref="Error"/> 非空 → 网关与回退双双失败，无内容可呈现。</item>
/// </list>
/// </summary>
public sealed record GatewayOrchestrationOutcome
{
    /// <summary>true = 结果来自 moa-gateway-pro 真实 HTTP 编排。</summary>
    public required bool UsedGateway { get; init; }

    /// <summary>true = 走了回退路径（网关不可达/调用失败），结果必须向用户显式标注降级。</summary>
    public required bool Degraded { get; init; }

    /// <summary>降级原因（Degraded=true 时非空）。</summary>
    public string? DegradedReason { get; init; }

    /// <summary>D6 mock 标注透传：X-MOA-Mock 响应头或信封 mock 字段命中。</summary>
    public bool Mock { get; init; }

    /// <summary>最终答复内容（网关 final_content 或回退编排的聚合产出；双失败为空）。</summary>
    public required string Content { get; init; }

    /// <summary>网关完整信封（UsedGateway=true 时非空）：references/critics/consensus 等。</summary>
    public MoaExecuteResult? GatewayResult { get; init; }

    /// <summary>回退编排产出的原始事件流（Degraded=true 且回退实际执行时非空）。</summary>
    public IReadOnlyList<ChatEvent> FallbackEvents { get; init; } = [];

    /// <summary>持久化的助手消息 Id（有会话上下文时）；否则 null。</summary>
    public string? MessageId { get; init; }

    /// <summary>网关与回退双失败时的错误说明。</summary>
    public string? Error { get; init; }
}

/// <summary>
/// MOA 对外统一编排门面：网关可用 → 真实调用 moa-gateway-pro <c>/v1/moa/execute</c>；
/// 网关不可用（sidecar 未就绪/探活失败/HTTP 失败）→ 显式 [DEGRADED] 并回退
/// <see cref="AeroAgent.Moa"/> 既有进程内自研编排（注入的 <see cref="IOrchestrationStrategy"/>），
/// 结果对象强制携带 <c>Degraded=true</c> + 原因——绝不静默冒充网关结果。
/// 与自研编排是并行能力：门面不改动任何既有策略行为，只在入口层择路与标注。
/// </summary>
public sealed class GatewayOrchestrationFacade
{
    /// <summary>网关产物消息的 provider 标识（区别于直连厂商 API 的 provider）。</summary>
    public const string GatewayProviderId = "moa-gateway";

    private readonly MoaGatewayClient _client;
    private readonly IOrchestrationStrategy _fallback;
    private readonly ISessionService? _sessions;
    private readonly GatewaySidecar? _sidecar;
    private readonly ILogger<GatewayOrchestrationFacade> _logger;

    /// <summary>
    /// 构造。<paramref name="fallback"/> 为网关不可用时的进程内编排
    /// （如 EnsembleStrategy/DecomposeStrategy 任一既有策略实例）；
    /// <paramref name="sessions"/> 提供时网关/回退产物落库并可回读最终内容；
    /// <paramref name="sidecar"/> 提供时先查其可用性，避免对已知离线的网关白发请求。
    /// </summary>
    public GatewayOrchestrationFacade(
        MoaGatewayClient client,
        IOrchestrationStrategy fallback,
        ISessionService? sessions = null,
        GatewaySidecar? sidecar = null,
        ILogger<GatewayOrchestrationFacade>? logger = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
        _sessions = sessions;
        _sidecar = sidecar;
        _logger = logger ?? NullLogger<GatewayOrchestrationFacade>.Instance;
    }

    /// <summary>
    /// 执行一次编排。查询文本取 <paramref name="gatewayRequest"/>.Query，
    /// 缺省时取 <paramref name="context"/> 历史中最后一条用户消息。
    /// 用户消息的持久化由调用方负责（与 ChatOrchestrationFacade 的分工一致）；
    /// 本门面只持久化编排产出的助手消息。
    /// </summary>
    /// <exception cref="ArgumentException">两个来源都拿不到查询文本（编程错误）。</exception>
    public async Task<GatewayOrchestrationOutcome> ExecuteAsync(
        OrchestrationContext? context,
        MoaGatewayExecuteRequest? gatewayRequest = null,
        CancellationToken ct = default)
    {
        var query = gatewayRequest?.Query
            ?? context?.History.LastOrDefault(m => m.Role == ChatRole.User)?.Content;
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new ArgumentException(
                "no query available: provide gatewayRequest.Query or a context whose history contains a user message");
        }

        var effectiveRequest = gatewayRequest is null
            ? new MoaGatewayExecuteRequest { Query = query! }
            : gatewayRequest;

        // ---- 1. sidecar 明确不可用时不白发请求，直接回退 ----
        if (_sidecar is not null && !_sidecar.IsAvailable)
        {
            var reason = $"gateway sidecar unavailable (state={_sidecar.State}): {_sidecar.LastError ?? "not started"}";
            return await FallbackAsync(context, reason, ct);
        }

        // ---- 2. 健康探活（对外部常驻网关同样核实，不缓存过期结论）----
        var health = await _client.HealthAsync(ct);
        if (!health.IsSuccess)
        {
            var reason = $"gateway health probe failed: {health.Error}";
            return await FallbackAsync(context, reason, ct);
        }

        // ---- 3. 真实网关编排 ----
        var executed = await _client.ExecuteAsync(effectiveRequest, ct);
        if (!executed.IsSuccess)
        {
            var reason = $"gateway execute failed: {executed.Error}"
                         + (executed.StatusCode is null ? string.Empty : $" (HTTP {executed.StatusCode})");
            return await FallbackAsync(context, reason, ct);
        }

        var result = executed.Value!;
        var mock = executed.IsMock || result.Mock;
        var messageId = context is null
            ? null
            : await PersistGatewayMessageAsync(context, effectiveRequest, result, mock, ct);

        return new GatewayOrchestrationOutcome
        {
            UsedGateway = true,
            Degraded = false,
            Mock = mock,
            Content = result.FinalContent,
            GatewayResult = result,
            MessageId = messageId,
        };
    }

    // ---------------- 回退路径 ----------------

    private async Task<GatewayOrchestrationOutcome> FallbackAsync(
        OrchestrationContext? context, string reason, CancellationToken ct)
    {
        _logger.LogWarning(
            "[DEGRADED] MOA gateway unavailable ({Reason}); falling back to in-process orchestration '{Kind}'.",
            reason, _fallback.Kind);

        if (context is null)
        {
            return new GatewayOrchestrationOutcome
            {
                UsedGateway = false,
                Degraded = true,
                DegradedReason = reason,
                Content = string.Empty,
                Error = "gateway unavailable and no fallback context was provided",
            };
        }

        var events = new List<ChatEvent>();
        string? failure = null;
        try
        {
            await foreach (var ev in _fallback.ExecuteAsync(context).WithCancellation(ct))
            {
                events.Add(ev);
                if (ev is MessageFailedEvent { MessageId: "" } turnFailed)
                {
                    failure = turnFailed.Error; // 轮级失败（策略整体无产出）
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw; // 取消如实向上抛。
        }
        catch (Exception ex)
        {
            failure = $"fallback orchestration '{_fallback.Kind}' threw: {ex.Message}";
            _logger.LogWarning("[DEGRADED] {Failure}", failure);
        }

        var (content, messageId) = await ExtractFallbackContentAsync(context, events, ct);

        // 回退产物在库中显式标注 Degraded（UI 徽标可见）；策略自身已标 Failed/Cancelled 的不动。
        if (messageId is not null && _sessions is not null)
        {
            await MarkMessageDegradedAsync(context.Session.Id, messageId, ct);
        }

        if (string.IsNullOrEmpty(content) && failure is not null)
        {
            return new GatewayOrchestrationOutcome
            {
                UsedGateway = false,
                Degraded = true,
                DegradedReason = reason,
                Content = string.Empty,
                FallbackEvents = events,
                MessageId = messageId,
                Error = failure,
            };
        }

        return new GatewayOrchestrationOutcome
        {
            UsedGateway = false,
            Degraded = true,
            DegradedReason = reason,
            Content = content,
            FallbackEvents = events,
            MessageId = messageId,
        };
    }

    /// <summary>
    /// 回退最终内容提取：以会话库为事实源（最后一条助手消息），
    /// 事件流增量累积作为无库场景的兜底。
    /// </summary>
    private async Task<(string Content, string? MessageId)> ExtractFallbackContentAsync(
        OrchestrationContext context, List<ChatEvent> events, CancellationToken ct)
    {
        if (_sessions is not null)
        {
            var messages = await _sessions.GetMessagesAsync(context.Session.Id);
            if (messages.IsSuccess && messages.Value is not null)
            {
                var lastAssistant = messages.Value.LastOrDefault(m =>
                    m.Role == ChatRole.Assistant &&
                    m.Status is MessageStatus.Completed or MessageStatus.Degraded or MessageStatus.Streaming);
                if (lastAssistant is not null && !string.IsNullOrEmpty(lastAssistant.Content))
                {
                    return (lastAssistant.Content, lastAssistant.Id);
                }
            }
        }

        // 无库兜底：按事件流累积，取最后一个完成消息的内容。
        var accumulated = new Dictionary<string, StringBuilder>();
        string? finalContent = null;
        string? finalMessageId = null;
        foreach (var ev in events)
        {
            switch (ev)
            {
                case AssistantMessageStarted started:
                    accumulated[started.MessageId] = new StringBuilder();
                    break;
                case TextDeltaEvent delta:
                    if (accumulated.TryGetValue(delta.MessageId, out var sb))
                    {
                        sb.Append(delta.Delta);
                    }

                    break;
                case MessageCompletedEvent completed:
                    if (accumulated.TryGetValue(completed.MessageId, out var done))
                    {
                        finalContent = done.ToString();
                        finalMessageId = completed.MessageId;
                    }

                    break;
            }
        }

        return (finalContent ?? string.Empty, finalMessageId);
    }

    private async Task MarkMessageDegradedAsync(string sessionId, string messageId, CancellationToken ct)
    {
        try
        {
            var messages = await _sessions!.GetMessagesAsync(sessionId);
            if (!messages.IsSuccess || messages.Value is null)
            {
                return;
            }

            var target = messages.Value.FirstOrDefault(m => m.Id == messageId);
            if (target is null || target.Status is MessageStatus.Failed or MessageStatus.Cancelled)
            {
                return;
            }

            target.Status = MessageStatus.Degraded;
            await _sessions.UpdateMessageAsync(target);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 标注失败不吞回退结果，但必须可见。
            _logger.LogWarning("failed to mark fallback message degraded: {Error}", ex.Message);
        }
    }

    // ---------------- 网关路径落库 ----------------

    private async Task<string?> PersistGatewayMessageAsync(
        OrchestrationContext context,
        MoaGatewayExecuteRequest request,
        MoaExecuteResult result,
        bool mock,
        CancellationToken ct)
    {
        if (_sessions is null)
        {
            return null;
        }

        var label = $"MOA 网关 · {result.Preset ?? request.Preset ?? "default"}";
        if (mock)
        {
            label += " · [Mock]"; // D6：显式 mock 标注随消息进入 UI。
        }

        var message = new ChatMessage
        {
            SessionId = context.Session.Id,
            Role = ChatRole.Assistant,
            ProviderId = GatewayProviderId,
            ModelId = result.AggregatorModel ?? result.WinnerModel ?? string.Empty,
            OrchestrationRole = StrategyRole.Synthesizer,
            ParentMessageId = null,
            Label = label,
            Content = result.FinalContent,
            // 网关自身动用内部兜底（fallback_used）时如实标 Degraded；否则 Completed。
            Status = result.FallbackUsed ? MessageStatus.Degraded : MessageStatus.Completed,
        };

        try
        {
            var appended = await _sessions.AppendMessageAsync(message);
            return appended.IsSuccess ? message.Id : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("failed to persist gateway answer: {Error}", ex.Message);
            return null;
        }
    }
}
