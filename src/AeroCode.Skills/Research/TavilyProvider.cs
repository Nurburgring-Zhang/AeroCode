// Copyright (c) AeroCode V3.3
// TavilyProvider — real Tavily search API backend (requires TAVILY_API_KEY).
// Without a key the provider is simply not instantiated by SearchService.CreateDefault;
// if called directly while unconfigured it returns empty + [DEGRADED] log (never fake hits).
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AeroCode.Skills.Research;

/// <summary>Search provider backed by the Tavily search API.</summary>
public sealed class TavilyProvider : ISearchProvider, IAvailabilityAwareSearchProvider, IDisposable
{
    /// <summary>Environment variable holding the Tavily API key.</summary>
    public const string ApiKeyEnvVar = "TAVILY_API_KEY";

    /// <summary>Tavily search endpoint.</summary>
    public const string Endpoint = "https://api.tavily.com/search";

    private readonly HttpClient _http;
    private readonly ILogger _logger;
    private readonly string? _apiKey;

    /// <inheritdoc/>
    public string Name => "tavily";

    /// <summary>True when the API key is present in the environment.</summary>
    public static bool IsConfigured => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ApiKeyEnvVar));

    /// <inheritdoc/>
    public bool IsAvailable => !string.IsNullOrWhiteSpace(_apiKey);

    /// <inheritdoc/>
    public string UnavailabilityReason =>
        string.IsNullOrWhiteSpace(_apiKey) ? $"{ApiKeyEnvVar} 未配置" : string.Empty;

    /// <summary>Create the provider (key read from <see cref="ApiKeyEnvVar"/> unless overridden).</summary>
    public TavilyProvider(string? apiKey = null, HttpMessageHandler? handler = null, ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
        _apiKey = apiKey ?? Environment.GetEnvironmentVariable(ApiKeyEnvVar);
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: true);
        _http.Timeout = TimeSpan.FromSeconds(20);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<WebSearchResult>> SearchAsync(string query, int maxResults, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query) || maxResults <= 0) return Array.Empty<WebSearchResult>();
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            _logger.LogWarning("[DEGRADED] TavilyProvider 缺少 {Env}，无结果（不伪造）", ApiKeyEnvVar);
            return Array.Empty<WebSearchResult>();
        }

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint);
            req.Headers.Add("Authorization", "Bearer " + _apiKey);
            req.Content = JsonContent.Create(new { query, max_results = Math.Min(maxResults, 20) });

            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("[DEGRADED] Tavily 返回 HTTP {Status}，无结果（不伪造）", (int)resp.StatusCode);
                return Array.Empty<WebSearchResult>();
            }
            var json = await resp.Content.ReadAsStringAsync(ct);
            return ParseResults(json, maxResults);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning("[DEGRADED] Tavily 查询失败: {Error}（不伪造结果）", ex.Message);
            return Array.Empty<WebSearchResult>();
        }
    }

    /// <summary>Parse Tavily JSON (results[]) — exposed for unit tests with captured payloads.</summary>
    public static IReadOnlyList<WebSearchResult> ParseResults(string json, int maxResults)
    {
        var results = new List<WebSearchResult>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("results", out var arr) || arr.ValueKind != JsonValueKind.Array) return results;
        foreach (var item in arr.EnumerateArray())
        {
            if (results.Count >= maxResults) break;
            var title = item.TryGetProperty("title", out var t) ? t.GetString() : null;
            var url = item.TryGetProperty("url", out var u) ? u.GetString() : null;
            var content = item.TryGetProperty("content", out var c) ? c.GetString() : null;
            if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(url)) continue;
            results.Add(new WebSearchResult(title!, url!, content ?? string.Empty, "tavily"));
        }
        return results;
    }

    /// <inheritdoc/>
    public void Dispose() => _http.Dispose();
}
