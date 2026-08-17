// Copyright (c) AeroCode V3.0
// WebResearchSkill — deep web research + scraping + structured extraction.
// Real HTTP via HttpClient + HTML parsing via HtmlAgilityPack. No mocks.
//
// Operations (selected via `mode` arg):
//   fetch       — single URL → cleaned text
//   search      — find links matching query in base_url, fetch top-1
//   crawl       — fetch URL + follow N internal links, all in parallel
//   sitemap     — parse <sitemap>/<urlset> XML, optionally fetch all <loc>
//   robots      — fetch /robots.txt, return Disallow/Allow rules
//   structured  — extract JSON-LD / microdata / OpenGraph from page
//   summary     — fetch + extract first N sentences as a quick summary
//
// Extra args (all ops):
//   url=<absolute-url>
//   base_url=<page>
//   query=<kw1 kw2 ...>
//   max_chars=<int>            default 8000
//   max_concurrency=<int>      default 4
//   max_pages=<int>            default 10 (for crawl/sitemap)
//   respect_robots=<bool>      default true
//   user_agent=<str>           default "AeroCode-WebResearch/1.0"
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Xml;
using AeroCode.Skills.Models;
using AeroCode.Skills.Registry;
using HtmlAgilityPack;

namespace AeroCode.Skills.Bundled.Research;

public sealed class WebResearchSkill : ISkill
{
    public string Id => "research/web_research";
    public string Name => "Web Research";
    public string Description => "深度 web 检索 + 抓取 + 结构化提取 (JSON-LD/微数据/sitemap/robots/并发爬取)";
    public string Category => "research";
    public string Author => "AeroCode Team (human first, Hermes rule)";
    public string Version => "2.0.0";
    public IReadOnlyList<string> Tags => new[] { "web", "http", "scrape", "research", "sitemap", "json-ld" };
    public bool IsAvailable() => true;

    public string GetSystemPrompt() =>
        "# Web Research Skill v2 (deep)\n" +
        "Real HTTP scraping + structured extraction. Operations:\n" +
        "  mode=fetch url=<url>               # single URL → cleaned text\n" +
        "  mode=search base_url=<page> query=<kw>  # find link, fetch top-1\n" +
        "  mode=crawl url=<root> max_pages=<N> max_concurrency=<C>   # follow internal links\n" +
        "  mode=sitemap url=<sitemap.xml> max_pages=<N>  # parse & fetch\n" +
        "  mode=robots url=<site-root>        # return robots.txt Disallow/Allow\n" +
        "  mode=structured url=<url>          # extract JSON-LD / microdata / OpenGraph\n" +
        "  mode=summary url=<url> sentences=<N>  # quick top-N sentence summary\n" +
        "Other args: max_chars, user_agent, respect_robots\n" +
        "Always cite source URLs. Be respectful: respect_robots=true is the default.";

    private static readonly HttpClient SharedHttp = new()
    {
        Timeout = TimeSpan.FromSeconds(60),
        DefaultRequestHeaders = { { "User-Agent", "AeroCode-WebResearch/2.0" } }
    };

    // Robots cache: host → Disallow patterns. Per-process, no persistence (good enough for short-lived research).
    private static readonly ConcurrentDictionary<string, HashSet<string>> RobotsDisallowCache = new();

