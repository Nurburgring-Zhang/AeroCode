// Copyright (c) AeroCode V3.3
// BingWebProvider — real Bing Web Search v7 backend (requires BING_SEARCH_API_KEY).
// Without a key the provider is simply not instantiated by SearchService.CreateDefault;
// if called directly while unconfigured it returns empty + [DEGRADED] log (never fake hits).
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AeroCode.Skills.Research;

/// <summary>Search provider backed by Bing Web Search API v7.</summary>
public sealed class BingWebProvider : ISearchProvider, IAvailabilityAwareSearchProvider, IDisposable
{
    /// <summary>Environment variable holding the Bing subscription key.</summary>
    public const string ApiKeyEnvVar = "BING_SEARCH_API_KEY";

    /// <summary>Bing v7 search endpoint.</summary>
    public const string Endpoint = "https://api.bing.microsoft.com/v7.0/search";

    private readonly HttpClient _http;
    private readonly ILogger _logger;
    private readonly string? _apiKey;

    /// <inheritdoc/>
    public string Name => "bing";

    /// <summary>True when the API key is present in the environment.</summary>
    public static bool IsConfigured => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ApiKeyEnvVar));

    /// <inheritdoc/>
    public bool IsAvailable => !string.IsNullOrWhiteSpace(_apiKey);

    /// <inheritdoc/>
    public string UnavailabilityReason =>
        string.IsNullOrWhiteSpace(_apiKey) ? $"{ApiKeyEnvVar} 未配置" : string.Empty;

    /// <summary>Create the provider (key read from <see cref="ApiKeyEnvVar"/> unless overridden).</summary>
    public BingWebProvider(string? apiKey = null, HttpMessageHandler? handler = null, ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
        _apiKey = apiKey ?? Environment.GetEnvironmentVariable(ApiKeyEnvVar);
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: true);
        _http.Timeout = TimeSpan.FromSeconds(15);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<WebSearchResult>> SearchAsync(string query, int maxResults, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query) || maxResults <= 0) return Array.Empty<WebSearchResult>();
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            _logger.LogWarning("[DEGRADED] BingWebProvider 缺少 {Env}，无结果（不伪造）", ApiKeyEnvVar);
            return Array.Empty<WebSearchResult>();
        }

        var url = Endpoint + "?q=" + Uri.EscapeDataString(query) + "&count=" + Math.Min(maxResults, 50);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("Ocp-Apim-Subscription-Key", _apiKey);
        req.Headers.Add("Accept", "application/json");

        try
        {
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("[DEGRADED] Bing 返回 HTTP {Status}，无结果（不伪造）", (int)resp.StatusCode);
                return Array.Empty<WebSearchResult>();
            }
            var json = await resp.Content.ReadAsStringAsync(ct);
            return ParseWebPages(json, maxResults);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning("[DEGRADED] Bing 查询失败: {Error}（不伪造结果）", ex.Message);
            return Array.Empty<WebSearchResult>();
        }
    }

    /// <summary>Parse Bing v7 JSON (webPages.value[]) — exposed for unit tests with captured payloads.</summary>
    public static IReadOnlyList<WebSearchResult> ParseWebPages(string json, int maxResults)
    {
        var results = new List<WebSearchResult>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("webPages", out var webPages)) return results;
        if (!webPages.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Array) return results;
        foreach (var item in value.EnumerateArray())
        {
            if (results.Count >= maxResults) break;
            var name = item.TryGetProperty("name", out var n) ? n.GetString() : null;
            var pageUrl = item.TryGetProperty("url", out var u) ? u.GetString() : null;
            var snippet = item.TryGetProperty("snippet", out var s) ? s.GetString() : null;
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(pageUrl)) continue;
            results.Add(new WebSearchResult(name!, pageUrl!, snippet ?? string.Empty, "bing"));
        }
        return results;
    }

    /// <inheritdoc/>
    public void Dispose() => _http.Dispose();
}
