// Copyright (c) AeroCode
// A3 ContextCurator / FactAssertionDetector 单测（R1 波次 β）：
// 冻结前缀不动 / 水位触发 / 摘要模板 / 状态外置 / 断言检测正反例。
using AeroAgent.Moa.Curation;
using AeroCode.AI.Models;
using AeroCode.Harness.Compaction;
using Xunit;

namespace AeroCode.Tests.MoaTests;

public sealed class CurationTests
{
    private static ChatMessage Msg(string role, string content, string? toolCallId = null)
        => new() { Role = role, Content = content, ToolCallId = toolCallId };

    /// <summary>无断言填充消息（长文本无数字/实体/谓词，不污染事实清单断言）。</summary>
    private static List<ChatMessage> LongConversation(int count, int charsPerMessage = 2000)
        => Enumerable.Range(0, count)
            .Select(_ => Msg("user", new string('x', charsPerMessage)))
            .ToList();

    private static ContextCurator TruncatingCurator(int keep = 3, int threshold = 1000)
        => new(new CurationOptions
        {
            WatermarkThresholdTokens = threshold,
            KeepRecentMessages = keep,
            Strategy = CompactionStrategy.TruncateOldest,
        });

    // ---------- 水位触发 ----------

    [Fact]
    public void Curate_BelowWatermark_NoOp()
    {
        var curator = new ContextCurator(new CurationOptions { WatermarkThresholdTokens = 100_000 });
        var messages = LongConversation(4);
        var result = curator.Curate(new CurationInput { Conversation = messages, FrozenPrefixCount = 1 });

        Assert.False(result.DidCurate);
        Assert.Empty(result.ExternalStateBlock);
        Assert.Empty(result.OverflowedFacts);
        Assert.Same(messages, result.Conversation);
    }