    public async Task<SkillResult> ExecuteAsync(SkillInput input, SkillContext ctx, CancellationToken ct = default)
    {
        var args = input.Args ?? new Dictionary<string, object?>();
        var mode = ((args.TryGetValue("mode", out var m) ? m as string : null) ?? "fetch").ToLowerInvariant();
        var maxChars = args.TryGetValue("max_chars", out var mc) && mc is not null ? Convert.ToInt32(mc) : 8000;
        var maxConcurrency = args.TryGetValue("max_concurrency", out var mcc) && mcc is not null ? Math.Max(1, Convert.ToInt32(mcc)) : 4;
        var maxPages = args.TryGetValue("max_pages", out var mp) && mp is not null ? Math.Max(1, Convert.ToInt32(mp)) : 10;
        var respectRobots = !args.TryGetValue("respect_robots", out var rr) || Convert.ToBoolean(rr);
        var userAgent = (args.TryGetValue("user_agent", out var ua) ? ua as string : null) ?? "AeroCode-WebResearch/2.0";

        var url = args.TryGetValue("url", out var u) ? u as string : null;
        var query = args.TryGetValue("query", out var q) ? q as string : null;
        var baseUrl = args.TryGetValue("base_url", out var bu) ? bu as string : null;

        // Override User-Agent for this call
        var prevUA = SharedHttp.DefaultRequestHeaders.UserAgent.ToString();
        try
        {
            if (prevUA != userAgent)
            {
                SharedHttp.DefaultRequestHeaders.UserAgent.Clear();
                SharedHttp.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
            }

            return mode switch
            {
                "fetch" => await Single(url, maxChars, respectRobots, ct),
                "search" => await Search(baseUrl, query, maxChars, respectRobots, ct),
                "crawl" => await CrawlAsync(url, maxPages, maxConcurrency, maxChars, respectRobots, ct),
                "sitemap" => await SitemapAsync(url, maxPages, maxConcurrency, maxChars, respectRobots, ct),
                "robots" => Robots(url),
                "structured" => await StructuredAsync(url, maxChars, respectRobots, ct),
                "summary" => await SummaryAsync(url, args, maxChars, respectRobots, ct),
                _ => new SkillResult { Success = false, Text = $"Unknown mode: {mode}" }
            };
        }
        catch (Exception ex)
        {
            return new SkillResult { Success = false, Text = $"WebResearch failed: {ex.GetType().Name}: {ex.Message}" };
        }
        finally
        {
            if (prevUA != userAgent)
            {
                SharedHttp.DefaultRequestHeaders.UserAgent.Clear();
                SharedHttp.DefaultRequestHeaders.UserAgent.ParseAdd(prevUA);
            }
        }
    }

    // ============== ops ===============

