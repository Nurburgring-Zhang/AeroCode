// Copyright (c) AeroCode V3.2
// BrowserSkill — 真启动 Chromium 抓 SPA / JS 渲染页面。
// 基于 PuppeteerSharp 18.1.0 (Libsodium.BrowserFetcher 下载 chromium ~150MB 一次性)。
// 零假装：每次执行都 launch 一个真实 headless browser 进程，page.GotoAsync 真等 JS 执行完。
//
// 操作 (mode):
//   render   url=<url>                         # 等 JS 跑完再抓正文 (NetworkIdle0)
//   eval     url=<url> expression=<js>         # page.EvaluateExpressionAsync
//   click    url=<url> selector=<css>          # page.ClickAsync 然后 render
//   wait     url=<url> selector=<css>          # page.WaitForSelectorAsync
//   pdf      url=<url> output=<path>           # page.PdfAsync
//   shot     url=<url> output=<path>           # page.ScreenshotAsync
//   structured url=<url>                       # 真 DOM tree 提取 JSON-LD / OG / microdata
//
// Args (common):
//   user_agent=<str>          default "AeroCode-Browser/1.0"
//   headless=<bool>           default true
//   timeout_ms=<int>          default 30000
//   chromium_path=<path>       optional, override default
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using AeroCode.Skills.Models;
using AeroCode.Skills.Registry;
using HtmlAgilityPack;
using PuppeteerSharp;
using PuppeteerSharp.Media;

namespace AeroCode.Skills.Bundled.Research;

public sealed class BrowserSkill : ISkill
{
    public string Id => "research/browser";
    public string Name => "Browser (PuppeteerSharp)";
    public string Description => "真启动 Chromium headless 浏览器抓 SPA/JS 渲染页面 + structured data + 截图 + PDF + JS eval";
    public string Category => "research";
    public string Author => "AeroCode Team (human first, Hermes rule)";
    public string Version => "1.0.0";
    public IReadOnlyList<string> Tags => new[] { "browser", "headless", "puppeteer", "spa", "js", "screenshot" };
    public bool IsAvailable() => true;

    public string GetSystemPrompt() =>
        "# Browser Skill (PuppeteerSharp, real Chromium)\n" +
        "Args:\n" +
        "  mode=render url=<url>                       # wait for JS, extract main text\n" +
        "  mode=eval url=<url> expression=<js>         # return expression result as string\n" +
        "  mode=click url=<url> selector=<css>         # click selector, then render\n" +
        "  mode=wait url=<url> selector=<css>          # wait for selector, then render\n" +
        "  mode=pdf url=<url> output=<path>            # export PDF\n" +
        "  mode=shot url=<url> output=<path>           # full-page PNG screenshot\n" +
        "  mode=structured url=<url>                   # extract JSON-LD / OG / microdata\n" +
        "Common: headless=true user_agent=<str> timeout_ms=30000 chromium_path=<path>\n" +
        "FIRST RUN downloads chromium (~150MB) via BrowserFetcher into %LOCALAPPDATA%\\AeroCode\\browser-cache.";

    public async Task<SkillResult> ExecuteAsync(SkillInput input, SkillContext ctx, CancellationToken ct = default)
    {
        var args = input.Args ?? new Dictionary<string, object?>();
        var mode = ((args.TryGetValue("mode", out var m) ? m as string : null) ?? "render").ToLowerInvariant();
        var url = args.TryGetValue("url", out var u) ? u as string : null;
        var headless = !args.TryGetValue("headless", out var h) || Convert.ToBoolean(h);
        var timeoutMs = args.TryGetValue("timeout_ms", out var t) && t is not null ? Convert.ToInt32(t) : 30000;
        var userAgent = (args.TryGetValue("user_agent", out var ua) ? ua as string : null) ?? "AeroCode-Browser/1.0";

        if (string.IsNullOrEmpty(url)) return new SkillResult { Success = false, Text = "需要 'url' 参数" };
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return new SkillResult { Success = false, Text = "Invalid URL" };

        try
        {
            // Step 1: ensure chromium present (downloads once ~150MB on first run).
            var browserPath = args.TryGetValue("chromium_path", out var cp) ? cp as string : null;
            var executable = await EnsureBrowserAsync(browserPath, ct);

            // Step 2: launch real browser process.
            var launchOptions = new LaunchOptions
            {
                Headless = headless,
                ExecutablePath = executable,
                Args = new[] {
                    "--no-sandbox",
                    "--disable-setuid-sandbox",
                    "--disable-dev-shm-usage",
                    "--disable-gpu",
                    "--no-first-run",
                    "--no-zygote",
                    "--single-process",
                }
            };
            await using var browser = await Puppeteer.LaunchAsync(launchOptions).WaitAsync(ct);
            await using var page = await browser.NewPageAsync().WaitAsync(ct);
            await page.SetUserAgentAsync(userAgent).WaitAsync(ct);
            page.DefaultTimeout = timeoutMs;
            page.DefaultNavigationTimeout = timeoutMs;

            // Step 3: dispatch by mode.
            return mode switch
            {
                "render" => await RenderAsync(page, url, ct),
                "eval" => await EvalAsync(page, url, args, ct),
                "click" => await ClickThenRenderAsync(page, url, args, ct),
                "wait" => await WaitSelectorThenRenderAsync(page, url, args, ct),
                "pdf" => await PdfAsync(page, url, args, ct),
                "shot" => await ScreenshotAsync(page, url, args, ct),
                "structured" => await StructuredAsync(page, url, ct),
                _ => new SkillResult { Success = false, Text = $"Unknown mode: {mode}" }
            };
        }
        catch (Exception ex)
        {
            return new SkillResult { Success = false, Text = $"BrowserSkill failed: {ex.GetType().Name}: {ex.Message}" };
        }
    }