    [Fact]
    public void Curate_DisabledThreshold_NoOp()
    {
        var curator = new ContextCurator(new CurationOptions { WatermarkThresholdTokens = 0 });
        var messages = LongConversation(10);
        var result = curator.Curate(new CurationInput { Conversation = messages });

        Assert.False(result.DidCurate);
        Assert.Contains("disabled", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Curate_WatermarkTriggers_Shrinks()
    {
        var messages = LongConversation(30);
        var result = TruncatingCurator(keep: 5).Curate(new CurationInput { Conversation = messages });

        Assert.True(result.DidCurate);
        Assert.True(result.CuratedTokens < result.OriginalTokens);
        // 前缀 0 + 状态块 1 + 保留窗口 5。
        Assert.Equal(6, result.Conversation.Count);
    }

    // ---------- 冻结前缀一字不动 ----------

    [Fact]
    public void Curate_FrozenPrefix_Untouched()
    {
        var prefix = new List<ChatMessage>
        {
            Msg("system", "稳定 system 段（含工具与 skill 稳定段）。"),
            Msg("user", "首条稳定指令。"),
        };
        var messages = new List<ChatMessage>(prefix);
        for (var i = 0; i < 30; i++)
        {
            messages.Add(Msg("user", new string('x', 2000)));
        }

        var result = TruncatingCurator(keep: 4).Curate(new CurationInput { Conversation = messages, FrozenPrefixCount = 2 });

        Assert.True(result.DidCurate);
        // 前缀逐条同实例、同顺序（prompt 缓存不受损的硬语义）。
        Assert.Same(prefix[0], result.Conversation[0]);
        Assert.Same(prefix[1], result.Conversation[1]);
        // 外置状态块紧随前缀。
        Assert.Equal("system", result.Conversation[2].Role);
        Assert.Equal(result.ExternalStateBlock, result.Conversation[2].Content);
    }

    // ---------- 摘要模板 / 状态外置 ----------

    [Fact]
    public void Curate_StateBlock_TemplateAndCap()
    {
        var messages = LongConversation(30);
        var result = TruncatingCurator(keep: 5).Curate(new CurationInput { Conversation = messages });
        var block = result.ExternalStateBlock;

        Assert.True(block.Length <= 400);
        Assert.StartsWith("<curated-state>", block, StringComparison.Ordinal);
        Assert.Contains("[任务状态]", block, StringComparison.Ordinal);
        Assert.Contains("[溢出事实]", block, StringComparison.Ordinal);
        Assert.EndsWith("</curated-state>", block, StringComparison.Ordinal);
    }

    [Fact]
    public void Curate_StateBlock_HardCapped_WithManyFacts()
    {
        var messages = Enumerable.Range(0, 40)
            .Select(i => Msg("user", $"统计共 {1000 + i} 个文件。" + new string('x', 100)))
            .ToList();
        var result = TruncatingCurator(keep: 2).Curate(new CurationInput { Conversation = messages });

        Assert.True(result.DidCurate);
        Assert.True(result.ExternalStateBlock.Length <= 400);
    }

    [Fact]
    public void Curate_StateBlock_ScrubsSecrets()
    {
        var messages = LongConversation(30);
        // secret 必须落在压缩区（保留窗 keep:3 只留末尾 3 条，放末尾进不了状态块）。
        messages.Insert(0, Msg("assistant", "环境变量读取完成 api_key = sk-abc123def456ghi789jkl"));
        var result = TruncatingCurator(keep: 3).Curate(new CurationInput { Conversation = messages });

        Assert.True(result.DidCurate);
        Assert.DoesNotContain("sk-abc123", result.ExternalStateBlock, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", result.ExternalStateBlock, StringComparison.Ordinal);
        Assert.All(result.OverflowedFacts, f => Assert.DoesNotContain("sk-abc123", f, StringComparison.Ordinal));
    }

    // ---------- 溢出事实清单 ----------

    [Fact]
    public void Curate_OverflowFacts_UngroundedListed_GroundedSkipped()
    {
        var messages = new List<ChatMessage>
        {
            Msg("user", "统计共 128 个文件。"),
            Msg("user", "截止 2026-09-03 完成 77 个用例。"),
        };
        for (var i = 0; i < 18; i++)
        {
            messages.Add(Msg("user", new string('x', 2000)));
        }

        messages.Add(Msg("user", "保留区：共 128 个文件。"));
        messages.Add(Msg("user", new string('x', 2000)));
        messages.Add(Msg("user", new string('x', 2000)));

        var result = TruncatingCurator(keep: 3).Curate(new CurationInput { Conversation = messages });

        Assert.True(result.DidCurate);
        Assert.Contains(result.OverflowedFacts, f => f.Contains("2026-09-03", StringComparison.Ordinal));
        Assert.Contains(result.OverflowedFacts, f => f.Contains("77", StringComparison.Ordinal));
        // "128 个"在保留窗口中有对应证据 → 已佐证，不列溢出。
        Assert.DoesNotContain(result.OverflowedFacts, f => f.Contains("128", StringComparison.Ordinal));
    }

    // ---------- LLM 摘要注入 ----------

    [Fact]
    public void Curate_LlmSummarizer_UsedInTemplate()
    {
        var messages = LongConversation(30);
        var curator = new ContextCurator(new CurationOptions
        {
            WatermarkThresholdTokens = 1000,
            KeepRecentMessages = 3,
            Strategy = CompactionStrategy.LlmSummarize,
            Summarizer = _ => Task.FromResult("已完成三批构建任务"),
        });
        var result = curator.Curate(new CurationInput { Conversation = messages });

        Assert.True(result.DidCurate);
        Assert.Contains("[摘要] 已完成三批构建任务", result.ExternalStateBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void Curate_LlmSummarizer_Throws_DegradesWithoutLeaking()
    {
        var messages = LongConversation(30);
        var curator = new ContextCurator(new CurationOptions
        {
            WatermarkThresholdTokens = 1000,
            KeepRecentMessages = 3,
            Summarizer = _ => throw new InvalidOperationException("gateway down"),
        });
        var result = curator.Curate(new CurationInput { Conversation = messages });

        Assert.True(result.DidCurate);
        Assert.DoesNotContain("gateway down", result.ExternalStateBlock, StringComparison.Ordinal);
    }

    // ---------- 窗口自洽 ----------

    [Fact]
    public void Curate_WindowHead_ToolResponse_ExtendsToCarrier()
    {
        var messages = LongConversation(10);
        messages.Add(Msg("assistant", "调用工具列出目录。"));
        messages.Add(Msg("tool", new string('y', 2000), toolCallId: "call-1"));

        var result = TruncatingCurator(keep: 1).Curate(new CurationInput { Conversation = messages });

        Assert.True(result.DidCurate);
        // 窗口左扩到 assistant 携带方：tool 应答不再位于窗口头（携带方在场，配对自洽）。
        var tail = result.Conversation.Skip(1).ToList();
        Assert.Equal(2, tail.Count);
        Assert.Equal("assistant", tail[0].Role);
        Assert.Equal("tool", tail[1].Role);
    }

    // ---------- FactAssertionDetector 正反例 ----------

    [Fact]
    public void Detect_NumberAssertion_WithoutEvidence_Flagged()
    {
        var found = FactAssertionDetector.Detect("构建共 128 个文件。");
        Assert.Contains(found, a => a.Kind == FactAssertionKind.Number
            && a.MatchedText.Contains("128", StringComparison.Ordinal));
    }

    [Fact]
    public void Detect_NumberAssertion_WithEvidence_NotFlagged()
    {
        var found = FactAssertionDetector.Detect("构建共 128 个文件。", new[] { "工具输出：构建共 128 个文件" });
        Assert.DoesNotContain(found, a => a.MatchedText.Contains("128", StringComparison.Ordinal));
    }

    [Fact]
    public void Detect_DateAssertion_Flagged_And_Grounded()
    {
        var flagged = FactAssertionDetector.Detect("发布日期是 2026-09-03。");
        Assert.Contains(flagged, a => a.Kind == FactAssertionKind.Date);

        var grounded = FactAssertionDetector.Detect("发布日期是 2026-09-03。", new[] { "changelog: 2026-09-03 released" });
        Assert.DoesNotContain(grounded, a => a.Kind == FactAssertionKind.Date);
    }

    [Fact]
    public void Detect_EntityAssertion_Flagged_And_Grounded()
    {
        var flagged = FactAssertionDetector.Detect("Kubernetes 支持横向自动扩容。");
        Assert.Contains(flagged, a => a.Kind == FactAssertionKind.NamedEntity
            && string.Equals(a.MatchedText, "Kubernetes", StringComparison.Ordinal));

        var grounded = FactAssertionDetector.Detect("Kubernetes 支持横向自动扩容。", new[] { "Kubernetes 官方文档" });
        Assert.DoesNotContain(grounded, a => a.Kind == FactAssertionKind.NamedEntity);
    }

    [Fact]
    public void Detect_CodeFence_Ignored()
    {
        Assert.Empty(FactAssertionDetector.Detect("```\ncount is 9999\n```"));
    }

    [Fact]
    public void Detect_NonAssertiveText_Empty()
    {
        Assert.Empty(FactAssertionDetector.Detect("今天天气不错，适合散步。"));
    }

    [Fact]
    public void Detect_NullOrEmpty_Empty()
    {
        Assert.Empty(FactAssertionDetector.Detect(null));
        Assert.Empty(FactAssertionDetector.Detect(string.Empty));
    }
}
