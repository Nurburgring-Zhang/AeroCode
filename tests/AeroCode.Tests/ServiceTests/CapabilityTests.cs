// Copyright (c) AeroCode V3.0
// 6 AI Capabilities tests — uses FakeHandler (HTTP mock) to verify LLM call correctness.
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AeroCode.AI.Capabilities;
using AeroCode.AI.Configuration;
using AeroCode.AI.Models;
using AeroCode.AI.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AeroCode.Tests.ServiceTests;

[Collection("EnvMutators")]
public class CapabilityTests
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        public string ResponseBody { get; set; } = "{}";
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public string? LastRequestBody { get; private set; }
        public string? LastSystemPrompt { get; private set; }
        public string? LastUserPrompt { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Content is not null)
            {
                var body = await request.Content.ReadAsStringAsync(ct);
                LastRequestBody = body;
                // Extract system + user messages for verification
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("messages", out var msgs))
                {
                    foreach (var msg in msgs.EnumerateArray())
                    {
                        var role = msg.GetProperty("role").GetString();
                        var content = msg.GetProperty("content").GetString() ?? "";
                        if (role == "system") LastSystemPrompt = content;
                        if (role == "user") LastUserPrompt = content;
                    }
                }
            }
            return new HttpResponseMessage(Status)
            {
                Content = new StringContent(ResponseBody, Encoding.UTF8, "application/json")
            };
        }
    }

    private static (IAiProvider provider, FakeHandler handler) MakeProvider()
    {
        var handler = new FakeHandler();
        var http = new HttpClient(handler);
        var cfg = new ProviderConfig
        {
            Id = "deepseek", DisplayName = "DeepSeek", Kind = "OpenAICompatible",
            BaseUrl = "https://api.deepseek.com/v1", DefaultModel = "deepseek-v4-flash",
            ApiKeyEnvVar = "DEEPSEEK_API_KEY"
        };
        Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", "sk-test");
        return (new DeepSeekProvider(http, cfg, NullLogger<DeepSeekProvider>.Instance), handler);
    }

    // ============== Summarizer ==============

    [Fact]
    public async Task Summarizer_CompressesInput()
    {
        var (provider, handler) = MakeProvider();
        handler.ResponseBody = "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"这是摘要\"}}]}";
        var sum = new Summarizer(provider, NullLogger<Summarizer>.Instance);
        var result = await sum.ExecuteAsync("很长很长的原文");
        Assert.Equal("这是摘要", result);
        Assert.NotNull(handler.LastSystemPrompt);
        Assert.Contains("编辑", handler.LastSystemPrompt);
        Assert.Contains("原文", handler.LastUserPrompt);
    }

    // ============== Translator ==============

    [Fact]
    public async Task Translator_TranslatesToTargetLanguage()
    {
        var (provider, handler) = MakeProvider();
        handler.ResponseBody = "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"Hello world\"}}]}";
        var tr = new Translator(provider, NullLogger<Translator>.Instance);
        var result = await tr.TranslateAsync("你好世界", "English");
        Assert.Equal("Hello world", result);
        Assert.Contains("English", handler.LastSystemPrompt);
    }

    [Fact]
    public async Task Translator_EmptyText_Throws()
    {
        var (provider, _) = MakeProvider();
        var tr = new Translator(provider, NullLogger<Translator>.Instance);
        await Assert.ThrowsAsync<System.ArgumentException>(() => tr.TranslateAsync("", "English"));
    }

    // ============== AutoTagger ==============

    [Fact]
    public async Task AutoTagger_ParsesJsonArray()
    {
        var (provider, handler) = MakeProvider();
        handler.ResponseBody = "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"[\\\"AI\\\", \\\"笔记\\\", \\\"学习\\\"]\"}}]}";
        var tagger = new AutoTagger(provider, NullLogger<AutoTagger>.Instance);
        var tags = await tagger.ExtractAsync("关于 AI 笔记");
        Assert.Equal(3, tags.Count);
        Assert.Contains("AI", tags);
    }

    [Fact]
    public async Task AutoTagger_HandlesMarkdownJsonWrapper()
    {
        var (provider, handler) = MakeProvider();
        handler.ResponseBody = "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"```json\\n[\\\"foo\\\", \\\"bar\\\"]\\n```\"}}]}";
        var tagger = new AutoTagger(provider, NullLogger<AutoTagger>.Instance);
        var tags = await tagger.ExtractAsync("anything");
        Assert.Equal(2, tags.Count);
    }

    [Fact]
    public async Task AutoTagger_EmptyContent_ReturnsEmpty()
    {
        var (provider, _) = MakeProvider();
        var tagger = new AutoTagger(provider, NullLogger<AutoTagger>.Instance);
        var tags = await tagger.ExtractAsync("");
        Assert.Empty(tags);
    }

    // ============== SemanticSearcher ==============

    [Fact]
    public async Task SemanticSearcher_RanksByRelevance()
    {
        var (provider, handler) = MakeProvider();
        // Note: 必须是合法 JSON 数组, 每个对象有 id (number) 和 score (number)
        handler.ResponseBody = "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"[{\\\"id\\\":2,\\\"score\\\":9.5},{\\\"id\\\":1,\\\"score\\\":3.2}]\"}}]}";
        var searcher = new SemanticSearcher(provider, NullLogger<SemanticSearcher>.Instance);
        var candidates = new List<SemanticSearcher.NoteCandidate>
        {
            new(1, "First", "first preview"),
            new(2, "Second", "second preview"),
        };
        var results = await searcher.SearchAsync("query", candidates, topK: 5);
        Assert.NotEmpty(results);
        Assert.Equal(2, results[0].Id);  // higher score first
        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task SemanticSearcher_EmptyCandidates_ReturnsEmpty()
    {
        var (provider, _) = MakeProvider();
        var searcher = new SemanticSearcher(provider, NullLogger<SemanticSearcher>.Instance);
        var r = await searcher.SearchAsync("q", new List<SemanticSearcher.NoteCandidate>());
        Assert.Empty(r);
    }

    // ============== QuestionAnswerer ==============

    [Fact]
    public async Task QuestionAnswerer_CitesNoteId()
    {
        var (provider, handler) = MakeProvider();
        handler.ResponseBody = "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"根据 #42 文档,答案是 42\"}}]}";
        var qa = new QuestionAnswerer(provider, NullLogger<QuestionAnswerer>.Instance);
        var notes = new List<(long, string, string)> { (42, "Answer", "the answer is 42") };
        var result = await qa.AnswerAsync("什么是答案?", notes);
        Assert.Contains("#42", result);
        Assert.Contains("42", handler.LastUserPrompt);
    }

    [Fact]
    public async Task QuestionAnswerer_NoCandidates_ReturnsGracefulMessage()
    {
        var (provider, _) = MakeProvider();
        var qa = new QuestionAnswerer(provider, NullLogger<QuestionAnswerer>.Instance);
        var result = await qa.AnswerAsync("q", new List<(long, string, string)>());
        Assert.Contains("无相关信息", result);
    }

    // ============== Writer ==============

    [Fact]
    public async Task Writer_GeneratesStructuredContent()
    {
        var (provider, handler) = MakeProvider();
        handler.ResponseBody = "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"# 标题\\n\\n- 要点1\\n- 要点2\"}}]}";
        var writer = new Writer(provider, NullLogger<Writer>.Instance);
        var result = await writer.ExecuteAsync("写一个 AI 入门");
        Assert.Contains("# 标题", result);
        Assert.Contains("写作", handler.LastSystemPrompt);
    }

    // ============== Common: invalid input ==============

    [Fact]
    public async Task Summarizer_EmptyInput_Throws()
    {
        var (provider, _) = MakeProvider();
        var sum = new Summarizer(provider, NullLogger<Summarizer>.Instance);
        await Assert.ThrowsAsync<System.ArgumentException>(() => sum.ExecuteAsync(""));
    }
}
