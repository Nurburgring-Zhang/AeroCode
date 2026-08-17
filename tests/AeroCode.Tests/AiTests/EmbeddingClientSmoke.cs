// Copyright (c) AeroCode V3.2
// EmbeddingClient + SemanticSearcher real-Ollama smoke test. Network-gated.
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AeroCode.AI.Capabilities;
using AeroCode.AI.Embedding;
using AeroCode.AI.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AeroCode.Tests.AiTests;

public class EmbeddingClientSmoke
{
    private static bool OllamaReachable() => Environment.GetEnvironmentVariable("AEROCODE_RUN_OLLAMA_TESTS") == "1";

    [Fact(Skip = "Requires local Ollama — set AEROCODE_RUN_OLLAMA_TESTS=1 and have ollama serve running on :11434 with all-minilm-l6-v2 model")]
    public async Task EmbeddingClient_Real_Ollama_Returns384DimVector()
    {
        if (!OllamaReachable()) return;
        await using var client = new EmbeddingClient(new EmbeddingClientOptions
        {
            BaseUrl = "http://localhost:11434",
            Model = "all-minlm-l6-v2",
            Backend = EmbeddingBackend.Ollama
        });
        var vec = await client.EmbedAsync("hello world");
        Assert.NotEmpty(vec);
        Assert.Equal(384, vec.Length);
    }

    [Fact(Skip = "Requires local Ollama — see above")]
    public async Task VectorStore_Real_CosineFindsRelevant()
    {
        if (!OllamaReachable()) return;
        await using var client = new EmbeddingClient(new EmbeddingClientOptions
        {
            BaseUrl = "http://localhost:11434",
            Model = "all-minlm-l6-v2",
            Backend = EmbeddingBackend.Ollama
        });
        var store = new VectorStore();
        var docs = new[] {
            "深度学习是机器学习的一个子集,使用神经网络。",
            "苹果是水果,富含维生素 C。",
            "北京是中国的首都,有故宫、长城。",
        };
        var vecs = await client.EmbedBatchAsync(docs);
        for (var i = 0; i < docs.Length; i++)
            store.Add(new EmbeddingRecord { Id = i.ToString(), Text = docs[i], Vector = vecs.vectors[i] });
        var q = await client.EmbedAsync("神经网络");
        var hits = store.Search(q, topK: 2);
        Assert.Equal(2, hits.Count);
        Assert.Equal("0", hits[0].Id); // "深度学习...神经网络" should rank first
    }
}