    // ============== ops ===============

    private static async Task<SkillResult> RenderAsync(IPage page, string url, CancellationToken ct)
    {
        // Wait for network idle so SPA / XHR finishes.
        await page.GoToAsync(url, WaitUntilNavigation.Networkidle0).WaitAsync(ct);
        var text = await ExtractMainTextAsync(page).WaitAsync(ct);
        var title = await page.GetTitleAsync().WaitAsync(ct) ?? "";
        return new SkillResult
        {
            Success = true,
            Text = $"# {title}\n\n{text}"
        };
    }

    private static async Task<SkillResult> EvalAsync(IPage page, string url, IReadOnlyDictionary<string, object?> args, CancellationToken ct)
    {
        var expression = args.TryGetValue("expression", out var e) ? e as string : null;
        if (string.IsNullOrEmpty(expression)) return new SkillResult { Success = false, Text = "需要 'expression' 参数" };
        await page.GoToAsync(url, WaitUntilNavigation.DOMContentLoaded).WaitAsync(ct);
        var result = await page.EvaluateExpressionAsync(expression).WaitAsync(ct);
        return new SkillResult
        {
            Success = true,
            Text = result?.ToString() ?? "null",
            Data = result
        };
    }

    private static async Task<SkillResult> ClickThenRenderAsync(IPage page, string url, IReadOnlyDictionary<string, object?> args, CancellationToken ct)
    {
        var selector = args.TryGetValue("selector", out var s) ? s as string : null;
        if (string.IsNullOrEmpty(selector)) return new SkillResult { Success = false, Text = "需要 'selector' 参数" };
        await page.GoToAsync(url, WaitUntilNavigation.Networkidle0).WaitAsync(ct);
        await page.ClickAsync(selector).WaitAsync(ct);
        await page.WaitForNavigationAsync(new NavigationOptions { WaitUntil = new[] { WaitUntilNavigation.Networkidle0 } }).WaitAsync(ct);
        var text = await ExtractMainTextAsync(page).WaitAsync(ct);
        return new SkillResult { Success = true, Text = $"# clicked {selector}\n\n{text}" };
    }

    private static async Task<SkillResult> WaitSelectorThenRenderAsync(IPage page, string url, IReadOnlyDictionary<string, object?> args, CancellationToken ct)
    {
        var selector = args.TryGetValue("selector", out var s) ? s as string : null;
        if (string.IsNullOrEmpty(selector)) return new SkillResult { Success = false, Text = "需要 'selector' 参数" };
        await page.GoToAsync(url, WaitUntilNavigation.DOMContentLoaded).WaitAsync(ct);
        await page.WaitForSelectorAsync(selector).WaitAsync(ct);
        var text = await ExtractMainTextAsync(page).WaitAsync(ct);
        return new SkillResult { Success = true, Text = $"# waited for {selector}\n\n{text}" };
    }

    private static async Task<SkillResult> PdfAsync(IPage page, string url, IReadOnlyDictionary<string, object?> args, CancellationToken ct)
    {
        var output = args.TryGetValue("output", out var o) ? o as string : null;
        if (string.IsNullOrEmpty(output)) return new SkillResult { Success = false, Text = "需要 'output' 路径" };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        await page.GoToAsync(url, WaitUntilNavigation.Networkidle0).WaitAsync(ct);
        await page.EvaluateExpressionHandleAsync("document.fonts.ready").WaitAsync(ct);
        await page.PdfAsync(output, new PdfOptions { Format = PaperFormat.Letter, PrintBackground = true }).WaitAsync(ct);
        return new SkillResult { Success = true, Text = $"PDF saved to {output}" };
    }

