// Copyright (c) AeroCode
// OllamaClient 单元测试（fake HttpMessageHandler）+ 真实 Ollama E2E（LOCAL_LLM_SPEC P1/P2 验证）。
// 真实 E2E 仅当本机 Ollama 服务可达时运行（否则跳过，不拖累常规套件）——拒绝伪造通过。
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AeroCode.AI.Configuration;
using AeroCode.AI.LocalModels;
using AeroCode.AI.Models;
using AeroCode.AI.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AeroCode.Tests.AiTests;

public sealed class OllamaClientTests
{
    /// <summary>脚本化 handler：按路径返回预设响应，支持 NDJSON 流。</summary>
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public List<(HttpMethod Method, Uri Uri, string? Body)> Requests { get; } = new();

        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request.Method, request.RequestUri!, body));
            return _responder(request);
        }
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private const string TagsJson = """
        {"models":[{"name":"qwen2.5:1.5b","model":"qwen2.5:1.5b","size":986061892,
          "digest":"abc","modified_at":"2026-09-13T00:00:00Z",
          "details":{"format":"gguf","family":"qwen2","parameter_size":"1.5B","quantization_level":"Q4_K_M"}}]}
        """;

    [Fact]
    public async Task GetVersion_ParsesVersion()
    {
        using var handler = new FakeHandler(_ => Json("""{"version":"0.34.0"}"""));
        using var client = new OllamaClient(handler: handler);

        var result = await client.GetVersionAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal("0.34.0", result.Value!.Version);
    }

    [Fact]
    public async Task ListModels_ParsesModels()
    {
        using var handler = new FakeHandler(_ => Json(TagsJson));
        using var client = new OllamaClient(handler: handler);

        var result = await client.ListModelsAsync();

        Assert.True(result.IsSuccess);
        var model = Assert.Single(result.Value!);
        Assert.Equal("qwen2.5:1.5b", model.Name);
        Assert.Equal("gguf", model.Details!.Format);
        Assert.Equal("Q4_K_M", model.Details.QuantizationLevel);
        Assert.True(model.Size > 0);
    }

    [Fact]
    public async Task Unreachable_ReturnsFailure_NotThrow()
    {
        using var handler = new FakeHandler(_ => throw new HttpRequestException("connection refused"));
        using var client = new OllamaClient(handler: handler);

        var result = await client.GetVersionAsync();

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Error);
        Assert.Contains("unreachable", result.Error);
    }

    [Fact]
    public async Task HttpError_ReturnsFailureWithStatus()
    {
        using var handler = new FakeHandler(_ => Json("""{"error":"model not found"}""", HttpStatusCode.NotFound));
        using var client = new OllamaClient(handler: handler);

        var result = await client.ShowModelAsync("missing:latest");

        Assert.False(result.IsSuccess);
        Assert.Equal(404, result.StatusCode);
        Assert.Contains("model not found", result.Error);
    }

    [Fact]
    public async Task DeleteModel_Success_ReturnsOk()
    {
        using var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var client = new OllamaClient(handler: handler);

        var result = await client.DeleteModelAsync("some:model");

        Assert.True(result.IsSuccess);
        Assert.True(result.Value);
        // DELETE 方法与 body 名称如实上送。
        var (method, _, body) = handler.Requests.Single();
        Assert.Equal(HttpMethod.Delete, method);
        Assert.Contains("some:model", body);
    }

    [Fact]
    public async Task PullModel_StreamsProgress_AndReportsSuccess()
    {
        // NDJSON 流：manifest → 下载中（带 total/completed）→ success。
        var ndjson = string.Join("\n",
            """{"status":"pulling manifest"}""",
            """{"status":"downloading sha256:abc","digest":"sha256:abc","total":1000,"completed":500}""",
            """{"status":"success"}""");
        using var handler = new FakeHandler(_ => Json(ndjson));
        using var client = new OllamaClient(handler: handler);

        var progress = new List<OllamaPullProgress>();
        await foreach (var p in client.PullModelAsync("qwen2.5:1.5b"))
        {
            progress.Add(p);
        }

        Assert.Equal(3, progress.Count);
        Assert.Equal(0.5, progress[1].Fraction);
        Assert.True(progress[2].IsDone);
    }

    [Fact]
    public async Task PullModel_EmptyName_Throws()
    {
        using var handler = new FakeHandler(_ => Json("{}"));
        using var client = new OllamaClient(handler: handler);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await foreach (var _ in client.PullModelAsync(" ")) { }
        });
    }

    // ---------------- 真实 Ollama E2E（本机服务可达才跑，否则诚实跳过） ----------------

    private static async Task<bool> OllamaReachableAsync()
    {
        try
        {
            using var client = new OllamaClient();
            return await client.IsReachableAsync();
        }
        catch
        {
            return false;
        }
    }

    [SkippableFact]
    public async Task Live_ListModels_ReturnsInstalledModel()
    {
        Skip.IfNot(await OllamaReachableAsync(), "本机 Ollama 不可达——跳过真实 E2E（非失败）");

        using var client = new OllamaClient();
        var result = await client.ListModelsAsync();

        Assert.True(result.IsSuccess, $"真实 Ollama 列模型失败：{result.Error}");
        Assert.NotNull(result.Value);
        // 只要列出即证明连通；若已 pull qwen2.5 则应能见到。
    }

    [SkippableFact]
    public async Task Live_OllamaProvider_StreamsRealCompletion()
    {
        Skip.IfNot(await OllamaReachableAsync(), "本机 Ollama 不可达——跳过真实本地模型推理 E2E（非失败）");

        // 选一个本机已装的模型；优先 qwen2.5:1.5b，否则用第一个已装模型。
        using var listClient = new OllamaClient();
        var list = await listClient.ListModelsAsync();
        Skip.IfNot(list.IsSuccess && list.Value is { Count: > 0 }, "本机无已装模型——跳过推理 E2E（非失败）");
        var model = list.Value!.Select(m => m.Name)
            .FirstOrDefault(n => n.StartsWith("qwen2.5", StringComparison.OrdinalIgnoreCase))
            ?? list.Value![0].Name;

        var config = new ProviderConfig
        {
            Id = "ollama",
            DisplayName = "Ollama (local)",
            Kind = "OpenAICompatible",
            BaseUrl = "http://localhost:11434/v1",
            DefaultModel = model,
            RequiresApiKey = false,
            SupportsStreaming = true,
        };
        using var http = new HttpClient { BaseAddress = new Uri("http://localhost:11434/v1") };
        var provider = new OllamaProvider(http, config, NullLogger<OllamaProvider>.Instance);

        var request = new ChatRequest
        {
            Model = model,
            Stream = true,
            Messages = new List<ChatMessage>
            {
                new() { Role = "user", Content = "用一句简短的话介绍快速排序。" },
            },
        };

        var sb = new StringBuilder();
        await foreach (var chunk in provider.StreamChatAsync(request))
        {
            if (chunk.DeltaContent is { Length: > 0 } c)
            {
                sb.Append(c);
            }
        }

        // 真实本地模型产出非空输出即证明"加载本地大模型运行"端到端成立。
        Assert.True(sb.Length > 0, $"本地模型 {model} 未产出任何内容");
    }
}
