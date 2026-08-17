// Copyright (c) AeroCode V3.2
// SemanticSearcher — 真 embedding-based 语义检索（默认） + LLM rank 降级。
// 默认走 VectorStore + EmbeddingClient 的真 cosine 相似度 (Ollama / OpenAI-compatible /v1/embeddings)；
// 降级用 LLM 让模型给候选打分 (和原来一样, 但只在 EmbeddingClient 不可达时)。
// 零假装：要么真调 embedding HTTP，要么真调 LLM chat，never mock。
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AeroCode.AI.Models;
using AeroCode.AI.Providers;
using AeroCode.AI.Embedding;
using Microsoft.Extensions.Logging;

namespace AeroCode.AI.Capabilities;

public sealed class SemanticSearcher : ICapability
{
    private readonly IAiProvider _provider;
    private readonly ILogger<SemanticSearcher> _logger;
    private readonly EmbeddingClient? _embedding;
    private readonly VectorStore? _vectorStore;

    public string Name => "semantic_search";
    public string Description => "默认走真 embedding cosine 检索 (Ollama/OpenAI), 降级到 LLM rank";

    /// <summary>
    /// Default constructor: LLM-rank only (legacy).
    /// </summary>
    public SemanticSearcher(IAiProvider provider, ILogger<SemanticSearcher> logger)
    {
        _provider = provider;
        _logger = logger;
    }

    /// <summary>
    /// New constructor: pass an EmbeddingClient + VectorStore to enable embedding-based search.
    /// EmbeddingClient is real (calls Ollama/OpenAI HTTP /v1/embeddings). If null, falls back to LLM.
    /// </summary>
    public SemanticSearcher(IAiProvider provider, ILogger<SemanticSearcher> logger, EmbeddingClient embedding, VectorStore? store = null)
    {
        _provider = provider;
        _logger = logger;
        _embedding = embedding;
        _vectorStore = store ?? new VectorStore();
    }

    public async Task<IReadOnlyList<ScoredNote>> SearchAsync(string query, IReadOnlyList<NoteCandidate> candidates, int topK = 5, CancellationToken ct = default)
    {
        if (candidates.Count == 0) return Array.Empty<ScoredNote>();
        if (string.IsNullOrWhiteSpace(query)) return Array.Empty<ScoredNote>();

        // Path A: real embedding-based cosine search (preferred).
        if (_embedding is not null && _vectorStore is not null)
        {
            try
            {
                return await SearchByEmbeddingAsync(query, candidates, topK, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "embedding path failed, falling back to LLM rank");
                // fall through to LLM path
            }
        }

        // Path B: LLM rank (legacy, real LLM call, no mock).
        return await SearchByLlmAsync(query, candidates, topK, ct);
    }

    private async Task<IReadOnlyList<ScoredNote>> SearchByEmbeddingAsync(string query, IReadOnlyList<NoteCandidate> candidates, int topK, CancellationToken ct)
    {
        // 1) Real embedding for query + every candidate preview (HTTP /api/embeddings).
        var queryVec = await _embedding!.EmbedAsync(query, ct);
        // Re-embed if any candidate vector is missing or dim mismatch.
        var needRebuild = false;
        foreach (var c in candidates)
        {
            var existing = _vectorStore!.Get(c.Id.ToString());
            if (existing is null || existing.Vector.Length != queryVec.Length) { needRebuild = true; break; }
        }
        if (needRebuild)
        {
            foreach (var c in candidates)
            {
                var key = c.Id.ToString();
                if (_vectorStore!.Get(key) is null)
                {
                    var text = $"{(c.Title ?? "").Trim()}\n{c.ContentPreview ?? ""}".Trim();
                    if (text.Length == 0) continue;
                    var v = await _embedding.EmbedAsync(text, ct);
                    _vectorStore.Add(new EmbeddingRecord { Id = key, Text = text, Vector = v });
                }
            }
        }
        // 2) Real cosine top-K (no LLM).
        var hits = _vectorStore!.Search(queryVec, topK, minScore: -1.0);
        var lookup = candidates.ToDictionary(c => c.Id);
        var scored = new List<ScoredNote>();
        foreach (var h in hits)
        {
            if (!lookup.TryGetValue(long.Parse(h.Id), out var c)) continue;
            scored.Add(new ScoredNote(c.Id, c.Title, h.Score));
        }
        return scored;
    }

    private async Task<IReadOnlyList<ScoredNote>> SearchByLlmAsync(string query, IReadOnlyList<NoteCandidate> candidates, int topK, CancellationToken ct)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("[查询]");
        sb.AppendLine(query);
        sb.AppendLine();
        sb.AppendLine("[候选笔记] (id | 标题 | 摘要前 200 字)");
        foreach (var c in candidates)
        {
            var preview = (c.ContentPreview ?? "").Length > 200 ? c.ContentPreview![..200] : c.ContentPreview;
            sb.AppendLine($"- {c.Id} | {c.Title} | {preview}");
        }
        sb.AppendLine();
        sb.AppendLine($"[要求] 按相关性给每条 0-10 分,选 top {topK}。仅输出 JSON 数组,格式: [{{\"id\":1,\"score\":8.5}},...]");

        var req = new ChatRequest
        {
            Model = string.Empty,
            Messages = new[]
            {
                new ChatMessage { Role = "system", Content = "你是相关性排序助手。客观评估每条候选与查询的语义相关性,避免标题党。" },
                new ChatMessage { Role = "user", Content = sb.ToString() }
            },
            Stream = false,
            Temperature = 0.1,
            MaxTokens = 1024,
            EnableThinking = false
        };
        var resp = await _provider.ChatAsync(req, ct);
        var text = (resp.Content ?? "").Trim();
        if (text.StartsWith("```"))
        {
            var s = text.IndexOf('[');
            var e = text.LastIndexOf(']');
            if (s >= 0 && e > s) text = text[s..(e + 1)];
        }
        var parseOpts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        try
        {
            var arr = JsonSerializer.Deserialize<List<ScoredItem>>(text, parseOpts) ?? new();
            var lookup = candidates.ToDictionary(c => c.Id);
            return arr
                .Where(s => lookup.ContainsKey(s.Id))
                .OrderByDescending(s => s.Score)
                .Take(topK)
                .Select(s => new ScoredNote(lookup[s.Id].Id, lookup[s.Id].Title, s.Score))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SemanticSearcher LLM parse failed: {Text}", text);
            return Array.Empty<ScoredNote>();
        }
    }

    public sealed record NoteCandidate(long Id, string Title, string? ContentPreview);
    public sealed record ScoredNote(long Id, string Title, double Score);
    private sealed class ScoredItem
    {
        public long Id { get; set; }
        public double Score { get; set; }
    }
}
