// Copyright (c) AeroCode V3.2
// Research contracts — the published, stable API surface for real web search.
// Consumed by AeroCode.Skills internally and by AeroCode.Harness (BlockadeResolver).
// Signatures in this file are contractual (PHASE 5 T3): do not change without notice.

namespace AeroCode.Skills.Research;

/// <summary>
/// One web search result returned by a real search provider.
/// </summary>
/// <param name="Title">Result title as shown by the search engine (HTML-decoded).</param>
/// <param name="Url">The resolved destination URL (redirect wrappers already unwrapped).</param>
/// <param name="Snippet">Short description/snippet text (HTML-decoded, may be empty).</param>
/// <param name="Source">Provider name that produced this result (e.g. "duckduckgo-html").</param>
public sealed record WebSearchResult(string Title, string Url, string Snippet, string Source);

/// <summary>
/// A real web search provider. Implementations must perform genuine network calls
/// (or honestly report unavailability) — fabricated results are forbidden.
/// </summary>
public interface ISearchProvider
{
    /// <summary>Provider identifier (e.g. "duckduckgo-html", "bing-web", "tavily").</summary>
    string Name { get; }

    /// <summary>
    /// Execute a real search. Implementations must not throw for "no results";
    /// they should return an empty list. Exceptions are treated as provider failure
    /// and isolated by <see cref="SearchService"/>.
    /// </summary>
    /// <param name="query">Search query text (non-empty).</param>
    /// <param name="maxResults">Maximum number of results requested.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<WebSearchResult>> SearchAsync(string query, int maxResults, CancellationToken ct);
}

/// <summary>
/// Optional capability interface for providers that require configuration
/// (e.g. an API key from an environment variable). <see cref="SearchService"/>
/// skips providers reporting <c>IsAvailable == false</c> instead of calling them.
/// </summary>
public interface IAvailabilityAwareSearchProvider : ISearchProvider
{
    /// <summary>True when the provider is configured and may be called.</summary>
    bool IsAvailable { get; }

    /// <summary>Human-readable reason when unavailable (e.g. "BING_SEARCH_API_KEY not set").</summary>
    string UnavailabilityReason { get; }
}
