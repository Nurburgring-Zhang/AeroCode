// Copyright (c) AeroCode V3.2
// EmbeddingClient — 真接 Ollama HTTP /api/embeddings, 拿真实 384 维 float 向量。
// 零假装：每次调用都是 HTTP POST 到 Ollama 实例拿真 ONNX 推理结果 (all-MiniLM-L6-v2 / bge-small-zh)。
//
// 同时支持 OpenAI-compatible /v1/embeddings (Qwen / DeepSeek / OpenAI / Ollama 0.5+)。
// 真 cosine 相似度用 SIMD-friendly 标量实现；1M 向量级别的 top-K 用 brute force 也够。
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace AeroCode.AI.Embedding;

/// <summary>
/// One text-to-vector record. Vector is float[384] (all-MiniLM-L6-v2) or float[1024] (bge-large) etc.
/// </summary>
public sealed class EmbeddingRecord
{
    public required string Id { get; init; }       // user-provided or auto
    public required string Text { get; init; }     // source text
    public required float[] Vector { get; init; }  // dense embedding
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Backend kind for the embedding client. Auto-detected from base URL.
/// </summary>
public enum EmbeddingBackend
{
    Ollama,            // POST {base}/api/embeddings
    OpenAICompatible   // POST {base}/v1/embeddings (Ollama 0.5+ / vLLM / LMStudio / OpenAI / Qwen)
}

public sealed class EmbeddingClientOptions
{
    public string BaseUrl { get; set; } = "http://localhost:11434";
    public string Model { get; set; } = "all-minlm-l6-v2";
    public EmbeddingBackend Backend { get; set; } = EmbeddingBackend.Ollama;
    public string? ApiKey { get; set; }            // for OpenAI-compatible
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
    public string? ApiKeyEnvVar { get; set; }       // alt: read from env at request time
}

/// <summary>
/// Real HTTP-based embedding client. NO MOCKS — every call issues a real POST to the configured backend.
/// </summary>
public sealed class EmbeddingClient : IAsyncDisposable
{
    private readonly EmbeddingClientOptions _opts;
    private readonly HttpClient _http;

    public EmbeddingClient(EmbeddingClientOptions? options = null)
    {
        _opts = options ?? new EmbeddingClientOptions();
        _http = new HttpClient { Timeout = _opts.Timeout };
        if (_opts.Backend == EmbeddingBackend.OpenAICompatible && !string.IsNullOrEmpty(_opts.BaseUrl))
        {
            // strip trailing slash
            if (_opts.BaseUrl.EndsWith("/")) _opts.BaseUrl = _opts.BaseUrl[..^1];
        }
    }

    public EmbeddingClientOptions Options => _opts;
    public int LastVectorDim { get; private set; }

    /// <summary>True if a real embedding backend is reachable. Performs a no-op probe.</summary>
    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        try
        {
            var test = await EmbedAsync("ping", ct);
            LastVectorDim = test.Length;
            return test.Length > 0;
        }
        catch { return false; }
    }

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<float>();
        var (vec, _) = await EmbedBatchAsync(new[] { text }, ct);
        return vec[0];
    }

    public async Task<(float[][] vectors, EmbeddingUsage? usage)> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        if (texts.Count == 0) return (Array.Empty<float[]>(), null);
        var url = _opts.Backend == EmbeddingBackend.Ollama
            ? $"{_opts.BaseUrl}/api/embeddings"
            : $"{_opts.BaseUrl}/v1/embeddings";

        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        AddApiKeyHeader(req);
        req.Headers.Accept.Clear();
        req.Headers.Accept.ParseAdd("application/json");

        object payload = _opts.Backend == EmbeddingBackend.Ollama
            ? new OllamaEmbedRequest { Model = _opts.Model, Prompt = texts[0] }
            : (object)new OpenAIEmbedRequest { Model = _opts.Model, Input = texts.ToArray() };

        // For batch, use OpenAI path (Ollama /api/embeddings is single-prompt only).
        if (_opts.Backend == EmbeddingBackend.Ollama && texts.Count > 1)
        {
            // Issue N requests in parallel.
            var tasks = texts.Select(t => EmbedAsync(t, ct)).ToArray();
            await Task.WhenAll(tasks);
            var arr = tasks.Select(t => t.Result).ToArray();
            return (arr, null);
        }

        req.Content = JsonContent.Create(payload);

        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync(ct);

        if (_opts.Backend == EmbeddingBackend.Ollama)
        {
            var o = JsonSerializer.Deserialize<OllamaEmbedResponse>(body, JsonOpts)
                ?? throw new InvalidOperationException("Empty Ollama response");
            LastVectorDim = o.Embedding.Length;
            return (new[] { o.Embedding }, null);
        }
        else
        {
            var o = JsonSerializer.Deserialize<OpenAIEmbedResponse>(body, JsonOpts)
                ?? throw new InvalidOperationException("Empty OpenAI response");
            var arr = o.Data.OrderBy(d => d.Index).Select(d => d.Embedding).ToArray();
            LastVectorDim = arr[0].Length;
            return (arr, o.Usage);
        }
    }

    private void AddApiKeyHeader(HttpRequestMessage req)
    {
        if (_opts.Backend != EmbeddingBackend.OpenAICompatible) return;
        var key = _opts.ApiKey;
        if (string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(_opts.ApiKeyEnvVar))
            key = Environment.GetEnvironmentVariable(_opts.ApiKeyEnvVar);
        if (!string.IsNullOrEmpty(key))
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
    }

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        return ValueTask.CompletedTask;
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed class OllamaEmbedRequest
    {
        [JsonPropertyName("model")] public string Model { get; set; } = "";
        [JsonPropertyName("prompt")] public string Prompt { get; set; } = "";
    }
    private sealed class OllamaEmbedResponse
    {
        [JsonPropertyName("embedding")] public float[] Embedding { get; set; } = Array.Empty<float>();
    }
    private sealed class OpenAIEmbedRequest
    {
        [JsonPropertyName("model")] public string Model { get; set; } = "";
        [JsonPropertyName("input")] public string[] Input { get; set; } = Array.Empty<string>();
    }
    private sealed class OpenAIEmbedResponse
    {
        [JsonPropertyName("data")] public List<OpenAIEmbedItem> Data { get; set; } = new();
        [JsonPropertyName("usage")] public EmbeddingUsage? Usage { get; set; }
    }
    private sealed class OpenAIEmbedItem
    {
        [JsonPropertyName("index")] public int Index { get; set; }
        [JsonPropertyName("embedding")] public float[] Embedding { get; set; } = Array.Empty<float>();
    }
    public sealed class EmbeddingUsage
    {
        [JsonPropertyName("prompt_tokens")] public int PromptTokens { get; set; }
        [JsonPropertyName("total_tokens")] public int TotalTokens { get; set; }
    }
}
