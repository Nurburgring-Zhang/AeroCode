// DuckDuckGoHtmlParser tests.
//
// Fixture honesty note (2026-08-20): live capture from the build machine was blocked by
// DDG's anomaly/bot challenge on both html/ and lite/ endpoints. The structural fixture
// below is reconstructed from DDG's documented, stable html-endpoint markup
// (result__a / result__snippet / /l/?uddg= redirect wrapper) and is labeled as such —
// it is NOT claimed to be a captured page. The challenge-page fragment, in contrast, IS
// genuinely captured on 2026-08-20 (see workspace p5/fixtures/ddg_avalonia.html).
using AeroCode.Skills.Research;
using Xunit;

namespace AeroCode.Tests.Skills.Research;

public class DuckDuckGoHtmlParserTests
{
    /// <summary>
    /// Structural fixture reconstructed from DDG's documented html-endpoint layout
    /// (NOT a live capture — live capture blocked by anomaly challenge on 2026-08-20).
    /// </summary>
    private const string StructuralResultsHtml = """
        <!DOCTYPE html>
        <html><body>
        <div id="links">
          <div class="result results_links results_links_deep web-result">
            <div class="links_main links_deep result__body">
              <h2 class="result__title">
                <a rel="nofollow" class="result__a" href="//duckduckgo.com/l/?uddg=https%3A%2F%2Favaloniaui.net%2F&amp;rut=abc">Avalonia UI - Cross-platform .NET UI framework</a>
              </h2>
              <a class="result__url" href="//duckduckgo.com/l/?uddg=https%3A%2F%2Favaloniaui.net%2F">avaloniaui.net</a>
              <a class="result__snippet" href="//duckduckgo.com/l/?uddg=https%3A%2F%2Favaloniaui.net%2F">Build <b>cross-platform</b> apps with .NET</a>
            </div>
          </div>
          <div class="result results_links results_links_deep web-result">
            <div class="links_main links_deep result__body">
              <h2 class="result__title">
                <a rel="nofollow" class="result__a" href="//duckduckgo.com/l/?uddg=https%3A%2F%2Fgithub.com%2FAvaloniaUI%2FAvalonia&amp;rut=def">GitHub - AvaloniaUI/Avalonia</a>
              </h2>
              <a class="result__snippet" href="//duckduckgo.com/l/?uddg=https%3A%2F%2Fgithub.com%2FAvaloniaUI%2FAvalonia">A free and open-source .NET UI framework</a>
            </div>
          </div>
        </div>
        </body></html>
        """;

    /// <summary>
    /// Real captured DDG anomaly-challenge fragment (2026-08-20, query "avalonia ui framework").
    /// Full page archived at workspace p5/fixtures/ddg_avalonia.html.
    /// </summary>
    private const string RealChallengeFragment = """
        <!DOCTYPE html>
        <html lang="en">
        <head><title>DuckDuckGo</title></head>
        <body>
        <form id="challenge-form" action="//duckduckgo.com/anomaly.js?sv=html&amp;cc=botnet" method="POST">
            <div class="anomaly-modal__mask"></div>
        </form>
        </body></html>
        """;

    [Fact]
    public void Parse_StructuralFixture_ExtractsTitlesUrlsAndSnippets()
    {
        var results = DuckDuckGoHtmlParser.Parse(StructuralResultsHtml, maxResults: 10);

        Assert.Equal(2, results.Count);
        Assert.Equal("Avalonia UI - Cross-platform .NET UI framework", results[0].Title);
        Assert.Equal("https://avaloniaui.net/", results[0].Url);
        Assert.Contains("cross-platform", results[0].Snippet);
        Assert.Equal("duckduckgo", results[0].Source);
        Assert.Equal("https://github.com/AvaloniaUI/Avalonia", results[1].Url);
    }

    [Fact]
    public void Parse_ResolvesUddgRedirectLinks_ToRealDestinations()
    {
        var results = DuckDuckGoHtmlParser.Parse(StructuralResultsHtml, maxResults: 10);
        Assert.All(results, r => Assert.DoesNotContain("duckduckgo.com/l/", r.Url));
    }

    [Fact]
    public void Parse_MaxResults_IsRespected()
    {
        var results = DuckDuckGoHtmlParser.Parse(StructuralResultsHtml, maxResults: 1);
        Assert.Single(results);
    }

    [Fact]
    public void Parse_RealChallengePage_ReturnsEmpty_NeverFabricates()
    {
        Assert.True(DuckDuckGoHtmlParser.IsChallengePage(RealChallengeFragment));
        var results = DuckDuckGoHtmlParser.Parse(RealChallengeFragment, maxResults: 10);
        Assert.Empty(results);
    }

    [Fact]
    public void Parse_EmptyOrWhitespaceHtml_ReturnsEmpty()
    {
        Assert.Empty(DuckDuckGoHtmlParser.Parse("", 10));
        Assert.Empty(DuckDuckGoHtmlParser.Parse("   ", 10));
    }

    [Fact]
    public void Parse_ZeroMaxResults_ReturnsEmpty()
    {
        Assert.Empty(DuckDuckGoHtmlParser.Parse(StructuralResultsHtml, 0));
    }

    [Fact]
    public void Parse_NoResultAnchors_ReturnsEmpty()
    {
        var html = "<html><body><p>no results here</p></body></html>";
        Assert.Empty(DuckDuckGoHtmlParser.Parse(html, 10));
    }

    [Fact]
    public void Parse_LiteLayout_FallsBackToResultLinkAnchors()
    {
        var lite = """
            <html><body><table>
            <tr><td><a rel="nofollow" href="https://example.com/page" class="result-link">Example Page</a></td></tr>
            <tr><td class="result-snippet">An example snippet for the page.</td></tr>
            </table></body></html>
            """;
        var results = DuckDuckGoHtmlParser.Parse(lite, 10);
        Assert.Single(results);
        Assert.Equal("Example Page", results[0].Title);
        Assert.Equal("https://example.com/page", results[0].Url);
        Assert.Contains("example snippet", results[0].Snippet);
    }

    [Theory]
    [InlineData("//duckduckgo.com/l/?uddg=https%3A%2F%2Fexample.com%2Fx", "https://example.com/x")]
    [InlineData("https://duckduckgo.com/l/?uddg=https%3A%2F%2Fa.b%2Fp%3Fq%3D1&rut=z", "https://a.b/p?q=1")]
    public void ResolveRedirectUrl_DecodesUddg(string href, string expected)
    {
        Assert.Equal(expected, DuckDuckGoHtmlParser.ResolveRedirectUrl(href));
    }

    [Theory]
    [InlineData("https://example.com/direct")]
    [InlineData("")]
    [InlineData("//duckduckgo.com/l/?other=1")]
    public void ResolveRedirectUrl_NonUddgHref_ReturnsNull(string href)
    {
        Assert.Null(DuckDuckGoHtmlParser.ResolveRedirectUrl(href));
    }

    [Fact]
    public void IsChallengePage_NormalResultsHtml_IsFalse()
    {
        Assert.False(DuckDuckGoHtmlParser.IsChallengePage(StructuralResultsHtml));
    }
}
