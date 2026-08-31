// Copyright (c) AeroCode
// WebToolbox — web_search / web_fetch 工具域（批次 B G1）。
// web_search 复用 Skills/Research 的真实检索栈（SearchService 聚合 DuckDuckGo HTML
// 零 key 后端 + Bing/Tavily 有 key 后端），web_fetch 做真实 HTTP GET 并复用
// WebResearchSkill.ExtractText 的正文抽取。零 mock：失败/超时/非白名单 URL
// 一律以 ToolInvokeResult.Fail 如实交还模型。
using System;
using System.Text;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AeroCode.AI.Models;
using AeroCode.Skills.Bundled.Research;
using AeroCode.Skills.Research;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AeroAgent.Moa.Tools;

/// <summary>
/// 检索工具域。URL 白名单校验：仅 http/https 绝对地址（file/ftp/javascript/data
/// 等一律拒绝）；解析上限：<see cref="HardMaxChars"/> 字符封顶，防止单次调用
/// 拖爆上下文。域内失败不抛异常（ToolboxRegistry 契约）。
/// </summary>
public sealed class WebToolbox : IWorkerToolbox
{
    /// <summary>web_search 默认/硬上限结果数。</summary>
    public const int DefaultMaxResults = 5;
    public const int HardMaxResults = 10;

    /// <summary>web_fetch 默认/硬上限正文抽取字符数。</summary>
    public const int DefaultMaxChars = 4000;
    public const int HardMaxChars = 20000;

    /// <summary>web_fetch 单次请求超时。</summary>
    public static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(15);

    private static readonly string[] AllowedSchemes = { "http", "https" };

    private readonly SearchService _search;
    private readonly HttpClient _http;
    private readonly ILogger<WebToolbox> _logger;
    private readonly IReadOnlyList<ToolDefinition> _definitions;

    /// <summary>
    /// 构造。缺省用真实默认检索栈与真实 HttpClient；测试可注入自定义 handler
    /// （本地 HttpListener 端到端）或自定义 <see cref="SearchService"/>。
    /// </summary>
    public WebToolbox(
        SearchService? search = null,
        HttpMessageHandler? handler = null,
        ILogger<WebToolbox>? logger = null)
    {
        _logger = logger ?? NullLogger<WebToolbox>.Instance;
        _search = search ?? SearchService.CreateDefault();
        if (handler is null)
        {
            _http = new HttpClient();
        }
        else
        {
            _http = new HttpClient(handler, disposeHandler: true);
        }

        _http.Timeout = FetchTimeout;
        // 与 DuckDuckGoHtmlProvider 同口径的浏览器 UA：多数站点对默认 dotnet UA 返回 403。
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36");
        _definitions = BuildDefinitions();
    }

    /// <inheritdoc/>
    public string Domain => "web";

    /// <inheritdoc/>
    public IReadOnlyList<ToolDefinition> Definitions => _definitions;

    /// <inheritdoc/>
    public async Task<ToolInvokeResult> InvokeAsync(string toolName, string argumentsJson, CancellationToken ct)
    {
        try
        {
            using var doc = string.IsNullOrWhiteSpace(argumentsJson)
                ? JsonDocument.Parse("{}")
                : JsonDocument.Parse(argumentsJson);
            var args = doc.RootElement.ValueKind == JsonValueKind.Object
                ? doc.RootElement
                : throw new ArgumentException("arguments must be a JSON object");

            return toolName switch
            {
                "web_search" => await SearchAsync(args, ct).ConfigureAwait(false),
                "web_fetch" => await FetchAsync(args, ct).ConfigureAwait(false),
                _ => ToolInvokeResult.Fail($"Unknown web tool '{toolName}'"),
            };
        }
        catch (JsonException ex)
        {
            return ToolInvokeResult.Fail($"Invalid arguments JSON: {ex.Message}");
        }
        catch (ArgumentException ex)
        {
            return ToolInvokeResult.Fail(ex.Message);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or UriFormatException or InvalidOperationException)
        {
            _logger.LogWarning("[DEGRADED] web tool '{Tool}' failed: {Error}", toolName, ex.Message);
            return ToolInvokeResult.Fail($"web tool '{toolName}' failed: {ex.Message}");
        }
    }

    // ---------- web_search ----------

    private async Task<ToolInvokeResult> SearchAsync(JsonElement args, CancellationToken ct)
    {
        var query = args.TryGetProperty("query", out var q) && q.ValueKind == JsonValueKind.String
            ? q.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(query))
        {
            return ToolInvokeResult.Fail("web_search requires a non-empty 'query' string argument");
        }

        var maxResults = DefaultMaxResults;
        if (args.TryGetProperty("max_results", out var mr) && mr.ValueKind == JsonValueKind.Number)
        {
            maxResults = mr.GetInt32();
        }

