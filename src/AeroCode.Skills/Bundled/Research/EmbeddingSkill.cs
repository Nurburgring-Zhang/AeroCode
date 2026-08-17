// Copyright (c) AeroCode V3.2
// EmbeddingSkill — 真 embedding HTTP + cosine top-K 检索。
// 零假装：每次调用都发 HTTP POST 到 Ollama (默认) 或 OpenAI-compatible /v1/embeddings。
// Args:
//   mode=embed text=<text>                         # 返回 384 维向量
//   mode=crawl  text=<text> top_k=<int>            # 单条 + 在 store 里搜 top-K
//   mode=upsert  id=<str> text=<text>              # 加进 vector store
//   mode=remove  id=<str>                          # 移除
//   mode=stats                                    # 当前 store 统计
//   backend=ollama|openai (default ollama)
//   base_url=<url>  model=<model>  api_key_env=<env>
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AeroCode.AI.Embedding;
using AeroCode.Skills.Models;
using AeroCode.Skills.Registry;

namespace AeroCode.Skills.Bundled.Research;

public sealed class EmbeddingSkill : ISkill
{
    public string Id => "research/embedding";
    public string Name => "Embedding (real HTTP)";
    public string Description => "真 HTTP 调 Ollama/OpenAI-compatible /v1/embeddings 拿 384 维向量 + cosine top-K 检索";
    public string Category => "research";
    public string Author => "AeroCode Team (human first, Hermes rule)";
    public string Version => "1.0.0";
    public IReadOnlyList<string> Tags => new[] { "embedding", "vector", "semantic", "ollama", "openai" };
    public bool IsAvailable() => true;

    public string GetSystemPrompt() =>
        "# Embedding Skill (real HTTP /api/embeddings or /v1/embeddings)\n" +
        "Args:\n" +
        "  mode=embed text=<text>                     # raw vector (JSON array)\n" +
        "  mode=upsert id=<str> text=<text>           # add to in-memory VectorStore\n" +
        "  mode=remove id=<str>                       # remove\n" +
        "  mode=stats                                 # store stats\n" +
        "  mode=query text=<text> top_k=<int>         # cosine top-K over the store\n" +
        "  backend=ollama|openai (default ollama)\n" +
        "  base_url=http://localhost:11434  model=all-minilm-l6-v2\n" +
        "  api_key_env=OPENAI_API_KEY (only for backend=openai)";

    // Singleton store for the skill — shared across calls (this is the AI assistant's "RAG" memory).
    private static readonly VectorStore _store = new();
    private static readonly object _storeLock = new();

    public async Task<SkillResult> ExecuteAsync(SkillInput input, SkillContext ctx, CancellationToken ct = default)
    {
        var args = input.Args ?? new Dictionary<string, object?>();
        var mode = ((args.TryGetValue("mode", out var m) ? m as string : null) ?? "embed").ToLowerInvariant();
        var backend = (args.TryGetValue("backend", out var b) && b as string == "openai")
            ? EmbeddingBackend.OpenAICompatible : EmbeddingBackend.Ollama;
        var baseUrl = (args.TryGetValue("base_url", out var bu) ? bu as string : null) ?? "http://localhost:11434";
        var model = (args.TryGetValue("model", out var mo) ? mo as string : null) ?? "all-minlm-l6-v2";
        var apiKeyEnv = args.TryGetValue("api_key_env", out var ake) ? ake as string : null;

        var client = new EmbeddingClient(new EmbeddingClientOptions
        {
            BaseUrl = baseUrl!,
            Model = model!,
            Backend = backend,
            ApiKeyEnvVar = apiKeyEnv
        });
        try
        {
            return mode switch
            {
                "embed" => await EmbedAsync(client, args, ct),
                "upsert" => await Upsert(client, args, ct),
                "remove" => Remove(args, ct),
                "stats" => Stats(),
                "query" => await QueryAsync(client, args, ct),
                _ => new SkillResult { Success = false, Text = $"Unknown mode: {mode}" }
            };
        }
        catch (Exception ex)
        {
            return new SkillResult { Success = false, Text = $"{ex.GetType().Name}: {ex.Message}" };
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    private static async Task<SkillResult> EmbedAsync(EmbeddingClient client, IReadOnlyDictionary<string, object?> args, CancellationToken ct)
    {
        var text = args.TryGetValue("text", out var t) ? t as string : null;
        if (string.IsNullOrEmpty(text)) return new SkillResult { Success = false, Text = "需要 'text' 参数" };
        var vec = await client.EmbedAsync(text, ct);
        return new SkillResult
        {
            Success = true,
            Text = $"# Embedding (dim={vec.Length}, backend={client.Options.Backend}, model={client.Options.Model})\n{JsonSerializer.Serialize(vec)}",
            Data = vec
        };
    }

    private static async Task<SkillResult> Upsert(EmbeddingClient client, IReadOnlyDictionary<string, object?> args, CancellationToken ct)
    {
        var id = args.TryGetValue("id", out var i) ? i as string : null;
        var text = args.TryGetValue("text", out var t) ? t as string : null;
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(text))
            return new SkillResult { Success = false, Text = "需要 'id' 和 'text' 参数" };
        var vec = await client.EmbedAsync(text, ct);
        lock (_storeLock) _store.Add(new EmbeddingRecord { Id = id, Text = text, Vector = vec });
        return new SkillResult { Success = true, Text = $"Upserted id={id} dim={vec.Length} text-len={text.Length}" };
    }

    private static SkillResult Remove(IReadOnlyDictionary<string, object?> args, CancellationToken ct)
    {
        var id = args.TryGetValue("id", out var i) ? i as string : null;
        if (string.IsNullOrEmpty(id)) return new SkillResult { Success = false, Text = "需要 'id' 参数" };
        lock (_storeLock)
        {
            var ok = _store.Remove(id);
            return new SkillResult { Success = ok, Text = ok ? $"Removed id={id}" : $"id={id} not found" };
        }
    }

    private static SkillResult Stats()
    {
        lock (_storeLock)
        {
            return new SkillResult
            {
                Success = true,
                Text = $"# VectorStore\n- Count: {_store.Count}\n- Searches: {_store.Searches}\n- Total vectors scored: {_store.TotalVectorsScored}"
            };
        }
    }

    private static async Task<SkillResult> QueryAsync(EmbeddingClient client, IReadOnlyDictionary<string, object?> args, CancellationToken ct)
    {
        var text = args.TryGetValue("text", out var t) ? t as string : null;
        var topK = args.TryGetValue("top_k", out var k) && k is not null ? Convert.ToInt32(k) : 5;
        if (string.IsNullOrEmpty(text)) return new SkillResult { Success = false, Text = "需要 'text' 参数" };
        var vec = await client.EmbedAsync(text, ct);
        List<EmbeddingRecord> snapshot;
        lock (_storeLock) snapshot = _store.Snapshot().ToList();
        // We want a transient search — use a new VectorStore to avoid mutating shared state.
        var transient = new VectorStore();
        transient.AddRange(snapshot);
        var hits = transient.Search(vec, topK);
        var sb = new StringBuilder();
        sb.AppendLine($"# Top-{topK} for: {text}");
        sb.AppendLine();
        foreach (var h in hits)
            sb.AppendLine($"- {h.Id} (score {h.Score:F4}) — {h.Text[..Math.Min(80, h.Text.Length)]}");
        return new SkillResult { Success = true, Text = sb.ToString() };
    }
}
