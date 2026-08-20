// Provider tests: JSON parsing against documented response shapes, availability
// semantics, and DuckDuckGo provider behavior over a fake HTTP transport
// (legitimate test double implementing the real HttpMessageHandler contract).
using System.Net;
using AeroCode.Skills.Research;
using Xunit;

namespace AeroCode.Tests.Skills.Research;

/// <summary>Test double: scripted HTTP transport (no network).</summary>
internal sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
    public HttpRequestMessage? LastRequest { get; private set; }

    public FakeHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        LastRequest = request;
        return Task.FromResult(_responder(request));
    }
}

public class BingWebProviderTests
{
    private const string BingSampleJson = """
        {
          "webPages": {
            "value": [
              { "name": "Result One", "url": "https://one.example.com/", "snippet": "First snippet" },
              { "name": "Result Two", "url": "https://two.example.com/", "snippet": "Second snippet" },
              { "name": "", "url": "https://empty-title.example.com/" }
            ]
          }
        }
        """;

    [Fact]
    public void ParseWebPages_ExtractsRealFields_SkipsEmptyEntries()
    {
        var results = BingWebProvider.ParseWebPages(BingSampleJson, 10);
        Assert.Equal(2, results.Count);
        Assert.Equal("Result One", results[0].Title);
        Assert.Equal("https://one.example.com/", results[0].Url);
        Assert.Equal("First snippet", results[0].Snippet);
        Assert.Equal("bing", results[0].Source);
    }

    [Fact]
    public void ParseWebPages_RespectsMaxResults()
    {
        var results = BingWebProvider.ParseWebPages(BingSampleJson, 1);
        Assert.Single(results);
    }

    [Fact]
    public void ParseWebPages_NoWebPagesNode_ReturnsEmpty()
    {
        Assert.Empty(BingWebProvider.ParseWebPages("{\"something\":1}", 10));
    }

    [Fact]
    public async Task WithoutApiKey_IsUnavailable_AndReturnsEmpty_NotFabricated()
    {
        using var provider = new BingWebProvider(apiKey: null);
        if (BingWebProvider.IsConfigured)
        {
            // Environment genuinely has a key — this assertion path is not applicable.
            return;
        }

        Assert.False(provider.IsAvailable);
        Assert.Contains(BingWebProvider.ApiKeyEnvVar, provider.UnavailabilityReason);
        Assert.Empty(await provider.SearchAsync("anything", 5, CancellationToken.None));
    }

    [Fact]
    public async Task WithKey_RealHttpFlow_ParsesResults()
    {
        using var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(BingSampleJson),
        });
        using var provider = new BingWebProvider(apiKey: "test-key", handler);

        var results = await provider.SearchAsync("dotnet", 5, CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.NotNull(handler.LastRequest);
        Assert.True(handler.LastRequest!.Headers.Contains("Ocp-Apim-Subscription-Key"));
    }

    [Fact]
    public async Task HttpError_ReturnsEmpty_Honestly()
    {
        using var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        using var provider = new BingWebProvider(apiKey: "test-key", handler);
        Assert.Empty(await provider.SearchAsync("q", 5, CancellationToken.None));
    }
}

public class TavilyProviderTests
{
    private const string TavilySampleJson = """
        {
          "results": [
            { "title": "Tavily One", "url": "https://t1.example.com/", "content": "Content one", "score": 0.9 },
            { "title": "Tavily Two", "url": "https://t2.example.com/", "content": "Content two", "score": 0.8 }
          ]
        }
        """;

    [Fact]
    public void ParseResults_ExtractsRealFields()
    {
        var results = TavilyProvider.ParseResults(TavilySampleJson, 10);
        Assert.Equal(2, results.Count);
        Assert.Equal("Tavily One", results[0].Title);
        Assert.Equal("Content one", results[0].Snippet);
        Assert.Equal("tavily", results[0].Source);
    }