        maxResults = Math.Clamp(maxResults, 1, HardMaxResults);

        // 真实检索：SearchService 聚合后端、聚合超时（默认 20s）、单后端失败隔离。
        var hits = await _search.SearchAsync(query, maxResults, ct).ConfigureAwait(false);
        if (hits.Count == 0)
        {
            // 诚实空结果：可能真无结果，也可能后端被 bot-challenge/限流——不伪造。
            return ToolInvokeResult.Ok(
                $"No results for '{query}'. (All real backends returned nothing — possibly blocked, rate-limited, or genuinely no hits.)");
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Search results for '{query}' ({hits.Count}):");
        for (var i = 0; i < hits.Count; i++)
        {
            sb.AppendLine($"{i + 1}. {hits[i].Title}");
            sb.AppendLine($"   URL: {hits[i].Url}");
            if (!string.IsNullOrEmpty(hits[i].Snippet))
            {
                sb.AppendLine($"   {hits[i].Snippet}");
            }

            sb.AppendLine($"   Source: {hits[i].Source}");
        }

        return ToolInvokeResult.Ok(sb.ToString().TrimEnd());
    }

    // ---------- web_fetch ----------

    private async Task<ToolInvokeResult> FetchAsync(JsonElement args, CancellationToken ct)
    {
        var url = args.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String
            ? u.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(url))
        {
            return ToolInvokeResult.Fail("web_fetch requires a non-empty 'url' string argument");
        }

        // ---- URL 白名单：仅 http/https 绝对地址 ----
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) ||
            Array.IndexOf(AllowedSchemes, uri.Scheme) < 0)
        {
            return ToolInvokeResult.Fail(
                $"URL not allowed: '{url}' (whitelist: absolute http/https only)");
        }

        var maxChars = DefaultMaxChars;
        if (args.TryGetProperty("max_chars", out var mc) && mc.ValueKind == JsonValueKind.Number)
        {
            maxChars = mc.GetInt32();
        }

        maxChars = Math.Clamp(maxChars, 1, HardMaxChars);

        using var perCallCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        perCallCts.CancelAfter(FetchTimeout);

        using var response = await _http.GetAsync(uri, perCallCts.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return ToolInvokeResult.Fail(
                $"HTTP {(int)response.StatusCode} ({response.StatusCode}) fetching {uri}");
        }

        var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        var content = await response.Content.ReadAsStringAsync(perCallCts.Token).ConfigureAwait(false);

        // HTML/XML 交给既有真实抽取器（去 script/style/nav 等，合并空白）；纯文本原样。
        var text = contentType.Contains("html", StringComparison.OrdinalIgnoreCase) ||
                   contentType.Contains("xml", StringComparison.OrdinalIgnoreCase) || IsProbablyHtml(content)
            ? WebResearchSkill.ExtractText(content)
            : content;

        if (string.IsNullOrWhiteSpace(text))
        {
            return ToolInvokeResult.Fail($"Fetched {uri} but extracted no readable text ({contentType}).");
        }

        text = text.Trim();
        if (text.Length > maxChars)
        {
            text = string.Concat(text.AsSpan(0, maxChars), "…（已截断）");
        }

        return ToolInvokeResult.Ok($"[{uri}]\n{text}");
    }

    private static bool IsProbablyHtml(string content)
    {
        // Content-Type 缺失时按内容嗅探（doctype/常见标签），避免把 HTML 页当纯文本倾倒。
        var head = content.AsSpan(0, Math.Min(content.Length, 512));
        return head.IndexOf("<!doctype html", StringComparison.OrdinalIgnoreCase) >= 0 ||
               head.IndexOf("<html", StringComparison.OrdinalIgnoreCase) >= 0 ||
               head.IndexOf("<head", StringComparison.OrdinalIgnoreCase) >= 0 ||
               head.IndexOf("<body", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static IReadOnlyList<ToolDefinition> BuildDefinitions() => new List<ToolDefinition>
    {
        new()
        {
            Name = "web_search",
            Description = "Search the open web (real engines: DuckDuckGo HTML key-free; Bing/Tavily when API keys configured). " +
                          "Args: {\"query\": string (required), \"max_results\": int (1-10, default 5)}.",
            ParametersJsonSchema = """{"type":"object","properties":{"query":{"type":"string"},"max_results":{"type":"integer"}},"required":["query"]}""",
        },
        new()
        {
            Name = "web_fetch",
            Description = "Fetch one URL over HTTP(S) and return its extracted readable text (real network). " +
                          "Args: {\"url\": string (required, absolute http/https only), \"max_chars\": int (1-20000, default 4000)}.",
            ParametersJsonSchema = """{"type":"object","properties":{"url":{"type":"string"},"max_chars":{"type":"integer"}},"required":["url"]}""",
        },
    };
}
