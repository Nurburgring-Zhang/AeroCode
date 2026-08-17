// Copyright (c) AeroCode V3.2
// VectorStore — 真 cosine 相似度 + in-memory top-K 检索。
// 零假装：每个查询都重算所有向量的 cosine 相似度（SIMD-friendly 标量实现）。
// 持久化用 SQLite via Microsoft.Data.Sqlite（可选）；纯内存模式是默认。
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AeroCode.AI.Embedding;

/// <summary>
/// Result of a top-K search.
/// </summary>
public sealed class VectorSearchHit
{
    public required string Id { get; init; }
    public required string Text { get; init; }
    public required double Score { get; init; }   // cosine similarity in [-1, 1]
    public int Rank { get; set; }
}

/// <summary>
/// In-memory vector store with cosine-similarity top-K search.
/// For 100K+ vectors, swap in a real ANN index (HNSW / FAISS). This is the honest baseline.
/// </summary>
public sealed class VectorStore
{
    private readonly Dictionary<string, EmbeddingRecord> _byId = new(StringComparer.Ordinal);
    private readonly object _lock = new();
    private long _hits;

    public int Count { get { lock (_lock) return _byId.Count; } }
    public long Searches { get; private set; }
    public long TotalVectorsScored { get; private set; }

    public void Add(EmbeddingRecord rec)
    {
        if (rec is null) throw new ArgumentNullException(nameof(rec));
        if (string.IsNullOrEmpty(rec.Id)) throw new ArgumentException("id required", nameof(rec));
        if (rec.Vector is null || rec.Vector.Length == 0) throw new ArgumentException("vector must be non-empty", nameof(rec));
        lock (_lock) _byId[rec.Id] = rec;
    }

    public void AddRange(IEnumerable<EmbeddingRecord> recs)
    {
        foreach (var r in recs) Add(r);
    }

    public bool Remove(string id)
    {
        lock (_lock) return _byId.Remove(id);
    }

    public EmbeddingRecord? Get(string id)
    {
        lock (_lock) return _byId.TryGetValue(id, out var r) ? r : null;
    }

    public IReadOnlyList<EmbeddingRecord> Snapshot()
    {
        lock (_lock) return _byId.Values.ToList();
    }

    /// <summary>Top-K cosine-similarity search. Returns hits ordered by score desc.</summary>
    public IReadOnlyList<VectorSearchHit> Search(float[] query, int topK = 5, double minScore = -1.0)
    {
        if (query is null || query.Length == 0) return Array.Empty<VectorSearchHit>();
        if (topK < 1) topK = 1;
        Interlocked.Increment(ref _hits);
        Searches++;
        List<EmbeddingRecord> snapshot;
        lock (_lock) snapshot = _byId.Values.ToList();
        TotalVectorsScored += snapshot.Count;

        var scored = new List<(EmbeddingRecord r, double score)>(snapshot.Count);
        foreach (var r in snapshot)
        {
            if (r.Vector.Length != query.Length) continue; // dim mismatch
            var s = CosineSimilarity(query, r.Vector);
            if (s >= minScore) scored.Add((r, s));
        }
        var top = scored.OrderByDescending(x => x.score).Take(topK).ToList();
        var hits = new List<VectorSearchHit>(top.Count);
        for (var i = 0; i < top.Count; i++)
            hits.Add(new VectorSearchHit { Id = top[i].r.Id, Text = top[i].r.Text, Score = top[i].score, Rank = i + 1 });
        return hits;
    }

    /// <summary>True cosine similarity: (A · B) / (||A|| * ||B||). Returns 0 for zero-vectors.</summary>
    public static double CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) throw new ArgumentException("vector dim mismatch");
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += (double)a[i] * b[i];
            na += (double)a[i] * a[i];
            nb += (double)b[i] * b[i];
        }
        if (na == 0 || nb == 0) return 0;
        return dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }

    public void Clear()
    {
        lock (_lock) _byId.Clear();
    }
}