    [Fact]
    public void ParseResults_NoResultsNode_ReturnsEmpty()
    {
        Assert.Empty(TavilyProvider.ParseResults("{\"answer\":\"x\"}", 10));
    }

    [Fact]
    public async Task WithoutApiKey_IsUnavailable_AndReturnsEmpty()
    {
        using var provider = new TavilyProvider(apiKey: null);
        if (TavilyProvider.IsConfigured) return; // env genuinely configured

        Assert.False(provider.IsAvailable);
        Assert.Empty(await provider.SearchAsync("anything", 5, CancellationToken.None));
    }

    [Fact]
    public async Task WithKey_RealHttpFlow_PostsBearerAndParses()
    {
        using var handler = new FakeHttpHandler(req =>
        {
            Assert.Equal(HttpMethod.Post, req.Method);
            Assert.Contains("Bearer", req.Headers.Authorization?.ToString() ?? "");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(TavilySampleJson) };
        });
        using var provider = new TavilyProvider(apiKey: "tvly-test", handler);

        var results = await provider.SearchAsync("query", 5, CancellationToken.None);
        Assert.Equal(2, results.Count);
    }
}

public class DuckDuckGoHtmlProviderTests
{
    private const string ResultsHtml = """
        <html><body>
        <div class="result"><div class="result__body">
        <a class="result__a" href="//duckduckgo.com/l/?uddg=https%3A%2F%2Fexample.com%2F">Example Title</a>
        <a class="result__snippet">Example snippet</a>
        </div></div>
        </body></html>
        """;

    private const string ChallengeHtml = """
        <html><body><form id="challenge-form" action="//duckduckgo.com/anomaly.js"></form></body></html>
        """;

    [Fact]
    public void IsAlwaysAvailable_KeyFree()
    {
        using var provider = new DuckDuckGoHtmlProvider();
        Assert.True(provider.IsAvailable);
        Assert.Equal(string.Empty, provider.UnavailabilityReason);
        Assert.Equal(DuckDuckGoHtmlParser.SourceName, provider.Name);
    }

    [Fact]
    public async Task RealHttpFlow_ParsesResults_FromTransport()
    {
        using var handler = new FakeHttpHandler(req =>
        {
            Assert.StartsWith(DuckDuckGoHtmlProvider.HtmlEndpoint, req.RequestUri!.ToString());
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ResultsHtml) };
        });
        using var provider = new DuckDuckGoHtmlProvider(handler);

        var results = await provider.SearchAsync("example query", 5, CancellationToken.None);

        Assert.Single(results);
        Assert.Equal("Example Title", results[0].Title);
        Assert.Equal("https://example.com/", results[0].Url);
    }

    [Fact]
    public async Task ChallengeResponse_ReturnsEmpty_NoFabrication()
    {
        using var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(ChallengeHtml),
        });
        using var provider = new DuckDuckGoHtmlProvider(handler);

        Assert.Empty(await provider.SearchAsync("q", 5, CancellationToken.None));
    }

    [Fact]
    public async Task NonSuccessStatus_ReturnsEmpty_Honestly()
    {
        using var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        using var provider = new DuckDuckGoHtmlProvider(handler);
        Assert.Empty(await provider.SearchAsync("q", 5, CancellationToken.None));
    }

    [Fact]
    public async Task NetworkException_ReturnsEmpty_Honestly()
    {
        using var handler = new FakeHttpHandler(_ => throw new HttpRequestException("connection reset"));
        using var provider = new DuckDuckGoHtmlProvider(handler);
        Assert.Empty(await provider.SearchAsync("q", 5, CancellationToken.None));
    }

    [Fact]
    public async Task EmptyQuery_ReturnsEmpty_WithoutHttpCall()
    {
        using var handler = new FakeHttpHandler(_ => throw new InvalidOperationException("must not be called"));
        using var provider = new DuckDuckGoHtmlProvider(handler);
        Assert.Empty(await provider.SearchAsync("", 5, CancellationToken.None));
    }
}
