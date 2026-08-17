// Copyright (c) AeroCode V3.0
// RealLLMSmoke — 真 LLM 烟囱测试,直打 minimax api。
// Gate:  env var MINIMAX_API_KEY 必须存在(空值则 SKIP)
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using AeroCode.AI.Capabilities;
using AeroCode.AI.Configuration;
using AeroCode.AI.Models;
using AeroCode.AI.Providers;
using AeroCode.AI.Resilience;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Sdk;

namespace AeroCode.Tests.RealLLM;

/// <summary>
/// R-R1: 真 LLM 连接 — 1 个 chat completion, 验证 endpoint/key/model 工作。
/// 如果 MINIMAX_API_KEY 未设置,整个 fixture 跳过 (Xunit skip via Assert.Skip)。
/// </summary>
[CollectionDefinition("RealLLM", DisableParallelization = true)]
public class RealLLMSmoke
{
    public static bool Enabled => !string.IsNullOrWhiteSpace(
        Environment.GetEnvironmentVariable("MINIMAX_API_KEY"));

    public static (IAiProvider provider, string envKey) CreateMiniMaxProvider()
    {
        var apiKey = Environment.GetEnvironmentVariable("MINIMAX_API_KEY")!;
        var cfg = new ProviderConfig
        {
            Id = "minimax",
            DisplayName = "MiniMax M2 (real)",
            Kind = "OpenAICompatible",
            BaseUrl = "https://api.minimaxi.com/v1",
            DefaultModel = "MiniMax-M2",
            ApiKeyEnvVar = "MINIMAX_API_KEY",
            RequiresApiKey = true,
            // reasoning_split=true: 把 thinking 分离到 reasoning_content 字段
            ExtraBody = new Dictionary<string, object> { ["reasoning_split"] = true }
        };
        var http = new HttpClient();
        return (new MiniMaxProvider(http, cfg, NullLogger<MiniMaxProvider>.Instance,
            new AiResiliencePipeline(new ResilienceOptions { MaxRetryAttempts = 0, CircuitBreakerMinThroughput = 0 })), apiKey);
    }

    [SkippableFact]
    public async Task R1_Chat_RoundTrip()
    {
        Skip.IfNot(Enabled, "MINIMAX_API_KEY not set");
        var (p, _) = CreateMiniMaxProvider();
        // Retry up to 3 times for transient rate-limit / empty response
        ChatResponse? resp = null;
        for (var i = 0; i < 3; i++)
        {
            try
            {
                resp = await p.ChatAsync(new ChatRequest
                {
                    Model = "MiniMax-M2",
                    Messages = new[] { new ChatMessage { Role = "user", Content = "1+1=?" } },
                    MaxTokens = 200,
                    Temperature = 0.1
                });
                if (!string.IsNullOrEmpty(resp?.Content) && (resp.Content.Contains("2") || resp.Content.Contains("二"))) break;
            }
            catch { }
            await Task.Delay(2000);
        }
        Assert.NotNull(resp);
        Assert.NotEmpty(resp!.Content);
        // Real Chinese reasoning: must contain "2" (answer) or "二"
        var ok = resp.Content.Contains("2") || resp.Content.Contains("二");
        Assert.True(ok, $"Real LLM should answer '2' or '二', got: {resp.Content}");
        Assert.NotNull(resp.Usage);
        Assert.True(resp.Usage!.TotalTokens > 0);
    }

    [SkippableFact]
    public async Task R2_Summarizer_RealLLM()
    {
        Skip.IfNot(Enabled, "MINIMAX_API_KEY not set");
        var (p, _) = CreateMiniMaxProvider();
        var s = new Summarizer(p, NullLogger<Summarizer>.Instance);
        var longText = "深度学习是机器学习的一个分支,它使用多层神经网络来学习数据的层次化表示。深度学习在图像识别、自然语言处理、语音识别等领域取得了突破性进展。卷积神经网络 (CNN) 擅长处理图像数据,循环神经网络 (RNN) 擅长处理序列数据,Transformer 架构则彻底改变了 NLP 领域。";
        var result = await s.ExecuteAsync(longText);
        Assert.NotEmpty(result);
        // Summary should be shorter than original
        Assert.True(result.Length < longText.Length, $"Summary too long: {result.Length} chars (orig {longText.Length})");
    }

