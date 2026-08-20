// WebResearchSkill websearch mode tests (search backend injected — no network needed)
// plus network-gated live tests that follow the repo convention
// (AEROCODE_RUN_NETWORK_TESTS=1; honest skip otherwise).
using AeroCode.Skills.Bundled.Research;
using AeroCode.Skills.Registry;
using AeroCode.Skills.Research;
using Xunit;

namespace AeroCode.Tests.Skills.Research;

public class WebResearchWebSearchModeTests
{
    private static SkillContext Ctx() => new() { WorkspaceRoot = Environment.CurrentDirectory };

    private static WebResearchSkill SkillWithHits(params WebSearchResult[] hits)
    {
        var provider = new FakeSearchProvider("scripted", (_, _) => hits);
        return new WebResearchSkill(new SearchService(new ISearchProvider[] { provider }));
    }

    [Fact]
    public async Task WebSearch_WithHits_ReturnsStructuredFindingsWithCitations()
    {
        var skill = SkillWithHits(
            new WebSearchResult("Doc Page", "https://docs.example.com/intro", "intro snippet", "scripted"));

        var result = await skill.ExecuteAsync(new SkillInput
        {
            Args = new Dictionary<string, object?>
            {
                ["mode"] = "websearch",
                ["query"] = "example docs",
                ["fetch_top"] = 0, // no page fetching — findings only
            },
        }, Ctx());

        Assert.True(result.Success, result.Text);
        Assert.Contains("Doc Page", result.Text);
        Assert.Contains("https://docs.example.com/intro", result.Text);
        Assert.Contains("scripted", result.Text);
        var data = Assert.IsAssignableFrom<IReadOnlyList<WebSearchResult>>(result.Data);
        Assert.Single(data);
    }

    [Fact]
    public async Task WebSearch_NoResults_FailsHonestly_NoFabrication()
    {
        var skill = SkillWithHits(); // zero hits

        var result = await skill.ExecuteAsync(new SkillInput
        {
            Args = new Dictionary<string, object?> { ["mode"] = "websearch", ["query"] = "anything" },
        }, Ctx());

        Assert.False(result.Success);
        Assert.Contains("无结果", result.Text);
        Assert.Contains("未伪造", result.Text);
    }

    [Fact]
    public async Task WebSearch_MissingQuery_FailsWithClearMessage()
    {
        var skill = SkillWithHits();
        var result = await skill.ExecuteAsync(new SkillInput
        {
            Args = new Dictionary<string, object?> { ["mode"] = "websearch" },
        }, Ctx());
        Assert.False(result.Success);
        Assert.Contains("query", result.Text);
    }

    [Fact]
    public async Task UnknownMode_FailsHonestly()
    {
        var skill = SkillWithHits();
        var result = await skill.ExecuteAsync(new SkillInput
        {
            Args = new Dictionary<string, object?> { ["mode"] = "teleport" },
        }, Ctx());
        Assert.False(result.Success);
        Assert.Contains("Unknown mode", result.Text);
    }

    [Fact]
    public void SystemPrompt_DocumentsWebSearchMode_Honestly()
    {
        var skill = new WebResearchSkill(new SearchService(Array.Empty<ISearchProvider>()));
        var prompt = skill.GetSystemPrompt();
        Assert.Contains("websearch", prompt);
        Assert.Contains("DuckDuckGo", prompt);
    }
}

/// <summary>
/// Live network tests — gated per repo convention. These hit REAL endpoints; when the
/// environment does not opt in (AEROCODE_RUN_NETWORK_TESTS=1) they skip honestly.
/// DDG note: from some networks DDG answers with an anomaly challenge; the assertion
/// accepts either real results or an honest empty outcome — never fabricated data.
/// </summary>
public class ResearchLiveTests
{
    private static bool NetworkEnabled() => Environment.GetEnvironmentVariable("AEROCODE_RUN_NETWORK_TESTS") == "1";

    [SkippableFact]
    public async Task Live_DuckDuckGo_ReturnsRealResultsOrHonestEmpty()
    {
        Skip.IfNot(NetworkEnabled(), "AEROCODE_RUN_NETWORK_TESTS != 1，网络用例如实跳过");

        using var provider = new DuckDuckGoHtmlProvider();
        var results = await provider.SearchAsync("dotnet avalonia", 5, CancellationToken.None);

        // Either genuine hits, or an honest empty set (challenge/blocked). Both are truthful.
        Assert.NotNull(results);
        foreach (var r in results)
        {
            Assert.False(string.IsNullOrWhiteSpace(r.Title));
            Assert.StartsWith("http", r.Url);
        }
    }

    [SkippableFact]
    public async Task Live_WebSearchMode_EndToEnd_OverRealBackend()
    {
        Skip.IfNot(NetworkEnabled(), "AEROCODE_RUN_NETWORK_TESTS != 1，网络用例如实跳过");

        var skill = new WebResearchSkill(); // default stack: DDG (+Bing/Tavily if keys set)
        var result = await skill.ExecuteAsync(new SkillInput
        {
            Args = new Dictionary<string, object?>
            {
                ["mode"] = "websearch",
                ["query"] = "duckduckgo html endpoint",
                ["max_results"] = 3,
                ["fetch_top"] = 0,
            },
        }, new SkillContext { WorkspaceRoot = Environment.CurrentDirectory });

        // Success = real hits; failure must be the honest "无结果/未伪造" path.
        if (!result.Success)
        {
            Assert.Contains("未伪造", result.Text);
        }
        else
        {
            Assert.Contains("真实结果", result.Text);
        }
    }
}