    private static async Task<SkillResult> Single(string? url, int maxChars, bool respectRobots, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(url)) return new SkillResult { Success = false, Text = "需要 'url' 参数" };
        if (!IsAllowed(url, respectRobots)) return new SkillResult { Success = false, Text = $"Disallowed by robots.txt: {url}" };
        var text = await FetchAndExtractAsync(url, ct);
        return new SkillResult { Success = true, Text = $"# {url}\n\n{Truncate(text, maxChars)}" };
    }

    private static async Task<SkillResult> Search(string? baseUrl, string? query, int maxChars, bool respectRobots, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(query))
            return new SkillResult { Success = false, Text = "search 模式需要 base_url + query" };
        var links = await FindLinksAsync(baseUrl!, query, respectRobots, ct);
        if (links.Count == 0)
            return new SkillResult { Success = false, Text = $"在 {baseUrl} 中未找到含 \"{query}\" 的链接" };
        var first = links[0];
        var text = await FetchAndExtractAsync(first, ct);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# 搜索结果: 在 {baseUrl} 中找到 {links.Count} 个匹配链接");
        sb.AppendLine($"## 主结果: {first}");
        sb.AppendLine();
        sb.AppendLine(Truncate(text, maxChars));
        if (links.Count > 1)
        {
            sb.AppendLine();
            sb.AppendLine("## 其他候选链接:");
            foreach (var l in links.Skip(1).Take(5)) sb.AppendLine($"- {l}");
        }
        return new SkillResult { Success = true, Text = sb.ToString() };
    }

    private static async Task<SkillResult> CrawlAsync(string? root, int maxPages, int maxConcurrency, int maxChars, bool respectRobots, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(root)) return new SkillResult { Success = false, Text = "crawl 模式需要 url=<root>" };
        if (!IsAllowed(root, respectRobots)) return new SkillResult { Success = false, Text = $"Disallowed by robots.txt: {root}" };
        Uri.TryCreate(root, UriKind.Absolute, out var rootUri);
        if (rootUri is null) return new SkillResult { Success = false, Text = "Invalid URL" };

        var seen = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var queue = new ConcurrentQueue<string>();
        queue.Enqueue(root);
        seen[root] = 1;

        var results = new ConcurrentBag<(string url, string text)>();
        var sem = new SemaphoreSlim(maxConcurrency);

        var tasks = new List<Task>();
        while (!queue.IsEmpty && results.Count < maxPages)
        {
            if (!queue.TryDequeue(out var current)) break;
            if (!IsAllowed(current, respectRobots)) continue;
            await sem.WaitAsync(ct);
            var t = Task.Run(async () =>
            {
                try
                {
                    var html = await SharedHttp.GetStringAsync(current, ct);
                    var text = ExtractText(html);
                    results.Add((current, text));
                    if (results.Count >= maxPages) return;
                    // Enqueue internal links (BFS)
                    var doc = new HtmlDocument();
                    doc.LoadHtml(html);
                    foreach (var a in doc.DocumentNode.SelectNodes("//a[@href]") ?? Enumerable.Empty<HtmlNode>())
                    {
                        if (results.Count >= maxPages) return;
                        var href = a.GetAttributeValue("href", "");
                        if (string.IsNullOrEmpty(href) || href.StartsWith("#") || href.StartsWith("javascript:")) continue;
                        string? abs = null;
                        if (Uri.TryCreate(href, UriKind.Absolute, out var absUri)) abs = absUri.ToString();
                        else if (Uri.TryCreate(rootUri, href, out var rel)) abs = rel.ToString();
                        if (abs is null) continue;
                        // Only same-origin (internal) crawl
                        if (abs.StartsWith(rootUri.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase) &&
                            seen.TryAdd(abs, 1))
                        {
                            queue.Enqueue(abs);
                        }
                    }
                }
                catch { /* skip broken links */ }
                finally { sem.Release(); }
            }, ct);
            tasks.Add(t);
        }
        await Task.WhenAll(tasks);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# Crawl of {root} ({results.Count} pages, max_concurrency={maxConcurrency})");
        foreach (var (u, t) in results.OrderBy(r => r.url))
        {
            sb.AppendLine();
            sb.AppendLine($"## {u}");
            sb.AppendLine(Truncate(t, maxChars / Math.Max(1, results.Count)));
        }
        return new SkillResult { Success = true, Text = sb.ToString() };
    }

    private static async Task<SkillResult> SitemapAsync(string? sitemapUrl, int maxPages, int maxConcurrency, int maxChars, bool respectRobots, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(sitemapUrl)) return new SkillResult { Success = false, Text = "sitemap 模式需要 url=<sitemap.xml>" };
        var urls = await ParseSitemapAsync(sitemapUrl, ct);
        if (urls.Count == 0) return new SkillResult { Success = false, Text = $"No <loc> entries found in {sitemapUrl}" };
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# Sitemap: {sitemapUrl}");
        sb.AppendLine($"- {urls.Count} URLs discovered (will fetch up to {maxPages})");

        var sem = new SemaphoreSlim(maxConcurrency);
        var fetched = new ConcurrentBag<(string url, string text)>();
        var tasks = new List<Task>();
        foreach (var u in urls.Take(maxPages))
        {
            if (!IsAllowed(u, respectRobots)) continue;
            await sem.WaitAsync(ct);
            var t = Task.Run(async () =>
            {
                try
                {
                    var text = await FetchAndExtractAsync(u, ct);
                    fetched.Add((u, text));
                }
                catch { /* skip */ }
                finally { sem.Release(); }
            }, ct);
            tasks.Add(t);
        }
        await Task.WhenAll(tasks);

        foreach (var (u, t) in fetched.OrderBy(r => r.url))
        {
            sb.AppendLine();
            sb.AppendLine($"## {u}");
            sb.AppendLine(Truncate(t, maxChars / Math.Max(1, fetched.Count)));
        }
        if (urls.Count > maxPages)
            sb.AppendLine($"\n*({urls.Count - maxPages} more URLs not fetched, increase max_pages to see all)*");
        return new SkillResult { Success = true, Text = sb.ToString() };
    }

    private static SkillResult Robots(string? siteRoot)
    {
        if (string.IsNullOrEmpty(siteRoot)) return new SkillResult { Success = false, Text = "robots 模式需要 url=<site-root>" };
        if (!Uri.TryCreate(siteRoot, UriKind.Absolute, out var uri)) return new SkillResult { Success = false, Text = "Invalid URL" };
        var robotsUrl = new Uri(uri, "/robots.txt").ToString();
        try
        {
            var text = SharedHttp.GetStringAsync(robotsUrl).GetAwaiter().GetResult();
            var disallow = new List<string>(); var allow = new List<string>();
            var currentUA = "*";
            foreach (var line in text.Split('\n'))
            {
                var l = line.Trim();
                if (l.StartsWith("#") || string.IsNullOrEmpty(l)) continue;
                if (l.StartsWith("User-agent:", StringComparison.OrdinalIgnoreCase))
                    currentUA = l.Substring("User-agent:".Length).Trim();
                else if (l.StartsWith("Disallow:", StringComparison.OrdinalIgnoreCase) && currentUA == "*")
                    disallow.Add(l.Substring("Disallow:".Length).Trim());
                else if (l.StartsWith("Allow:", StringComparison.OrdinalIgnoreCase) && currentUA == "*")
                    allow.Add(l.Substring("Allow:".Length).Trim());
            }
            // Cache for IsAllowed
            RobotsDisallowCache[uri.Host] = new HashSet<string>(disallow.Where(p => !string.IsNullOrEmpty(p)), StringComparer.Ordinal);
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"# robots.txt for {uri.Host}");
            sb.AppendLine($"- Source: {robotsUrl}");
            sb.AppendLine($"- Disallow ({disallow.Count}):");
            foreach (var d in disallow) sb.AppendLine($"  - {d}");
            sb.AppendLine($"- Allow ({allow.Count}):");
            foreach (var a in allow) sb.AppendLine($"  - {a}");
            return new SkillResult { Success = true, Text = sb.ToString() };
        }
        catch (Exception ex)
        {
            return new SkillResult { Success = false, Text = $"Failed to fetch {robotsUrl}: {ex.Message}" };
        }
    }

    private static async Task<SkillResult> StructuredAsync(string? url, int maxChars, bool respectRobots, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(url)) return new SkillResult { Success = false, Text = "structured 模式需要 url" };
        if (!IsAllowed(url, respectRobots)) return new SkillResult { Success = false, Text = $"Disallowed: {url}" };
        var html = await SharedHttp.GetStringAsync(url, ct);
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# Structured data for {url}");

        // 1) JSON-LD
        var jsonLd = new List<string>();
        foreach (var n in doc.DocumentNode.SelectNodes("//script[@type='application/ld+json']") ?? Enumerable.Empty<HtmlNode>())
        {
            var content = HtmlEntity.DeEntitize(n.InnerText);
            if (!string.IsNullOrWhiteSpace(content)) jsonLd.Add(content);
        }
        sb.AppendLine($"\n## JSON-LD ({jsonLd.Count} blocks)");
        foreach (var j in jsonLd.Take(5))
        {
            try
            {
                using var parsed = JsonDocument.Parse(j);
                var pretty = JsonSerializer.Serialize(parsed.RootElement, new JsonSerializerOptions { WriteIndented = true });
                sb.AppendLine("```json");
                sb.AppendLine(Truncate(pretty, maxChars / 4));
                sb.AppendLine("```");
            }
            catch { sb.AppendLine($"(unparseable JSON-LD block, {j.Length} chars)"); }
        }

        // 2) OpenGraph
        var og = new List<(string property, string content)>();
        foreach (var m in doc.DocumentNode.SelectNodes("//meta[starts-with(@property, 'og:')]") ?? Enumerable.Empty<HtmlNode>())
        {
            var p = m.GetAttributeValue("property", "");
            var c = m.GetAttributeValue("content", "");
            if (!string.IsNullOrEmpty(p) && !string.IsNullOrEmpty(c)) og.Add((p, c));
        }
        sb.AppendLine($"\n## OpenGraph ({og.Count} properties)");
        foreach (var (p, c) in og) sb.AppendLine($"- {p} = {Truncate(c, 200)}");

        // 3) microdata (itemtype / itemprop)
        var micro = new List<(string itemtype, string itemprop, string value)>();
        foreach (var el in doc.DocumentNode.SelectNodes("//*[@itemprop and @itemtype]") ?? Enumerable.Empty<HtmlNode>().Take(20))
        {
            var it = el.GetAttributeValue("itemtype", "");
            var ip = el.GetAttributeValue("itemprop", "");
            var v = HtmlEntity.DeEntitize(el.InnerText).Trim();
            if (v.Length > 200) v = v[..200] + "...";
            micro.Add((it, ip, v));
        }
        sb.AppendLine($"\n## Microdata (sample, up to 20)");
        foreach (var m in micro.Take(20)) sb.AppendLine($"- {m.itemtype} / {m.itemprop} = {m.value}");

        return new SkillResult { Success = true, Text = Truncate(sb.ToString(), maxChars) };
    }

    private static async Task<SkillResult> SummaryAsync(string? url, IReadOnlyDictionary<string, object?> args, int maxChars, bool respectRobots, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(url)) return new SkillResult { Success = false, Text = "summary 模式需要 url" };
        if (!IsAllowed(url, respectRobots)) return new SkillResult { Success = false, Text = $"Disallowed: {url}" };
        var sentences = args.TryGetValue("sentences", out var s) && s is not null ? Convert.ToInt32(s) : 5;
        var text = await FetchAndExtractAsync(url, ct);
        // crude sentence splitter
        var parts = Regex.Split(text, @"(?<=[\.!?])\s+").Where(p => !string.IsNullOrWhiteSpace(p)).Take(sentences);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# Summary of {url} (top {sentences} sentences)");
        sb.AppendLine();
        foreach (var p in parts) sb.AppendLine("- " + p.Trim());
        return new SkillResult { Success = true, Text = Truncate(sb.ToString(), maxChars) };
    }

    // ============== helpers ===============

    private static async Task<string> FetchAndExtractAsync(string url, CancellationToken ct)
    {
        using var resp = await SharedHttp.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        var html = await resp.Content.ReadAsStringAsync(ct);
        return ExtractText(html);
    }

    private static async Task<List<string>> FindLinksAsync(string baseUrl, string query, bool respectRobots, CancellationToken ct)
    {
        var html = await SharedHttp.GetStringAsync(baseUrl, ct);
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var matches = new List<(int score, string url)>();
        var qLower = query.ToLowerInvariant();
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri);
        foreach (var a in doc.DocumentNode.SelectNodes("//a[@href]") ?? Enumerable.Empty<HtmlNode>())
        {
            var href = a.GetAttributeValue("href", "");
            var text = HttpUtility.HtmlDecode(a.InnerText ?? "").Trim();
            if (string.IsNullOrEmpty(href) || href.StartsWith("#") || href.StartsWith("javascript:")) continue;
            string? abs = null;
            if (Uri.TryCreate(href, UriKind.Absolute, out var absUri)) abs = absUri.ToString();
            else if (baseUri is not null && Uri.TryCreate(baseUri, href, out var rel)) abs = rel.ToString();
            if (abs is null) continue;
            var combined = (text + " " + abs).ToLowerInvariant();
            var score = 0;
            foreach (var w in qLower.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                if (combined.Contains(w)) score++;
            if (score > 0) matches.Add((score, abs));
        }
        return matches
            .OrderByDescending(m => m.score)
            .Select(m => m.url)
            .Distinct()
            .Take(10)
            .ToList();
    }

    /// <summary>Parse a sitemap.xml — supports both sitemap index and urlset.</summary>
    public static async Task<List<string>> ParseSitemapAsync(string sitemapUrl, CancellationToken ct)
    {
        using var resp = await SharedHttp.GetAsync(sitemapUrl, ct);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        return await ParseSitemapXmlAsync(stream, ct);
    }

    /// <summary>Parse sitemap XML content (test-friendly: no HTTP needed).</summary>
    public static async Task<List<string>> ParseSitemapXmlAsync(Stream xmlStream, CancellationToken ct)
    {
        var urls = new List<string>();
        var settings = new XmlReaderSettings { Async = true, IgnoreWhitespace = true, DtdProcessing = DtdProcessing.Ignore };
        using var reader = XmlReader.Create(xmlStream, settings);
        var inLoc = false;
        while (await reader.ReadAsync())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "loc")
                inLoc = true;
            else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "loc")
                inLoc = false;
            else if (inLoc && reader.NodeType == XmlNodeType.Text)
            {
                var u = reader.Value.Trim();
                if (!string.IsNullOrEmpty(u)) urls.Add(u);
            }
        }
        return urls;
    }

    /// <summary>Parse sitemap XML from a string (test-friendly).</summary>
    public static Task<List<string>> ParseSitemapStringAsync(string xml)
        => ParseSitemapXmlAsync(new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(xml)), CancellationToken.None);

    /// <summary>从 HTML 提取正文,移除 script/style/nav/header/footer/aside/noscript/form,合并空白。</summary>
    public static string ExtractText(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var removeTags = new[] { "script", "style", "nav", "header", "footer", "aside", "noscript", "form" };
        foreach (var tag in removeTags)
            foreach (var n in doc.DocumentNode.SelectNodes($"//{tag}") ?? Enumerable.Empty<HtmlNode>())
                n.Remove();
        foreach (var br in doc.DocumentNode.SelectNodes("//br") ?? Enumerable.Empty<HtmlNode>())
            br.ParentNode.ReplaceChild(HtmlNode.CreateNode("text"), br).InnerHtml = "\n";
        var text = HttpUtility.HtmlDecode(doc.DocumentNode.InnerText);
        text = Regex.Replace(text, @"[ \t]+", " ");
        text = Regex.Replace(text, @"\n\s*\n+", "\n\n");
        return text.Trim();
    }

    private static bool IsAllowed(string url, bool respectRobots)
    {
        if (!respectRobots) return true;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (!RobotsDisallowCache.TryGetValue(uri.Host, out var disallow)) return true; // no data → allow
        var path = uri.AbsolutePath;
        foreach (var d in disallow)
        {
            if (string.IsNullOrEmpty(d)) continue;
            if (path.StartsWith(d, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + $"... [截断, 原文 {s.Length} 字符]";
}
