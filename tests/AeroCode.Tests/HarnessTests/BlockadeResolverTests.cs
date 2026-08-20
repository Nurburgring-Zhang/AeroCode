// BlockadeResolver tests — search-grounded candidate generation + sequential attempts.
using AeroCode.Harness.Blockade;
using AeroCode.Skills.Research;
using Xunit;

namespace AeroCode.Tests.HarnessTests;

public class BlockadeResolverTests
{
    private static SearchService ServiceWithHits(params WebSearchResult[] hits) =>
        new(new ISearchProvider[] { new BlockadeFakeProvider(hits) });

    private sealed class BlockadeFakeProvider : ISearchProvider
    {
        private readonly IReadOnlyList<WebSearchResult> _hits;
        public BlockadeFakeProvider(IReadOnlyList<WebSearchResult> hits) => _hits = hits;
        public string Name => "blockade-fake";
        public Task<IReadOnlyList<WebSearchResult>> SearchAsync(string query, int maxResults, CancellationToken ct)
            => Task.FromResult(_hits.Take(maxResults).ToList() as IReadOnlyList<WebSearchResult>);
    }

    [Fact]
    public async Task FirstCandidateFixes_ResolvesWithOneAttempt()
    {
        var resolver = new BlockadeResolver(ServiceWithHits(
            new WebSearchResult("Known issue KB123", "https://kb.example.com/123", "reset the cache dir", "blockade-fake")));

        var resolution = await resolver.ResolveAsync(
            new BlockadeContext("MSB3061 access denied", "build", null),
            (candidate, _) => Task.FromResult((true, $"applied: {candidate.Title}")));

        Assert.True(resolution.Resolved);
        Assert.Single(resolution.Attempts);
        Assert.True(resolution.Attempts[0].Succeeded);
        Assert.Contains("KB123", resolution.Summary);
        Assert.Single(resolution.References);
        Assert.False(resolution.SearchDegraded);
    }

    [Fact]
    public async Task LaterCandidateFixes_AllAttemptsRecorded()
    {
        var resolver = new BlockadeResolver(ServiceWithHits(
            new WebSearchResult("Hit A", "https://a.example.com/", "snip a", "blockade-fake"),
            new WebSearchResult("Hit B", "https://b.example.com/", "snip b", "blockade-fake")));

        var resolution = await resolver.ResolveAsync(
            new BlockadeContext("error X", "test", null),
            (candidate, _) => Task.FromResult((candidate.Title == "Hit B", $"tried {candidate.Title}")),
            maxCandidates: 4);

        Assert.True(resolution.Resolved);
        Assert.True(resolution.Attempts.Count >= 2);
        Assert.False(resolution.Attempts[0].Succeeded);
        Assert.True(resolution.Attempts[^1].Succeeded);
    }

    [Fact]
    public async Task NoCandidateWorks_ReportsUnresolved_WithFullAttemptLog()
    {
        var resolver = new BlockadeResolver(ServiceWithHits());

        var resolution = await resolver.ResolveAsync(
            new BlockadeContext("mystery error", null, null),
            (_, _) => Task.FromResult((false, "did not help")));

        Assert.False(resolution.Resolved);
        Assert.True(resolution.Attempts.Count >= 2); // deterministic strategies always present
        Assert.All(resolution.Attempts, a => Assert.False(a.Succeeded));
        Assert.True(resolution.SearchDegraded); // no hits → degraded honestly flagged
    }

    [Fact]
    public async Task FixDelegateException_CountedAsFailedAttempt_NotCrash()
    {
        var resolver = new BlockadeResolver(ServiceWithHits());

        var resolution = await resolver.ResolveAsync(
            new BlockadeContext("err", null, null),
            (_, _) => throw new InvalidOperationException("fix blew up"));

        Assert.False(resolution.Resolved);
        Assert.All(resolution.Attempts, a => Assert.Contains("fix blew up", a.Detail));
    }

    [Fact]
    public async Task EmptyError_ReturnsUnresolved_NoSearchCall()
    {
        var resolver = new BlockadeResolver(ServiceWithHits(
            new WebSearchResult("should never be used", "https://x.example.com/", "", "blockade-fake")));

        var resolution = await resolver.ResolveAsync(
            new BlockadeContext("   ", null, null),
            (_, _) => Task.FromResult((true, "must not run")));

        Assert.False(resolution.Resolved);
        Assert.Empty(resolution.Attempts);
    }

    [Fact]
    public void BuildQuery_IncludesStageAndTrimsLongErrors()
    {
        var query = BlockadeResolver.BuildQuery(new BlockadeContext(new string('e', 500), "build", null));
        Assert.StartsWith("build error fix:", query);
        Assert.True(query.Length < 200);
    }

    [Fact]
    public void BuildCandidates_SearchGroundedPlusDeterministic_AlwaysAtLeastTwo()
    {
        var none = BlockadeResolver.BuildCandidates(
            new BlockadeContext("e", null, null), "q", Array.Empty<WebSearchResult>(), 3);
        Assert.True(none.Count >= 2);

        var grounded = BlockadeResolver.BuildCandidates(
            new BlockadeContext("e", null, null), "q",
            new[]
            {
                new WebSearchResult("R1", "https://r1.example.com/", "fix one", "p"),
                new WebSearchResult("R2", "https://r2.example.com/", "fix two", "p"),
                new WebSearchResult("R3", "https://r3.example.com/", "fix three", "p"),
            }, 4);

        Assert.Equal(4, grounded.Count);
        Assert.All(grounded.Take(3), c => Assert.NotNull(c.ReferenceUrl)); // 3 hits grounded
        Assert.Null(grounded[3].ReferenceUrl);                              // deterministic tail
    }

    [Fact]
    public void BuildCandidates_NeverFabricatesReferences()
    {
        var candidates = BlockadeResolver.BuildCandidates(
            new BlockadeContext("e", null, null), "q", Array.Empty<WebSearchResult>(), 3);
        Assert.All(candidates, c => Assert.Null(c.ReferenceUrl)); // no hits → no fake references
    }

    [Fact]
    public async Task SearchServiceFailure_DegradesToDeterministicStrategies_Honestly()
    {
        var failing = new SearchService(new ISearchProvider[] { new ThrowingBlockadeProvider() });
        var resolver = new BlockadeResolver(failing);

        var resolution = await resolver.ResolveAsync(
            new BlockadeContext("err", null, null),
            (_, _) => Task.FromResult((false, "nope")));

        Assert.True(resolution.SearchDegraded);
        Assert.False(resolution.Resolved);
        Assert.True(resolution.Attempts.Count >= 2);
    }

    private sealed class ThrowingBlockadeProvider : ISearchProvider
    {
        public string Name => "throwing-blockade";
        public Task<IReadOnlyList<WebSearchResult>> SearchAsync(string query, int maxResults, CancellationToken ct)
            => throw new HttpRequestException("search backend down");
    }
}