    [SkippableFact]
    public async Task R3_Translator_RealLLM()
    {
        Skip.IfNot(Enabled, "MINIMAX_API_KEY not set");
        var (p, _) = CreateMiniMaxProvider();
        var t = new Translator(p, NullLogger<Translator>.Instance);
        var result = await t.TranslateAsync("人工智能正在改变世界", "English");
        Assert.NotEmpty(result);
        // Should mention key concepts in English
        var has = result.Contains("AI", StringComparison.OrdinalIgnoreCase) ||
                  result.Contains("intelligence", StringComparison.OrdinalIgnoreCase) ||
                  result.Contains("artificial", StringComparison.OrdinalIgnoreCase);
        Assert.True(has, $"Translation should be English, got: {result}");
    }

    [SkippableFact]
    public async Task R4_AutoTagger_RealLLM()
    {
        Skip.IfNot(Enabled, "MINIMAX_API_KEY not set");
        var (p, _) = CreateMiniMaxProvider();
        var t = new AutoTagger(p, NullLogger<AutoTagger>.Instance);
        var tags = await t.ExtractAsync("深度学习中的反向传播算法使用梯度下降优化神经网络参数");
        Assert.NotEmpty(tags);
        // Should have AI-related tags
        var has = tags.Any(x => x.Contains("AI") || x.Contains("深度") || x.Contains("学习") || x.Contains("算法") || x.Contains("网络"));
        Assert.True(has, $"Tags should include AI/deep-learning related, got: {string.Join(",", tags)}");
    }

    [SkippableFact]
    public async Task R5_QuestionAnswerer_RealLLM()
    {
        Skip.IfNot(Enabled, "MINIMAX_API_KEY not set");
        var (p, _) = CreateMiniMaxProvider();
        var qa = new QuestionAnswerer(p, NullLogger<QuestionAnswerer>.Instance);
        var r = await qa.AnswerAsync("Python 是什么?", new List<(long, string, string)>
        {
            (1, "Python 简介", "Python 是一种广泛使用的高级编程语言, 由 Guido van Rossum 创建, 语法简洁清晰, 适合初学者和专业开发者。"),
            (2, "Python 用途", "Python 用于 Web 开发、数据科学、机器学习、自动化脚本等。")
        });
        Assert.NotEmpty(r);
        // Should mention Python
        Assert.Contains("Python", r);
    }

    [SkippableFact]
    public async Task R6_SemanticSearcher_RealLLM()
    {
        Skip.IfNot(Enabled, "MINIMAX_API_KEY not set");
        var (p, _) = CreateMiniMaxProvider();
        var s = new SemanticSearcher(p, NullLogger<SemanticSearcher>.Instance);
        var candidates = new List<SemanticSearcher.NoteCandidate>
        {
            new(1, "苹果是水果", "苹果是一种常见的水果, 富含维生素 C。"),
            new(2, "深度学习教程", "深度学习是机器学习的一个子集, 使用神经网络。"),
            new(3, "北京旅游", "北京是中国的首都, 有故宫、长城等景点。")
        };
        var results = await s.SearchAsync("神经网络", candidates, 2);
        Assert.NotEmpty(results);
        // The deep-learning candidate (#2) should rank first
        Assert.Equal(2, results[0].Id);
    }

    [SkippableFact]
    public async Task R7_Writer_RealLLM()
    {
        Skip.IfNot(Enabled, "MINIMAX_API_KEY not set");
        var (p, _) = CreateMiniMaxProvider();
        var w = new Writer(p, NullLogger<Writer>.Instance);
        var result = await w.ExecuteAsync("Python Hello World");
        Assert.NotEmpty(result);
        // Should have some code-like content
        var has = result.Contains("print") || result.Contains("Hello") || result.Contains("python") || result.Contains("Python");
        Assert.True(has, $"Writer should output Python code, got: {result}");
    }
}
