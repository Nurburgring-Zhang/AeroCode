// SearchService aggregation tests — providers are legitimate hand-written test doubles
// implementing the real ISearchProvider contract (no mocking library).
using AeroCode.Skills.Research;
using Xunit;

namespace AeroCode.Tests.Skills.Research;

/// <summary>Test double: scriptable real-contract search provider.</summary>
internal sealed class FakeSearchProvider : ISearchProvider
{
    private readonly Func<string, int, IReadOnlyList<WebSearchResult>> _script;
    public string Name { get; }
    public int CallCount { get; private set; }

    public FakeSearchProvider(string name, Func<string, int, IReadOnlyList<WebSearchResult>> script)
    {
        Name = name;
        _script = script;
    }

    public Task<IReadOnlyList<WebSearchResult>> SearchAsync(string query, int maxResults, CancellationToken ct)
    {
        CallCount++;
        return Task.FromResult(_script(query, maxResults));
    }
}

/// <summary>Test double: provider that always throws (failure-isolation checks).</summary>
internal sealed class ThrowingSearchProvider : ISearchProvider
{
    public string Name => "throwing";
    public Task<IReadOnlyList<WebSearchResult>> SearchAsync(string query, int maxResults, CancellationToken ct)
        => throw new InvalidOperationException("backend down");
}

/// <summary>Test double: availability-aware provider with configurable availability.</summary>
internal sealed class GatedSearchProvider : IAvailabilityAwareSearchProvider
{
    public string Name => "gated";
    public bool IsAvailable { get; init; }
    public string UnavailabilityReason => IsAvailable ? string.Empty : "no key";
    public int CallCount { get; private set; }

    public Task<IReadOnlyList<WebSearchResult>> SearchAsync(string query, int maxResults, CancellationToken ct)
    {
        CallCount++;
        return Task.FromResult<IReadOnlyList<WebSearchResult>>(new[]
        {
            new WebSearchResult("Gated hit", "https://gated.example.com/", "snip", Name),
        });
    }
}

public class SearchServiceTests
{
    private static WebSearchResult Hit(string url, string source, string title = "t") =>
        new(title, url, "snippet", source);

    [Fact]
    public async Task SearchAsync_MergesProviders_InPriorityOrder()
    {
        var first = new FakeSearchProvider("p1", (_, _) => new[] { Hit("https://a.com/", "p1"), Hit("https://b.com/", "p1") });
        var second = new FakeSearchProvider("p2", (_, _) => new[] { Hit("https://c.com/", "p2") });
        var service = new SearchService(new ISearchProvider[] { first, second });

        var results = await service.SearchAsync("query", 8);

        Assert.Equal(3, results.Count);
        Assert.Equal("p1", results[0].Source);
        Assert.Equal("p1", results[1].Source);
        Assert.Equal("p2", results[2].Source);
    }

    [Fact]
    public async Task SearchAsync_DeduplicatesByNormalizedUrl_KeepsFirstSource()
    {
        var first = new FakeSearchProvider("p1", (_, _) => new[] { Hit("https://a.com/page", "p1") });
        var second = new FakeSearchProvider("p2", (_, _) => new[] { Hit("https://A.COM/page/", "p2", "other title") });
        var service = new SearchService(new ISearchProvider[] { first, second });

        var results = await service.SearchAsync("query", 8);

        Assert.Single(results);
        Assert.Equal("p1", results[0].Source);
    }

    [Fact]
    public async Task SearchAsync_MaxResults_StopsEarly()
    {
        var first = new FakeSearchProvider("p1", (_, _) => new[] { Hit("https://a.com/", "p1"), Hit("https://b.com/", "p1") });
        var second = new FakeSearchProvider("p2", (_, _) => new[] { Hit("https://c.com/", "p2") });
        var service = new SearchService(new ISearchProvider[] { first, second });

        var results = await service.SearchAsync("query", 2);

        Assert.Equal(2, results.Count);
        Assert.Equal(0, second.CallCount); // satisfied by p1 — never queried
    }

    [Fact]
    public async Task SearchAsync_ProviderException_IsIsolated_AggregateContinues()
    {
        var throwing = new ThrowingSearchProvider();
        var good = new FakeSearchProvider("good", (_, _) => new[] { Hit("https://ok.com/", "good") });
        var service = new SearchService(new ISearchProvider[] { throwing, good });

        var results = await service.SearchAsync("query", 8);

        Assert.Single(results);
        Assert.Equal("good", results[0].Source);
    }

    [Fact]
    public async Task SearchAsync_UnavailableAwareProvider_IsSkipped_NotCalled()
    {
        var gated = new GatedSearchProvider { IsAvailable = false };
        var good = new FakeSearchProvider("good", (_, _) => new[] { Hit("https://ok.com/", "good") });
        var service = new SearchService(new ISearchProvider[] { gated, good });

        var results = await service.SearchAsync("query", 8);

        Assert.Equal(0, gated.CallCount);
        Assert.Single(results);
    }

    [Fact]
    public async Task SearchAsync_AvailableAwareProvider_IsCalled()
    {
        var gated = new GatedSearchProvider { IsAvailable = true };
        var service = new SearchService(new ISearchProvider[] { gated });

        var results = await service.SearchAsync("query", 8);

        Assert.Equal(1, gated.CallCount);
        Assert.Single(results);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SearchAsync_EmptyQuery_ReturnsEmpty(string query)
    {
        var provider = new FakeSearchProvider("p", (_, _) => new[] { Hit("https://a.com/", "p") });
        var service = new SearchService(new ISearchProvider[] { provider });

        Assert.Empty(await service.SearchAsync(query, 8));
        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public async Task SearchAsync_ZeroMaxResults_ReturnsEmpty()
    {
        var provider = new FakeSearchProvider("p", (_, _) => new[] { Hit("https://a.com/", "p") });
        var service = new SearchService(new ISearchProvider[] { provider });
        Assert.Empty(await service.SearchAsync("q", 0));
    }

    [Fact]
    public void CreateDefault_ContainsThreeRealProviders_DdgAlwaysAvailable()
    {
        var service = SearchService.CreateDefault();
        Assert.Equal(3, service.Providers.Count);
        Assert.Contains(service.Providers, p => p.Name == DuckDuckGoHtmlParser.SourceName);
        var ddg = service.Providers.First(p => p.Name == DuckDuckGoHtmlParser.SourceName);
        Assert.True(((IAvailabilityAwareSearchProvider)ddg).IsAvailable);
    }

    [Theory]
    [InlineData("https://Example.COM/Path/", "https://example.com/Path")]
    [InlineData("https://a.com", "https://a.com")]
    public void NormalizeUrl_LowercasesHostAndTrimsTrailingSlash(string input, string expected)
    {
        Assert.Equal(expected, SearchService.NormalizeUrl(input));
    }

    [Fact]
    public void NormalizeUrl_GarbageInput_ReturnsTrimmedOriginal()
    {
        Assert.Equal("not a url", SearchService.NormalizeUrl("  not a url  "));
    }
}
