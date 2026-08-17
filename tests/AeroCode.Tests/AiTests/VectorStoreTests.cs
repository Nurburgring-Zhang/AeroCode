// Copyright (c) AeroCode V3.2
// VectorStore + EmbeddingClient + Roslyn + Otel + Meai tests
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AeroCode.AI.Embedding;
using AeroCode.AI.Integration;
using AeroCode.AI.Providers;
using AeroCode.AI.Telemetry;
using AeroCode.Skills.Bundled.Analysis;
using AeroCode.Skills.Models;
using AeroCode.Skills.Registry;
using Microsoft.Extensions.AI;
using Xunit;

namespace AeroCode.Tests.AiTests;

public class VectorStoreTests
{
    [Fact]
    public void CosineSimilarity_SameVector_Returns1()
    {
        var a = new float[] { 1, 0, 0 };
        var b = new float[] { 1, 0, 0 };
        Assert.Equal(1.0, VectorStore.CosineSimilarity(a, b), 5);
    }

    [Fact]
    public void CosineSimilarity_Orthogonal_Returns0()
    {
        var a = new float[] { 1, 0, 0 };
        var b = new float[] { 0, 1, 0 };
        Assert.Equal(0.0, VectorStore.CosineSimilarity(a, b), 5);
    }

    [Fact]
    public void CosineSimilarity_Opposite_ReturnsNegative1()
    {
        var a = new float[] { 1, 0, 0 };
        var b = new float[] { -1, 0, 0 };
        Assert.Equal(-1.0, VectorStore.CosineSimilarity(a, b), 5);
    }

    [Fact]
    public void CosineSimilarity_DimMismatch_Throws()
    {
        var a = new float[] { 1, 0 };
        var b = new float[] { 1, 0, 0 };
        Assert.Throws<ArgumentException>(() => VectorStore.CosineSimilarity(a, b));
    }

    [Fact]
    public void Add_Get_Remove_StoreRoundTrip()
    {
        var s = new VectorStore();
        s.Add(new EmbeddingRecord { Id = "x", Text = "x text", Vector = new float[] { 1, 0, 0 } });
        Assert.NotNull(s.Get("x"));
        Assert.True(s.Remove("x"));
        Assert.Null(s.Get("x"));
    }

    [Fact]
    public void Search_ReturnsTopK_OrderedByScoreDesc()
    {
        var s = new VectorStore();
        s.Add(new EmbeddingRecord { Id = "a", Text = "a", Vector = new float[] { 1, 0 } });
        s.Add(new EmbeddingRecord { Id = "b", Text = "b", Vector = new float[] { 0.9f, 0.1f } });
        s.Add(new EmbeddingRecord { Id = "c", Text = "c", Vector = new float[] { 0, 1 } });
        var hits = s.Search(new float[] { 1, 0 }, topK: 2);
        Assert.Equal(2, hits.Count);
        Assert.Equal("a", hits[0].Id);   // score ~1.0
        Assert.Equal("b", hits[1].Id);   // score ~0.99
        Assert.True(hits[0].Score > hits[1].Score);
    }

    [Fact]
    public void Search_DimMismatch_SkipsRecord()
    {
        var s = new VectorStore();
        s.Add(new EmbeddingRecord { Id = "good", Text = "g", Vector = new float[] { 1, 0, 0 } });
        s.Add(new EmbeddingRecord { Id = "bad", Text = "b", Vector = new float[] { 1, 0 } }); // dim 2 vs query 3
        var hits = s.Search(new float[] { 1, 0, 0 }, topK: 5);
        Assert.Single(hits);
        Assert.Equal("good", hits[0].Id);
    }

    [Fact]
    public void Search_MinScore_Filters()
    {
        var s = new VectorStore();
        s.Add(new EmbeddingRecord { Id = "positive", Text = "p", Vector = new float[] { 1, 0 } });
        s.Add(new EmbeddingRecord { Id = "negative", Text = "n", Vector = new float[] { -1, 0 } });
        var hits = s.Search(new float[] { 1, 0 }, topK: 10, minScore: 0.5);
        Assert.Single(hits);
        Assert.Equal("positive", hits[0].Id);
    }
}
