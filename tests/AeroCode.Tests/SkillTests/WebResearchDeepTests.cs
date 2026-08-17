// Copyright (c) AeroCode V3.0
// WebResearchSkill v2 deep tests (sitemaps, structured, summaries, content extraction)
using System;
using System.Threading;
using System.Threading.Tasks;
using AeroCode.Skills.Bundled.Research;
using AeroCode.Skills.Models;
using AeroCode.Skills.Registry;
using Xunit;

namespace AeroCode.Tests.SkillTests;

public class WebResearchDeepTests
{
    private static SkillContext Ctx() => new() { WorkspaceRoot = Environment.CurrentDirectory };

    // Network tests are gated — they hit external URLs that may be blocked by firewalls.
    // Set AEROCODE_RUN_NETWORK_TESTS=1 to enable.
    private static bool NetworkEnabled() => Environment.GetEnvironmentVariable("AEROCODE_RUN_NETWORK_TESTS") == "1";
    private static string SampleUrl() => Environment.GetEnvironmentVariable("AEROCODE_NETWORK_URL") ?? "https://example.com";

    [Fact]
    public void ExtractText_RemovesScriptAndStyle()
    {
        var html = "<html><head><style>body{}</style><script>alert(1)</script></head><body><h1>Title</h1><p>Hello world</p></body></html>";
        var text = WebResearchSkill.ExtractText(html);
        Assert.DoesNotContain("alert", text);
        Assert.DoesNotContain("body{}", text);
        Assert.Contains("Title", text);
        Assert.Contains("Hello world", text);
    }

    [Fact(Skip = "Network tests disabled — set AEROCODE_RUN_NETWORK_TESTS=1 to enable")]
    public async Task ModeFetch_Wikipedia_ExtractsRealText()
    {
        if (!NetworkEnabled()) return; // skip when network tests disabled
        var skill = new WebResearchSkill();
        var input = new SkillInput
        {
            Args = new System.Collections.Generic.Dictionary<string, object?>
            {
                ["mode"] = "fetch",
                ["url"] = SampleUrl(),
                ["max_chars"] = 4000
            }
        };
        var res = await skill.ExecuteAsync(input, Ctx());
        Assert.True(res.Success, res.Text);
        Assert.NotEmpty(res.Text);
    }

    [Fact(Skip = "Network tests disabled — set AEROCODE_RUN_NETWORK_TESTS=1 to enable")]
    public async Task ModeSummary_ReturnsTopSentences()
    {
        if (!NetworkEnabled()) return;
        var skill = new WebResearchSkill();
        var input = new SkillInput
        {
            Args = new System.Collections.Generic.Dictionary<string, object?>
            {
                ["mode"] = "summary",
                ["url"] = SampleUrl(),
                ["sentences"] = 3,
                ["max_chars"] = 2000
            }
        };
        var res = await skill.ExecuteAsync(input, Ctx());
        Assert.True(res.Success, res.Text);
        Assert.NotEmpty(res.Text);
    }

    [Fact(Skip = "Network tests disabled — set AEROCODE_RUN_NETWORK_TESTS=1 to enable")]
    public async Task ModeStructured_ExtractsOpenGraph_FromOgMeta()
    {
        if (!NetworkEnabled()) return;
        var url = "https://github.com/dotnet/runtime";
        var skill = new WebResearchSkill();
        var input = new SkillInput
        {
            Args = new System.Collections.Generic.Dictionary<string, object?>
            {
                ["mode"] = "structured",
                ["url"] = url,
                ["max_chars"] = 4000
            }
        };
        var res = await skill.ExecuteAsync(input, Ctx());
        Assert.True(res.Success, res.Text);
        Assert.Contains("OpenGraph", res.Text);
    }

    [Fact(Skip = "Network tests disabled — set AEROCODE_RUN_NETWORK_TESTS=1 to enable")]
    public async Task ModeSitemap_ParsesValidSitemap()
    {
        if (!NetworkEnabled()) return;
        var skill = new WebResearchSkill();
        var input = new SkillInput
        {
            Args = new System.Collections.Generic.Dictionary<string, object?>
            {
                ["mode"] = "sitemap",
                ["url"] = "https://www.sitemaps.org/sitemap.xml",
                ["max_pages"] = 3
            }
        };
        var res = await skill.ExecuteAsync(input, Ctx());
        Assert.True(res.Success, res.Text);
        Assert.Contains("URLs discovered", res.Text);
    }

    [Fact]
    public async Task ModeSitemap_ParsesUrls_WithoutFetching()
    {
        // Internal: verify the XML parser correctly extracts <loc> entries.
        var xml = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
                  "<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">" +
                  "<url><loc>https://a.com/p1</loc></url>" +
                  "<url><loc>https://a.com/p2</loc></url>" +
                  "</urlset>";
        var urls = await WebResearchSkill.ParseSitemapStringAsync(xml);
        Assert.Equal(2, urls.Count);
        Assert.Contains("https://a.com/p1", urls);
        Assert.Contains("https://a.com/p2", urls);
    }

    [Fact(Skip = "Network tests disabled — set AEROCODE_RUN_NETWORK_TESTS=1 to enable")]
    public async Task ModeRobots_ParsesDisallow()
    {
        if (!NetworkEnabled()) return;
        var skill = new WebResearchSkill();
        var input = new SkillInput
        {
            Args = new System.Collections.Generic.Dictionary<string, object?>
            {
                ["mode"] = "robots",
                ["url"] = "https://example.com/"
            }
        };
        var res = await skill.ExecuteAsync(input, Ctx());
        Assert.True(res.Success, res.Text);
        Assert.Contains("robots.txt", res.Text);
    }
}
