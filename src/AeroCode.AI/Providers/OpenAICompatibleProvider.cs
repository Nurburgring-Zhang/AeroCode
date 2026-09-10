using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AeroCode.AI.Configuration;
using AeroCode.AI.Models;
using AeroCode.AI.Resilience;
using Microsoft.Extensions.Logging;

namespace AeroCode.AI.Providers;

/// <summary>
/// OpenAI 兼容协议基类。DeepSeek / Qwen / Kimi / GLM / OpenAI / OpenRouter / Ollama /
/// LMStudio / RunningHub / OpenCode 等都基于此协议,只需改 BaseUrl + 默认模型 + 鉴权头。
/// </summary>
public abstract class OpenAICompatibleProvider : IAiProvider
{
    protected readonly HttpClient Http;
    protected readonly ProviderConfig Config;
    protected readonly ILogger Logger;
    protected readonly AiResiliencePipeline? Resilience;

    /// <summary>R3-δ：可选能力探测（null = 现行为，xhigh 不做 probe 门控）。</summary>
    private readonly Capabilities.IVendorCapabilityProbe? _capabilityProbe;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    protected OpenAICompatibleProvider(HttpClient http, ProviderConfig config, ILogger logger, AiResiliencePipeline? resilience = null, Capabilities.IVendorCapabilityProbe? capabilityProbe = null)
    {
        Http = http;
        Config = config;
        Logger = logger;
        Resilience = resilience;
        _capabilityProbe = capabilityProbe;
        if (config.RequiresApiKey && string.IsNullOrWhiteSpace(config.ApiKeyEnvVar))
            Logger.LogWarning("Provider {Id}: RequiresApiKey=true but ApiKeyEnvVar is empty", config.Id);
        if (config.TimeoutSeconds > 0) http.Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds);
    }

    /// <summary>
    /// Resolve API key fresh on each call so that tests / runtime config changes
    /// (e.g. secrets rotated) are picked up without recreating the provider.
    /// </summary>
    protected string? ResolveApiKey()
    {
        if (!Config.RequiresApiKey) return null;
        if (string.IsNullOrWhiteSpace(Config.ApiKeyEnvVar)) return null;
        return Environment.GetEnvironmentVariable(Config.ApiKeyEnvVar);
    }

    public string ProviderId => Config.Id;
    public string DisplayName => Config.DisplayName;
    public virtual ProviderKind Kind => ProviderKind.OpenAICompatible;
    public bool SupportsStreaming => Config.SupportsStreaming;
    public bool SupportsToolCalling => Config.SupportsToolCalling;
    public virtual bool SupportsThinking => Config.SupportsThinking;
    public bool SupportsVision => Config.SupportsVision;

    protected virtual string ChatCompletionsPath => "/chat/completions";

    protected virtual void ConfigureRequestHeaders(HttpRequestHeaders headers)
    {
        if (Config.ExtraHeaders is { Count: > 0 })
        {
            foreach (var kv in Config.ExtraHeaders) headers.TryAddWithoutValidation(kv.Key, kv.Value);
        }
    }

    protected virtual object BuildRequestBody(ChatRequest request)
    {
        var messages = new List<object>();
        foreach (var m in request.Messages)
        {
            // vision：带图且 provider 支持时，content 以 content-parts 上送；否则纯文本（现行为）。
            object contentValue = m.Content;
            if (m.Images is { Count: > 0 } && Config.SupportsVision)
            {
                var parts = new List<object>();
                if (!string.IsNullOrEmpty(m.Content))
                {
                    parts.Add(new { type = "text", text = m.Content });
                }
                foreach (var img in m.Images)
                {
                    parts.Add(new
                    {
                        type = "image_url",
                        image_url = new { url = $"data:{img.Mime};base64,{img.DataBase64}" }
                    });
                }
                contentValue = parts;
            }

            var msg = new Dictionary<string, object?>
            {
                ["role"] = m.Role,
                ["content"] = contentValue
            };
            if (!string.IsNullOrEmpty(m.Name)) msg["name"] = m.Name;
            if (!string.IsNullOrEmpty(m.ToolCallId)) msg["tool_call_id"] = m.ToolCallId;
            if (m.ToolCalls is { Count: > 0 })
            {
                msg["tool_calls"] = m.ToolCalls.Select(tc => new
                {
                    id = tc.Id,
                    type = tc.Type,
                    function = new { name = tc.FunctionName, arguments = tc.ArgumentsJson }
                }).ToArray<object>();
            }
            if (!string.IsNullOrEmpty(m.ReasoningContent))
            {
                // DeepSeek V4: thinking 模式下必须原样回传 reasoning_content
                msg["reasoning_content"] = m.ReasoningContent;
            }
            messages.Add(msg);
        }

        var body = new Dictionary<string, object?>
        {
            ["model"] = string.IsNullOrEmpty(request.Model) ? Config.DefaultModel : request.Model,
            ["messages"] = messages,
            ["stream"] = request.Stream
        };
        if (request.Temperature.HasValue) body["temperature"] = request.Temperature.Value;
        if (request.MaxTokens.HasValue) body["max_tokens"] = request.MaxTokens.Value;
        if (Config.ExtraBody is { Count: > 0 })
        {
            foreach (var kv in Config.ExtraBody) body[kv.Key] = kv.Value;
        }
        if (request.Tools is { Count: > 0 })
        {
            body["tools"] = request.Tools.Select(t => new
            {
                type = "function",
                function = new
                {
                    name = t.Name,
                    description = t.Description,
                    parameters = JsonNode.Parse(t.ParametersJsonSchema) ?? new JsonObject()
                }
            }).ToArray<object>();
        }
        if (request.EnableThinking && SupportsThinking)
        {
            // DeepSeek V4 协议: thinking object with type=enabled
            body["thinking"] = new { type = "enabled" };
            if (!string.IsNullOrEmpty(request.ThinkingEffort))
                body["reasoning_effort"] = request.ThinkingEffort;
        }
        return body;
    }

    public virtual async Task<ChatResponse> ChatAsync(ChatRequest request, CancellationToken ct = default)
    {
        // R3-δ：xhigh probe 门控（可选链路；probe 未注入 = 请求原样透传，行为与基线一致）。
        var effectiveRequest = await ApplyProbeGatedXHighAsync(request, ct).ConfigureAwait(false);
        var body = BuildRequestBody(effectiveRequest);
        var json = JsonSerializer.Serialize(body, JsonOpts);
        // Use resilience pipeline if available (retry + circuit breaker + rate limit).
        if (Resilience is null)
        {
            return await SendOnceAsync(json, ct).ConfigureAwait(false);
        }
        try
        {
            return await Resilience.ExecuteAsync(async c =>
            {
                var t = SendOnceAsync(json, c);
                return await t.ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
        }
        catch (AiTransientHttpException ex)
        {
            // Convert transient signal into the public exception so callers can surface it.
            throw new AiProviderException(ProviderId, ex.StatusCode, ex.Body);
        }
        catch (Polly.CircuitBreaker.BrokenCircuitException ex)
        {
            // Circuit breaker is open: surface as 503 so callers can retry-after.
            throw new AiProviderException(ProviderId, 503, $"circuit-open: {ex.Message}");
        }
        catch (Polly.RateLimiting.RateLimiterRejectedException ex)
        {
            throw new AiProviderException(ProviderId, 429, $"rate-limited: {ex.Message}");
        }
        catch (Polly.Timeout.TimeoutRejectedException ex)
        {
            throw new AiProviderException(ProviderId, 504, $"timeout: {ex.Message}");
        }
    }

    /// <summary>
    /// R3-δ（可选链路）：O 家族 xhigh probe-gated 发射。仅当注入了 <see cref="Capabilities.IVendorCapabilityProbe"/>
    /// 且请求显式携带峰值档 token "xhigh" 时生效——probe 证实该端点 EffortTiers（Supported/Downgraded）
    /// 才在 wire 层发射 reasoning_effort=xhigh；探测 Missing（离线/未证实/失败，fail-closed）→ 回落基线档
    /// "high"（维持现行为的请求形态，绝不发射未证实的 xhigh）。probe 未注入（null = 现行为）或
    /// effort 值非 "xhigh" 时请求逐字节透传（R2 钉死的 reasoning_effort 发射点不变）。
    /// </summary>
    private async Task<ChatRequest> ApplyProbeGatedXHighAsync(ChatRequest request, CancellationToken ct)
    {
        var probe = _capabilityProbe;
        if (probe is null || !string.Equals(request.ThinkingEffort, "xhigh", StringComparison.Ordinal))
        {
            return request;
        }

        try
        {
            var state = await probe.ProbeAsync(Config.Id, Capabilities.VendorCapability.EffortTiers, ct).ConfigureAwait(false);
            if (state != Capabilities.VendorCapabilityState.Missing)
            {
                return request; // probe 证实（Supported/Downgraded）：按请求发射 xhigh。
            }

            Logger.LogInformation(
                "Provider {Id}: xhigh effort not verified by capability probe (Missing, fail-closed); falling back to baseline \"high\"",
                Config.Id);
            return CloneWithBaselineEffort(request);
        }
        catch (OperationCanceledException)
        {
            throw; // 调用方取消如实上抛（内建 probe 不会抛，此为对探测实现的兜底契约）。
        }
        catch (Exception ex)
        {
            // 探测通道故障一律 fail-closed：绝不发射未证实的 xhigh，回落基线档。
            Logger.LogWarning(
                "Provider {Id}: capability probe failed during xhigh gating ({Error}); falling back to baseline \"high\" (fail-closed)",
                Config.Id, ex.GetType().Name);
            return CloneWithBaselineEffort(request);
        }
    }

    /// <summary>浅拷贝请求并把 ThinkingEffort 落回基线档 "high"（其余字段原样，请求形态 = 基线 high 请求）。</summary>
    private static ChatRequest CloneWithBaselineEffort(ChatRequest request) => new()
    {
        Model = request.Model,
        Messages = request.Messages,
        Tools = request.Tools,
        Temperature = request.Temperature,
        MaxTokens = request.MaxTokens,
        Stream = request.Stream,
        EnableThinking = request.EnableThinking,
        ThinkingEffort = "high",
        CacheBreakpoints = request.CacheBreakpoints,
    };

    private async Task<ChatResponse> SendOnceAsync(string json, CancellationToken ct)
    {
        using var httpReq = BuildHttpRequest(json, stream: false);
        using var resp = await Http.SendAsync(httpReq, ct).ConfigureAwait(false);
        var respText = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var sc = (int)resp.StatusCode;
            if (sc >= 500 || sc == 429 || sc == 408)
                throw new AiTransientHttpException(sc, respText);
            throw new AiProviderException(ProviderId, sc, respText);
        }
        return ParseNonStreamResponse(respText);
    }

    /// <summary>
    /// 流式 idle 超时：ResponseHeadersRead 语义下 HttpClient.Timeout 不覆盖内容读取，
    /// 服务端发完头后停滞会把枚举永久挂起。每收到一行重置倒计时，到期按流中断上抛。
    /// 虚设以便测试注入小值快速验证。
    /// </summary>
    protected virtual TimeSpan StreamIdleTimeout => Config.TimeoutSeconds > 0
        ? TimeSpan.FromSeconds(Config.TimeoutSeconds)
        : TimeSpan.FromMinutes(2);

    public virtual async IAsyncEnumerable<ChatChunk> StreamChatAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var body = BuildRequestBody(request);
        var json = JsonSerializer.Serialize(body, JsonOpts);
        using var httpReq = BuildHttpRequest(json, stream: true);
        using var resp = await Http.SendAsync(httpReq, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new AiProviderException(ProviderId, (int)resp.StatusCode, err);
        }
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        // idle 看门狗：链接令牌 + CancelAfter，每收到一行重置倒计时；
        // 到期触发的取消与调用方取消严格区分（后者保持 OperationCanceledException 语义）。
        // 循环用 ReadLineAsync 返回 null 判 EOF——EndOfStream 会同步阻塞读，绕过看门狗。
        using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idleCts.CancelAfter(StreamIdleTimeout);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            string? line;
            try
            {
                line = await reader.ReadLineAsync(idleCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new AiProviderException(ProviderId, 504,
                    $"stream idle timeout: no data for {StreamIdleTimeout.TotalSeconds:0}s");
            }
            if (line is null) yield break; // EOF：流未带 [DONE] 也如实结束
            idleCts.CancelAfter(StreamIdleTimeout); // 收到数据 → 重置倒计时
            if (string.IsNullOrEmpty(line)) continue;
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
            var data = line[6..].Trim();
            if (data == "[DONE]") yield break;
            var chunk = ParseStreamChunk(data);
            if (chunk is not null) yield return chunk;
        }
    }

    public virtual async Task<bool> HealthCheckAsync(CancellationToken ct = default)
    {
        try
        {
            // 用最小请求健康检查
            var req = new ChatRequest
            {
                Model = Config.DefaultModel,
                Messages = new[] { new ChatMessage { Role = "user", Content = "ping" } },
                Stream = false,
                EnableThinking = false,
                MaxTokens = 4
            };
            var resp = await ChatAsync(req, ct).ConfigureAwait(false);
            return !string.IsNullOrEmpty(resp.Content);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Provider {Id} health check failed", ProviderId);
            return false;
        }
    }

    protected virtual HttpRequestMessage BuildHttpRequest(string json, bool stream)
    {
        var url = Config.BaseUrl.TrimEnd('/') + ChatCompletionsPath;
        var httpReq = new HttpRequestMessage(HttpMethod.Post, url);
        if (ResolveApiKey() is { Length: > 0 } apiKey) httpReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        ConfigureRequestHeaders(httpReq.Headers);
        // B6 版本 pinning：API 版本锁 header 值来自 ProviderConfig.ApiVersionHeaders（配置而非硬编码）；
        // 未配置（null）= 现行为（无额外 header）。置于 ExtraHeaders 之后，与既有自定义头合并。
        var pin = Capabilities.VendorVersionPin.From(Config);
        if (pin is not null)
        {
            foreach (var kv in pin.Headers) httpReq.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
        }
        httpReq.Content = new StringContent(json, Encoding.UTF8, "application/json");
        if (stream) httpReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        return httpReq;
    }

    protected virtual ChatResponse ParseNonStreamResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var id = root.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? string.Empty : string.Empty;
        var model = root.TryGetProperty("model", out var mEl) ? mEl.GetString() ?? string.Empty : string.Empty;
        // Be defensive: malformed responses (no choices, empty array) → return empty content.
        if (!root.TryGetProperty("choices", out var choicesEl) || choicesEl.ValueKind != JsonValueKind.Array || choicesEl.GetArrayLength() == 0)
            return new ChatResponse { Id = id, Model = model, Content = string.Empty, FinishReason = "stop" };
        var choice = choicesEl[0];
        if (!choice.TryGetProperty("message", out var msg))
            return new ChatResponse { Id = id, Model = model, Content = string.Empty, FinishReason = "stop" };
        var content = msg.TryGetProperty("content", out var cEl) ? cEl.GetString() ?? string.Empty : string.Empty;
        var reasoning = msg.TryGetProperty("reasoning_content", out var rEl) ? rEl.GetString() : null;
        var finish = choice.TryGetProperty("finish_reason", out var fEl) ? fEl.GetString() ?? "stop" : "stop";
        var toolCalls = ParseToolCalls(msg);
        var usage = root.TryGetProperty("usage", out var uEl) ? ParseUsage(uEl) : null;
        return new ChatResponse
        {
            Id = id, Model = model, Content = content, ReasoningContent = reasoning,
            FinishReason = finish, ToolCalls = toolCalls, Usage = usage
        };
    }

    protected virtual ChatChunk? ParseStreamChunk(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var id = root.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? string.Empty : string.Empty;
            if (!root.TryGetProperty("choices", out var choicesEl) || choicesEl.GetArrayLength() == 0)
                return null;
            var choice = choicesEl[0];
            if (!choice.TryGetProperty("delta", out var delta)) return null;
            string? dContent = delta.TryGetProperty("content", out var dcEl) ? dcEl.GetString() : null;
            string? dReason = delta.TryGetProperty("reasoning_content", out var drEl) ? drEl.GetString() : null;
            var toolCalls = ParseToolCalls(delta);
            string? finish = choice.TryGetProperty("finish_reason", out var fEl) && fEl.ValueKind == JsonValueKind.String
                ? fEl.GetString() : null;
            return new ChatChunk
            {
                Id = id, DeltaContent = dContent, DeltaReasoning = dReason,
                DeltaToolCalls = toolCalls, FinishReason = finish
            };
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to parse stream chunk: {Json}", json);
            return null;
        }
    }

    protected virtual IReadOnlyList<ToolCall> ParseToolCalls(JsonElement msgOrDelta)
    {
        if (!msgOrDelta.TryGetProperty("tool_calls", out var tcEl) || tcEl.ValueKind != JsonValueKind.Array)
            return Array.Empty<ToolCall>();
        var list = new List<ToolCall>();
        foreach (var tc in tcEl.EnumerateArray())
        {
            var id = tc.TryGetProperty("id", out var iEl) && iEl.ValueKind == JsonValueKind.String
                ? iEl.GetString() ?? string.Empty : string.Empty;
            var fn = tc.GetProperty("function");
            var name = fn.TryGetProperty("name", out var nEl) && nEl.ValueKind == JsonValueKind.String
                ? nEl.GetString() ?? string.Empty : string.Empty;
            var args = fn.TryGetProperty("arguments", out var aEl) && aEl.ValueKind == JsonValueKind.String
                ? aEl.GetString() ?? "{}" : "{}";
            list.Add(new ToolCall { Id = id, Type = "function", FunctionName = name, ArgumentsJson = args });
        }
        return list;
    }

    protected virtual UsageInfo? ParseUsage(JsonElement uEl)
    {
        if (uEl.ValueKind != JsonValueKind.Object) return null;
        int p = uEl.TryGetProperty("prompt_tokens", out var pEl) ? pEl.GetInt32() : 0;
        int c = uEl.TryGetProperty("completion_tokens", out var cEl) ? cEl.GetInt32() : 0;
        int t = uEl.TryGetProperty("total_tokens", out var tEl) ? tEl.GetInt32() : p + c;
        int? cached = uEl.TryGetProperty("prompt_cache_hit_tokens", out var caEl) ? caEl.GetInt32() : null;
        // B2 缓存适配（OpenAI 官方形态）：implicit prompt caching 命中数在
        // usage.prompt_tokens_details.cached_tokens（DeepSeek 用 prompt_cache_hit_tokens）。
        // 仅在前者缺位时补读，不改变既有解析结果。
        if (cached is null && uEl.TryGetProperty("prompt_tokens_details", out var ptdEl) &&
            ptdEl.ValueKind == JsonValueKind.Object &&
            ptdEl.TryGetProperty("cached_tokens", out var ctEl) && ctEl.ValueKind == JsonValueKind.Number)
        {
            cached = ctEl.GetInt32();
        }
        int? reasoning = null;
        if (uEl.TryGetProperty("completion_tokens_details", out var dEl) &&
            dEl.TryGetProperty("reasoning_tokens", out var rEl))
            reasoning = rEl.GetInt32();
        return new UsageInfo { PromptTokens = p, CompletionTokens = c, TotalTokens = t, CachedTokens = cached, ReasoningTokens = reasoning };
    }
}

/// <summary>
/// Provider 异常,含 HTTP 状态码和原始响应体,便于上层自审。
/// </summary>
public sealed class AiProviderException : Exception
{
    public string ProviderId { get; }
    public int StatusCode { get; }
    public string ResponseBody { get; }

    public AiProviderException(string providerId, int statusCode, string body)
        : base($"[{providerId}] HTTP {statusCode}: {body}")
    {
        ProviderId = providerId;
        StatusCode = statusCode;
        ResponseBody = body;
    }
}

/// <summary>
/// 鉴权头注入钩子。Anthropic 用 x-api-key 头, OpenAI 兼容用 Bearer,子类可重写。
/// </summary>
public interface IProviderAuthHook
{
    void Apply(HttpRequestHeaders headers, string? apiKey);
}