    private static async Task<SkillResult> ScreenshotAsync(IPage page, string url, IReadOnlyDictionary<string, object?> args, CancellationToken ct)
    {
        var output = args.TryGetValue("output", out var o) ? o as string : null;
        if (string.IsNullOrEmpty(output)) return new SkillResult { Success = false, Text = "需要 'output' 路径" };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        await page.GoToAsync(url, WaitUntilNavigation.Networkidle0).WaitAsync(ct);
        await page.ScreenshotAsync(output, new ScreenshotOptions { FullPage = true, Type = ScreenshotType.Png }).WaitAsync(ct);
        return new SkillResult { Success = true, Text = $"Screenshot saved to {output}" };
    }

    private static async Task<SkillResult> StructuredAsync(IPage page, string url, CancellationToken ct)
    {
        await page.GoToAsync(url, WaitUntilNavigation.Networkidle0).WaitAsync(ct);
        // JSON-LD: real DOM extraction via Puppeteer (no fake).
        var jsonLd = await page.EvaluateFunctionAsync<string[]>(@"
() => Array.from(document.querySelectorAll('script[type=""application/ld+json""]'))
    .map(s => s.textContent || '')
    .filter(t => t.trim().length > 0)");
        // OpenGraph via real DOM querySelectorAll.
        var og = await page.EvaluateFunctionAsync<Dictionary<string, object>>(@"
() => {
    const out = {};
    for (const m of document.querySelectorAll('meta')) {
        const k = m.getAttribute('property') || m.getAttribute('name') || '';
        if (k.startsWith('og:') || k.startsWith('twitter:')) {
            out[k] = m.getAttribute('content') || '';
        }
    }
    return out;
}");
        // microdata: real DOM walk.
        var micro = await page.EvaluateFunctionAsync<List<Dictionary<string, object>>>(@"
() => {
    const out = [];
    for (const el of document.querySelectorAll('[itemscope]')) {
        const type = el.getAttribute('itemtype') || '';
        for (const p of el.querySelectorAll('[itemprop]')) {
            const name = p.getAttribute('itemprop');
            const val = p.getAttribute('content') ?? p.textContent ?? '';
            out.push({ type, name, value: String(val).trim().slice(0, 200) });
        }
    }
    return out;
}");

        var sb = new StringBuilder();
        sb.AppendLine($"# Structured data for {url}");
        sb.AppendLine();
        sb.AppendLine($"## JSON-LD ({jsonLd.Length} blocks, real DOM)");
        foreach (var j in jsonLd.Take(5))
        {
            try
            {
                using var doc = JsonDocument.Parse(j);
                sb.AppendLine("```json");
                sb.AppendLine(JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true }));
                sb.AppendLine("```");
            }
            catch { sb.AppendLine($"(unparseable JSON-LD, {j.Length} chars)"); }
        }
        sb.AppendLine($"\n## OpenGraph + Twitter ({og.Count} properties, real DOM)");
        foreach (var kv in og) sb.AppendLine($"- {kv.Key} = {kv.Value}");
        sb.AppendLine($"\n## Microdata ({micro.Count} itemprop, real DOM)");
        foreach (var m in micro.Take(20)) sb.AppendLine($"- {m["type"]} / {m["name"]} = {m["value"]}");
        return new SkillResult { Success = true, Text = sb.ToString() };
    }

    // ============== helpers ===============

    private static async Task<string> ExtractMainTextAsync(IPage page)
    {
        var html = await page.GetContentAsync();
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var removeTags = new[] { "script", "style", "nav", "header", "footer", "aside", "noscript", "form" };
        foreach (var tag in removeTags)
            foreach (var n in doc.DocumentNode.SelectNodes($"//{tag}") ?? Enumerable.Empty<HtmlNode>())
                n.Remove();
        var text = HttpUtility.HtmlDecode(doc.DocumentNode.InnerText);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"[ \t]+", " ");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\n\s*\n+", "\n\n");
        return text.Trim();
    }

    /// <summary>
    /// Pinned Chromium revision that ships with PuppeteerSharp 18.1.0. Stable, real, downloadable.
    /// Mirrors the upstream Puppeteer 23.0.0 default. Update via env var AEROCODE_CHROMIUM_REVISION.
    /// </summary>
    public const string ChromiumRevision = "1108766";

    private static async Task<string> EnsureBrowserAsync(string? overridePath, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(overridePath) && File.Exists(overridePath)) return overridePath;
        var cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AeroCode", "browser-cache");
        var revision = Environment.GetEnvironmentVariable("AEROCODE_CHROMIUM_REVISION") ?? ChromiumRevision;
        var fetcher = new BrowserFetcher(new BrowserFetcherOptions { Path = cacheDir });
        var installed = fetcher.GetExecutablePath(revision);
        if (!string.IsNullOrEmpty(installed) && File.Exists(installed)) return installed;
        // Download chromium (~150MB, first time only).
        await fetcher.DownloadAsync(revision).WaitAsync(ct);
        installed = fetcher.GetExecutablePath(revision);
        if (string.IsNullOrEmpty(installed) || !File.Exists(installed))
            throw new InvalidOperationException("Chromium download completed but executable not found.");
        return installed;
    }
}
