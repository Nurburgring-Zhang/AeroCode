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
/// Anthropic Claude 5 Provider。Anthropic Messages API 独立协议, 与 OpenAI 兼容不同。
/// 端点: POST https://api.anthropic.com/v1/messages
/// 必需 header: x-api-key, anthropic-version
/// </summary>
public sealed class ClaudeProvider : IAiProvider
{
    private readonly HttpClient _http;
    private readonly ProviderConfig _config;
    private readonly ILogger<ClaudeProvider> _logger;
    private readonly AiResiliencePipeline? _resilience;
    private readonly string? _apiKey;
    private const string MessagesPath = "/v1/messages";
    private const string AnthropicVersion = "2023-06-01";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public ClaudeProvider(HttpClient http, ProviderConfig config, ILogger<ClaudeProvider> logger, AiResiliencePipeline? resilience = null)
    {
        _http = http;
        _config = config;
        _logger = logger;
        _resilience = resilience;
        if (!string.IsNullOrWhiteSpace(config.ApiKeyEnvVar))
            _apiKey = Environment.GetEnvironmentVariable(config.ApiKeyEnvVar);
    }

    public string ProviderId => _config.Id;
    public string DisplayName => _config.DisplayName;
    public ProviderKind Kind => ProviderKind.AnthropicMessages;
    public bool SupportsStreaming => _config.SupportsStreaming;
    public bool SupportsToolCalling => _config.SupportsToolCalling;
    public bool SupportsThinking => true; // Claude extended thinking

