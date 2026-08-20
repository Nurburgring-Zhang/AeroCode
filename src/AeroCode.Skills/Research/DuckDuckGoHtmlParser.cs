// Copyright (c) AeroCode V3.3
// DuckDuckGoHtmlParser — pure-function parser for the html.duckduckgo.com/html/ endpoint.
// No HTTP here (see DuckDuckGoHtmlProvider); this class only turns a real HTML payload
// into structured hits so it can be unit-tested against captured documents.
//
// Endpoint structure parsed (stable DDG "html" layout):
//   <a class="result__a" href="//duckduckgo.com/l/?uddg=<url-encoded-real-url>&rut=..">Title</a>
//   <a class="result__snippet" ...>snippet text</a>
//   (lite endpoint: <a class="result-link"> + <td class="result-snippet">)
//
// Honesty note (2026-08-20): live capture from the build machine is blocked by DDG's
// anomaly/bot challenge (both html/ and lite/ endpoints returned challenge-form pages).
// The parser therefore also detects challenge pages explicitly so the provider can
// report "no results (challenged)" instead of fabricating hits.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;
using HtmlAgilityPack;

namespace AeroCode.Skills.Research;

/// <summary>Pure parser for DuckDuckGo HTML search responses. Thread-safe (stateless).</summary>
public static class DuckDuckGoHtmlParser
{
    /// <summary>Provider source tag stamped onto every parsed hit.</summary>
    public const string SourceName = "duckduckgo";

    /// <summary>
    /// Detect DDG anti-bot challenge pages (anomaly.js / challenge-form / anomaly-modal).
    /// Such pages contain zero organic results; callers must treat them as "no data",
    /// never as empty-but-successful searches with invented content.
    /// </summary>
    public static bool IsChallengePage(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return false;
        return html.Contains("challenge-form", StringComparison.OrdinalIgnoreCase)
            || html.Contains("anomaly-modal", StringComparison.OrdinalIgnoreCase)
            || html.Contains("anomaly.js", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Parse a DDG html-endpoint response into search hits.
    /// Returns only what is actually present in the document — never pads results.
    /// </summary>
    /// <param name="html">Raw HTML from html.duckduckgo.com/html/?q=... (or lite endpoint).</param>
    /// <param name="maxResults">Upper bound of hits to return.</param>
    public static IReadOnlyList<WebSearchResult> Parse(string html, int maxResults)
    {
        if (string.IsNullOrWhiteSpace(html) || maxResults <= 0) return Array.Empty<WebSearchResult>();
        if (IsChallengePage(html)) return Array.Empty<WebSearchResult>();

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var results = new List<WebSearchResult>();

        // Primary layout: result__a title links.
        var titleNodes = doc.DocumentNode.SelectNodes("//a[contains(concat(' ', normalize-space(@class), ' '), ' result__a ')]");
        if (titleNodes is not null)
        {
            foreach (var a in titleNodes)
            {
                if (results.Count >= maxResults) break;
                var href = a.GetAttributeValue("href", string.Empty);
                var url = ResolveRedirectUrl(href);
                if (string.IsNullOrEmpty(url)) continue;
                var title = HttpUtility.HtmlDecode(a.InnerText ?? string.Empty).Trim();
                if (string.IsNullOrEmpty(title)) continue;
                var snippet = FindSnippetFor(a);
                results.Add(new WebSearchResult(title, url, snippet, SourceName));
            }
        }

        // Lite layout fallback: result-link anchors + result-snippet cells.
        if (results.Count == 0)
        {
            var liteLinks = doc.DocumentNode.SelectNodes("//a[contains(concat(' ', normalize-space(@class), ' '), ' result-link ')]");
            var liteSnippets = doc.DocumentNode.SelectNodes("//td[contains(concat(' ', normalize-space(@class), ' '), ' result-snippet ')]");
            if (liteLinks is not null)
            {
                var snippetList = liteSnippets?.Select(n => HttpUtility.HtmlDecode(n.InnerText ?? string.Empty).Trim()).ToList()
                                  ?? new List<string>();
                var i = 0;
                foreach (var a in liteLinks)
                {
                    if (results.Count >= maxResults) break;
                    var href = a.GetAttributeValue("href", string.Empty);
                    var url = ResolveRedirectUrl(href) ?? AbsoluteOrEmpty(href);
                    if (string.IsNullOrEmpty(url)) continue;
                    var title = HttpUtility.HtmlDecode(a.InnerText ?? string.Empty).Trim();
                    if (string.IsNullOrEmpty(title)) continue;
                    var snippet = i < snippetList.Count ? snippetList[i] : string.Empty;
                    results.Add(new WebSearchResult(title, url, snippet, SourceName));
                    i++;
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Resolve a DDG redirect link ("//duckduckgo.com/l/?uddg=&lt;encoded&gt;...") to the real
    /// destination URL. Returns null when the href is not a uddg redirect link.
    /// </summary>
    public static string? ResolveRedirectUrl(string href)
    {
        if (string.IsNullOrWhiteSpace(href)) return null;
        var qIndex = href.IndexOf('?');
        if (qIndex < 0) return null;
        var query = href[(qIndex + 1)..];
        var pairs = HttpUtility.ParseQueryString(query);
        var uddg = pairs["uddg"];
        if (string.IsNullOrWhiteSpace(uddg)) return null;
        try
        {
            var decoded = Uri.UnescapeDataString(uddg);
            return Uri.TryCreate(decoded, UriKind.Absolute, out var uri) ? uri.ToString() : null;
        }
        catch
        {
            return null;
        }
    }

    private static string FindSnippetFor(HtmlNode titleAnchor)
    {
        // Walk up to the enclosing result body, then look for the snippet anchor inside it.
        var node = titleAnchor.ParentNode;
        while (node is not null && node.Name != "body")
        {
            var cls = node.GetAttributeValue("class", string.Empty);
            if (cls.Contains("result__body", StringComparison.OrdinalIgnoreCase)
                || (cls.Contains("result", StringComparison.OrdinalIgnoreCase) && node.Name == "div"))
            {
                var snip = node.SelectSingleNode(".//*[contains(concat(' ', normalize-space(@class), ' '), ' result__snippet ')]");
                if (snip is not null)
                    return HttpUtility.HtmlDecode(snip.InnerText ?? string.Empty).Trim();
            }
            node = node.ParentNode;
        }
        return string.Empty;
    }

    private static string AbsoluteOrEmpty(string href)
    {
        if (string.IsNullOrWhiteSpace(href)) return string.Empty;
        if (href.StartsWith("//")) href = "https:" + href;
        return Uri.TryCreate(href, UriKind.Absolute, out var uri) ? uri.ToString() : string.Empty;
    }
}
