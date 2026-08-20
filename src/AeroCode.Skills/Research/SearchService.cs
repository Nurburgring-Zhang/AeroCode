// Copyright (c) AeroCode V3.3
// SearchService — aggregates multiple real ISearchProvider backends into one query.
// Deduplicates by normalized URL, keeps provider priority order, isolates single-provider
// failures (logged, never fatal), enforces an aggregate timeout. Zero fabricated data:
// if every backend yields nothing, the result is an empty list.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AeroCode.Skills.Research;

/// <summary>
/// Aggregator over one or more real search providers.
/// Constructor signature is a frozen cross-project contract (PHASE 5 master plan).
/// </summary>
public sealed class SearchService
{
    /// <summary>Default aggregate timeout when the caller passes no cancellation.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

    private readonly IReadOnlyList<ISearchProvider> _providers;
    private readonly ILogger _logger;
    private readonly TimeSpan _timeout;

    /// <summary>
    /// Create an aggregator. Providers are queried in list order (priority order);
    /// results are deduplicated by normalized URL keeping the first (highest-priority) hit.
    /// </summary>
    public SearchService(IReadOnlyList<ISearchProvider> providers, ILogger? logger = null, TimeSpan? timeout = null)
    {
        _providers = providers ?? throw new ArgumentNullException(nameof(providers));
        _logger = logger ?? NullLogger.Instance;
        _timeout = timeout ?? DefaultTimeout;
    }

    /// <summary>The providers this service aggregates (in priority order).</summary>
    public IReadOnlyList<ISearchProvider> Providers => _providers;

    /// <summary>
    /// Build the default provider stack from the real environment:
    /// DuckDuckGo HTML (always, key-free) + Bing + Tavily. Bing/Tavily implement
    /// <see cref="IAvailabilityAwareSearchProvider"/> and are skipped at query time
    /// (with an explicit [DEGRADED] log) when their API keys are absent — never faked.
    /// </summary>
    public static SearchService CreateDefault(ILogger? logger = null)
    {
        logger ??= NullLogger.Instance;
        var providers = new List<ISearchProvider>
        {
            new DuckDuckGoHtmlProvider(logger: logger),
            new BingWebProvider(logger: logger),
            new TavilyProvider(logger: logger),
        };
        return new SearchService(providers, logger);
    }

    /// <summary>
    /// Run the query against all providers (priority order), merge + dedupe.
    /// Contract signature frozen per PHASE 5 master plan.
    /// </summary>
    public async Task<IReadOnlyList<WebSearchResult>> SearchAsync(string query, int maxResults = 8, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return Array.Empty<WebSearchResult>();
        if (maxResults <= 0) return Array.Empty<WebSearchResult>();

        using var timeoutCts = new CancellationTokenSource(_timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        var token = linked.Token;

        var merged = new List<WebSearchResult>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var provider in _providers)
        {
            if (merged.Count >= maxResults) break;
            if (token.IsCancellationRequested) break;

            if (provider is IAvailabilityAwareSearchProvider aware && !aware.IsAvailable)
            {
                _logger.LogWarning("[DEGRADED] search provider {Provider} 不可用，跳过：{Reason}",
                    provider.Name, aware.UnavailabilityReason);
                continue;
            }

            IReadOnlyList<WebSearchResult> hits;
            try
            {
                hits = await provider.SearchAsync(query, maxResults, token);
            }
            catch (Exception ex)
            {
                // Single-provider failure is isolated — the aggregate continues.
                _logger.LogWarning("[DEGRADED] search provider {Provider} 查询失败: {Error}（其余 provider 继续）",
                    provider.Name, ex.Message);
                continue;
            }

            foreach (var hit in hits)
            {
                if (merged.Count >= maxResults) break;
                var key = NormalizeUrl(hit.Url);
                if (string.IsNullOrEmpty(key) || !seen.Add(key)) continue;
                merged.Add(hit);
            }
        }

        return merged;
    }

    /// <summary>Normalize a URL for deduplication (scheme/host lowercased, trailing slash removed).</summary>
    public static string NormalizeUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return url.Trim();
        var builder = new UriBuilder(uri) { Host = uri.Host.ToLowerInvariant() };
        var s = builder.Uri.ToString().TrimEnd('/');
        return s;
    }
}