    public async Task<ChatResponse> ChatAsync(ChatRequest request, CancellationToken ct = default)
    {
        if (_resilience is null)
        {
            return await SendOnceAsync(request, ct).ConfigureAwait(false);
        }
        try
        {
            return await _resilience.ExecuteAsync(async c =>
            {
                var t = SendOnceAsync(request, c);
                return await t.ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
        }
        catch (AiTransientHttpException ex)
        {
            throw new AiProviderException(ProviderId, ex.StatusCode, ex.Body);
        }
        catch (Polly.CircuitBreaker.BrokenCircuitException ex)
        {
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

    private async Task<ChatResponse> SendOnceAsync(ChatRequest request, CancellationToken ct)
    {
        var body = BuildBody(request, stream: false);
        using var req = BuildHttpRequest(body);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var sc = (int)resp.StatusCode;
            if (sc >= 500 || sc == 429 || sc == 408)
                throw new AiTransientHttpException(sc, text);
            throw new AiProviderException(ProviderId, sc, text);
        }
        return ParseResponse(text);
    }

    public async IAsyncEnumerable<ChatChunk> StreamChatAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var body = BuildBody(request, stream: true);
        using var req = BuildHttpRequest(body);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
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
            if (string.IsNullOrEmpty(data) || data == "[DONE]") continue;
            var chunk = ParseStreamEvent(data);
            if (chunk is not null) yield return chunk;
        }
    }

    public async Task<bool> HealthCheckAsync(CancellationToken ct = default)
    {
        try
        {
            var resp = await ChatAsync(new ChatRequest
            {
                Model = _config.DefaultModel,
                Messages = new[] { new ChatMessage { Role = "user", Content = "ping" } },
                MaxTokens = 4,
                EnableThinking = false
            }, ct);
            return !string.IsNullOrEmpty(resp.Content);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Claude health check failed");
            return false;
        }
    }

    private object BuildBody(ChatRequest request, bool stream)
    {
        // Anthropic API 字段: model, max_tokens, system, messages[], stream, tools[]
        string? systemText = null;
        var msgs = new List<object>();
        foreach (var m in request.Messages)
        {
            if (m.Role == "system") { systemText = m.Content; continue; }
            if (m.Role == "tool")
            {
                msgs.Add(new { role = "user", content = new object[] { new { type = "tool_result", tool_use_id = m.ToolCallId, content = m.Content } } });
                continue;
            }
            if (m.ToolCalls is { Count: > 0 })
            {
                var content = new List<object> { new { type = "text", text = m.Content ?? string.Empty } };
                foreach (var tc in m.ToolCalls)
                {
                    content.Add(new { type = "tool_use", id = tc.Id, name = tc.FunctionName, input = JsonNode.Parse(tc.ArgumentsJson) ?? new JsonObject() });
                }
                msgs.Add(new { role = "assistant", content });
                continue;
            }
            msgs.Add(new { role = m.Role, content = m.Content });
        }

        var body = new Dictionary<string, object?>
        {
            ["model"] = string.IsNullOrEmpty(request.Model) ? _config.DefaultModel : request.Model,
            ["max_tokens"] = request.MaxTokens ?? 4096,
            ["messages"] = msgs,
            ["stream"] = stream
        };
        if (!string.IsNullOrEmpty(systemText)) body["system"] = systemText;
        if (request.Temperature.HasValue) body["temperature"] = request.Temperature.Value;
        if (request.Tools is { Count: > 0 })
        {
            body["tools"] = request.Tools.Select(t => new
            {
                name = t.Name,
                description = t.Description,
                input_schema = JsonNode.Parse(t.ParametersJsonSchema) ?? new JsonObject()
            }).ToArray<object>();
        }
        if (request.EnableThinking && SupportsThinking)
        {
            body["thinking"] = new { type = "enabled", budget_tokens = 5000 };
        }
        return body;
    }

    private HttpRequestMessage BuildHttpRequest(object body)
    {
        var json = JsonSerializer.Serialize(body, JsonOpts);
        var url = _config.BaseUrl.TrimEnd('/') + MessagesPath;
        var req = new HttpRequestMessage(HttpMethod.Post, url);
        if (!string.IsNullOrEmpty(_apiKey)) req.Headers.Add("x-api-key", _apiKey);
        req.Headers.Add("anthropic-version", AnthropicVersion);
        if (_config.ExtraHeaders is { Count: > 0 })
            foreach (var kv in _config.ExtraHeaders) req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
        req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return req;
    }

    private static ChatResponse ParseResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var id = root.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? string.Empty : string.Empty;
        var model = root.TryGetProperty("model", out var mEl) ? mEl.GetString() ?? string.Empty : string.Empty;
        var content = new StringBuilder();
        var toolCalls = new List<ToolCall>();
        string? stopReason = root.TryGetProperty("stop_reason", out var srEl) ? srEl.GetString() : null;
        if (root.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in contentEl.EnumerateArray())
            {
                var type = block.TryGetProperty("type", out var tEl) ? tEl.GetString() : null;
                if (type == "text" && block.TryGetProperty("text", out var textEl))
                    content.Append(textEl.GetString());
                else if (type == "tool_use")
                {
                    var id2 = block.TryGetProperty("id", out var iEl) ? iEl.GetString() ?? string.Empty : string.Empty;
                    var name = block.TryGetProperty("name", out var nEl) ? nEl.GetString() ?? string.Empty : string.Empty;
                    var input = block.TryGetProperty("input", out var inpEl) ? inpEl.GetRawText() : "{}";
                    toolCalls.Add(new ToolCall { Id = id2, Type = "function", FunctionName = name, ArgumentsJson = input });
                }
            }
        }
        var usage = root.TryGetProperty("usage", out var uEl) ? new UsageInfo
        {
            PromptTokens = uEl.TryGetProperty("input_tokens", out var ipt) ? ipt.GetInt32() : 0,
            CompletionTokens = uEl.TryGetProperty("output_tokens", out var outt) ? outt.GetInt32() : 0,
            TotalTokens = (uEl.TryGetProperty("input_tokens", out var ipt2) ? ipt2.GetInt32() : 0)
                         + (uEl.TryGetProperty("output_tokens", out var out2) ? out2.GetInt32() : 0)
        } : null;
        return new ChatResponse
        {
            Id = id, Model = model, Content = content.ToString(),
            FinishReason = stopReason ?? "stop", ToolCalls = toolCalls, Usage = usage
        };
    }

    private static ChatChunk? ParseStreamEvent(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var tEl) ? tEl.GetString() : null;
            if (type is null) return null;
            if (type == "content_block_delta")
            {
                var delta = root.GetProperty("delta");
                if (delta.TryGetProperty("text", out var txtEl))
                {
                    return new ChatChunk
                    {
                        Id = root.TryGetProperty("message", out var msgEl) && msgEl.TryGetProperty("id", out var idEl)
                            ? idEl.GetString() ?? string.Empty : string.Empty,
                        DeltaContent = txtEl.GetString()
                    };
                }
            }
            return null;
        }
        catch { return null; }
    }
}
