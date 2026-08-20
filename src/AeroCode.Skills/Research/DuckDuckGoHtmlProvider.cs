// Copyright (c) AeroCode V3.3
// DuckDuckGoHtmlProvider — real key-free web search via the html.duckduckgo.com endpoint.
// Every call performs a genuine HTTP GET; bot-challenge responses are detected and
// reported honestly (empty result + [DEGRADED] log) instead of fabricating hits.
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AeroCode.Skills.Research;

/// <summary>
/// Search provider backed by DuckDuckGo's HTML endpoint (no API key required).
/// </summary>
public sealed class DuckDuckGoHtmlProvider : ISearchProvider, IAvailabilityAwareSearchProvider, IDisposable
{
    /// <summary>Key-free DDG search endpoint.</summary>
    public const string HtmlEndpoint = "https://html.duckduckgo.com/html/";

    private const string DefaultUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36";

    private readonly HttpClient _http;
    private readonly ILogger _logger;
    private readonly bool _ownsHttp;

    /// <inheritdoc/>
    public string Name => DuckDuckGoHtmlParser.SourceName;

    /// <inheritdoc/>
    public bool IsAvailable => true; // key-free endpoint; bot-challenges handled per-query, honestly.

    /// <inheritdoc/>
    public string UnavailabilityReason => string.Empty;

    /// <summary>
    /// Create the provider.
    /// </summary>
    /// <param name="handler">Optional transport handler (tests inject a fake transport; production uses a real one).</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="requestTimeout">Per-request timeout (default 15s).</param>
    public DuckDuckGoHtmlProvider(HttpMessageHandler? handler = null, ILogger? logger = null, TimeSpan? requestTimeout = null)
    {
        _logger = logger ?? NullLogger.Instance;
        if (handler is null)
        {
            _http = new HttpClient();
            _ownsHttp = true;
        }
        else
        {
            _http = new HttpClient(handler, disposeHandler: true);
            _ownsHttp = true;
        }
        _http.Timeout = requestTimeout ?? TimeSpan.FromSeconds(15);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(DefaultUserAgent);
        _http.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml");
        _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<WebSearchResult>> SearchAsync(string query, int maxResults, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query) || maxResults <= 0) return Array.Empty<WebSearchResult>();

        var url = HtmlEndpoint + "?q=" + Uri.EscapeDataString(query);
        string html;
        try
        {
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("[DEGRADED] DuckDuckGo 返回 HTTP {Status}，无结果（不伪造）", (int)resp.StatusCode);
                return Array.Empty<WebSearchResult>();
            }
            html = await resp.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning("[DEGRADED] DuckDuckGo 请求失败: {Error}（不伪造结果）", ex.Message);
            return Array.Empty<WebSearchResult>();
        }

        if (DuckDuckGoHtmlParser.IsChallengePage(html))
        {
            _logger.LogWarning("[DEGRADED] DuckDuckGo 返回反爬质询页(anomaly challenge)，本次无真实结果（不伪造）");
            return Array.Empty<WebSearchResult>();
        }

        return DuckDuckGoHtmlParser.Parse(html, maxResults);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
