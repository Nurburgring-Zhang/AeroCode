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

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    protected OpenAICompatibleProvider(HttpClient http, ProviderConfig config, ILogger logger, AiResiliencePipeline? resilience = null)
    {
        Http = http;
        Config = config;
        Logger = logger;
        Resilience = resilience;
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
            var msg = new Dictionary<string, object?>
            {
                ["role"] = m.Role,
                ["content"] = m.Content
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
        var body = BuildRequestBody(request);
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
        while (!reader.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
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
