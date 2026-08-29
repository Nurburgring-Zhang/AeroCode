// Copyright (c) AeroCode V3.0
// SSE 流空闲看门狗与 x-api-key 每请求解析测试（ClaudeProvider / OpenAICompatibleProvider）。
// 背景：StreamChatAsync 重写为逐行 ReadLineAsync(linkedCts.Token) + 可重置空闲超时，
// 空闲到期抛 AiProviderException(504, "stream idle timeout: no data for Ns")，
// 调用方取消保持 OperationCanceledException 语义；EOF（line==null）正常结束。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AeroCode.AI.Configuration;
using AeroCode.AI.Models;
using AeroCode.AI.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AeroCode.Tests.Ai;

/// <summary>
/// Provider 流式空闲看门狗行为测试：
/// 1) Claude SSE（Anthropic 格式 event:/data: 行）逐块解析且拼接正确；
/// 2) 服务器中途 EOF（无 [DONE]）时流正常结束不抛异常；
/// 3) 服务器停止发数据也不关闭连接（真实挂起流）时，空闲超时抛 AiProviderException(504)；
/// 4) 调用方取消与空闲超时严格区分：前者保持 OperationCanceledException；
/// 5) OpenAICompatible 侧同款空闲看门狗；
/// 6) Claude x-api-key 每请求解析（构造后设置环境变量仍生效，证明非构造期缓存）。
/// 多个用例读写进程环境变量，归入 EnvMutators 集合串行执行。
/// </summary>
[Collection("EnvMutators")]
public class ProviderStreamWatchdogTests
{
    /// <summary>把给定 SSE 文本作为 text/event-stream 一次性返回（可逐行解析）。</summary>
    private sealed class SseHandler : HttpMessageHandler
    {
        private readonly string _sse;
        public SseHandler(string sse) { _sse = sse; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_sse, Encoding.UTF8, "text/event-stream")
            });
    }

    /// <summary>返回一个永不写数据也不结束的流（模拟服务器挂死的 SSE 长连接）。</summary>
    private sealed class HangingStreamHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new HangingStream())
            });
    }

    /// <summary>
    /// 永不返回数据的流：ReadAsync 一直挂起，仅当取消令牌触发时以取消结束。
    /// 取消必须真实传入底层 ReadAsync——否则 StreamReader.ReadLineAsync(token) 无法被看门狗打断。
    /// </summary>
    private sealed class HangingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(new Memory<byte>(buffer, offset, count), cancellationToken).AsTask();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
            return new ValueTask<int>(tcs.Task);
        }
    }

    /// <summary>非流式响应并捕获请求头（验证 x-api-key 是否随请求发出）。</summary>
    private sealed class HeaderCapturingHandler : HttpMessageHandler
    {
        public Dictionary<string, string> LastHeaders { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in req.Headers) headers[h.Key] = string.Join(",", h.Value);
            LastHeaders = headers;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"id\":\"msg_1\",\"model\":\"claude-5\",\"content\":[{\"type\":\"text\",\"text\":\"Hello!\"}],\"stop_reason\":\"end_turn\",\"usage\":{\"input_tokens\":5,\"output_tokens\":3}}",
                    Encoding.UTF8, "application/json")
            });
        }
    }

    private static ClaudeProvider MakeClaude(HttpMessageHandler handler, int timeoutSeconds)
    {
        var cfg = new ProviderConfig
        {
            Id = "claude-watchdog",
            DisplayName = "Claude Watchdog",
            Kind = "AnthropicMessages",
            BaseUrl = "https://api.anthropic.com",
            DefaultModel = "claude-5-sonnet",
            ApiKeyEnvVar = "AERO_WATCHDOG_UNUSED_KEY", // 这些用例不关心鉴权头，避免触碰共享变量
            RequiresApiKey = true,
            TimeoutSeconds = timeoutSeconds // >0 → StreamIdleTimeout 取该秒数
        };
        return new ClaudeProvider(new HttpClient(handler), cfg, NullLogger<ClaudeProvider>.Instance);
    }

    /// <summary>OpenAICompatible 是抽象基类，用最小具体子类验证基类看门狗逻辑。</summary>
    private sealed class TestOpenAIProvider : OpenAICompatibleProvider
    {
        public TestOpenAIProvider(HttpClient http, ProviderConfig config)
            : base(http, config, NullLogger<TestOpenAIProvider>.Instance)
        {
        }
    }

    /// <summary>
    /// Claude SSE 正常解析：message_start / content_block_delta / ping / message_delta / [DONE]
    /// 混合事件中只有 content_block_delta.text 产出 DeltaContent，逐块顺序与拼接正确。
    /// </summary>
    [Fact]
    public async Task Claude_StreamChatAsync_ParsesAnthropicSseChunks()
    {
        var sse =
            "event: message_start\n" +
            "data: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_1\",\"model\":\"claude-5-sonnet\"}}\n\n" +
            "event: content_block_delta\n" +
            "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"你好，\"}}\n\n" +
            "event: ping\n" +
            "data: {\"type\":\"ping\"}\n\n" +
            "event: content_block_delta\n" +
            "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"AeroCode\"}}\n\n" +
            "event: message_delta\n" +
            "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":7}}\n\n" +
            "data: [DONE]\n\n";
        var provider = MakeClaude(new SseHandler(sse), timeoutSeconds: 30);

        var deltas = new List<string>();
        await foreach (var chunk in provider.StreamChatAsync(new ChatRequest
        {
            Stream = true,
            Messages = new[] { new ChatMessage { Role = "user", Content = "hi" } },
            EnableThinking = false
        }))
        {
            Assert.NotNull(chunk.DeltaContent);
            deltas.Add(chunk.DeltaContent!);
        }

        // 恰好两个 content_block_delta 产出，顺序与内容逐块正确，拼接为完整文本
        Assert.Equal(new[] { "你好，", "AeroCode" }, deltas);
        Assert.Equal("你好，AeroCode", string.Concat(deltas));
    }

    /// <summary>
    /// 服务器中途 EOF：只发了两个增量、没有 [DONE] 就结束。
    /// ReadLineAsync 返回 null → yield break，枚举正常完成不抛异常。
    /// </summary>
    [Fact]
    public async Task Claude_StreamChatAsync_ServerEofEndsCleanly()
    {
        var sse =
            "event: content_block_delta\n" +
            "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"abc\"}}\n\n" +
            "event: content_block_delta\n" +
            "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"def\"}}\n";
        var provider = MakeClaude(new SseHandler(sse), timeoutSeconds: 30);

        var sb = new StringBuilder();
        await foreach (var chunk in provider.StreamChatAsync(new ChatRequest
        {
            Stream = true,
            Messages = new[] { new ChatMessage { Role = "user", Content = "hi" } },
            EnableThinking = false
        }))
        {
            if (chunk.DeltaContent is not null) sb.Append(chunk.DeltaContent);
        }

        Assert.Equal("abcdef", sb.ToString());
    }

    /// <summary>
    /// Claude 空闲超时：流永不写数据也不结束，TimeoutSeconds=1 → StreamIdleTimeout=1s。
    /// 必须抛 AiProviderException：StatusCode=504、消息体为 "stream idle timeout: no data for 1s"；
    /// 且真实等满空闲窗口（不早退）又远小于挂死（CI 上限 10s）。
    /// </summary>
    [Fact]
    public async Task Claude_StreamChatAsync_IdleStream_Throws504Watchdog()
    {
        var provider = MakeClaude(new HangingStreamHandler(), timeoutSeconds: 1);

        var sw = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<AiProviderException>(async () =>
        {
            await foreach (var _ in provider.StreamChatAsync(new ChatRequest
            {
                Stream = true,
                Messages = new[] { new ChatMessage { Role = "user", Content = "hi" } },
                EnableThinking = false
            }))
            {
            }
        });
        sw.Stop();

        Assert.Equal(504, ex.StatusCode);
        Assert.Contains("stream idle timeout", ex.Message);
        Assert.Equal("stream idle timeout: no data for 1s", ex.ResponseBody);
        Assert.True(sw.Elapsed >= TimeSpan.FromSeconds(0.8),
            $"空闲看门狗应等待约 1s 空闲窗口，实际 {sw.Elapsed} 就失败，疑似未走超时路径");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10),
            $"空闲看门狗应在数秒内触发，实际 {sw.Elapsed}，疑似看门狗失效导致挂起");
    }

    /// <summary>
    /// 调用方取消与空闲超时的区分：空闲窗口放宽到 30s，300ms 后由调用方取消。
    /// 必须抛 OperationCanceledException（含其子类 TaskCanceledException），
    /// 而不是被看门狗误转为 AiProviderException——后者仅在调用方未取消时才允许。
    /// </summary>
    [Fact]
    public async Task Claude_StreamChatAsync_CallerCancellation_KeepsOperationCanceledSemantics()
    {
        var provider = MakeClaude(new HangingStreamHandler(), timeoutSeconds: 30);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in provider.StreamChatAsync(new ChatRequest
            {
                Stream = true,
                Messages = new[] { new ChatMessage { Role = "user", Content = "hi" } },
                EnableThinking = false
            }, cts.Token))
            {
            }
        });
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10),
            $"调用方取消应立刻生效，实际 {sw.Elapsed}，疑似取消令牌未被观察");
    }

    /// <summary>
    /// OpenAICompatible 侧同款空闲看门狗：TimeoutSeconds=1 → StreamIdleTimeout=1s，
    /// 挂起流在空闲窗口后抛 AiProviderException(504, "stream idle timeout: no data for 1s")。
    /// 注意：TimeoutSeconds>0 时基类构造器还会把 HttpClient.Timeout 设为 1s，
    /// 但那只能管到 SendAsync（响应头）阶段，不影响内容流读取——本用例正是验证内容流这段。
    /// </summary>
    [Fact]
    public async Task OpenAICompatible_StreamChatAsync_IdleStream_Throws504Watchdog()
    {
        var cfg = new ProviderConfig
        {
            Id = "openai-watchdog",
            DisplayName = "Watchdog",
            Kind = "OpenAICompatible",
            BaseUrl = "https://example.com/v1",
            DefaultModel = "test-model",
            ApiKeyEnvVar = null,
            RequiresApiKey = false,
            TimeoutSeconds = 1
        };
        var provider = new TestOpenAIProvider(new HttpClient(new HangingStreamHandler()), cfg);

        var sw = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<AiProviderException>(async () =>
        {
            await foreach (var _ in provider.StreamChatAsync(new ChatRequest
            {
                Stream = true,
                Messages = new[] { new ChatMessage { Role = "user", Content = "hi" } },
                EnableThinking = false
            }))
            {
            }
        });
        sw.Stop();

        Assert.Equal(504, ex.StatusCode);
        Assert.Contains("stream idle timeout", ex.Message);
        Assert.Equal("stream idle timeout: no data for 1s", ex.ResponseBody);
        Assert.True(sw.Elapsed >= TimeSpan.FromSeconds(0.8),
            $"空闲看门狗应等待约 1s 空闲窗口，实际 {sw.Elapsed} 就失败，疑似未走超时路径");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10),
            $"空闲看门狗应在数秒内触发，实际 {sw.Elapsed}，疑似看门狗失效导致挂起");
    }

    /// <summary>
    /// Claude x-api-key 每请求解析（非构造期缓存）：
    /// 构造时环境变量未设置 → 第一次请求不带 x-api-key；
    /// 构造后才设置环境变量 → 第二次请求立刻带上该 key，无需重建 provider。
    /// </summary>
    [Fact]
    public async Task Claude_ApiKeyResolvedPerRequest_NotCachedAtConstruction()
    {
        const string envVar = "AERO_CLAUDE_TEST_KEY";
        const string key = "sk-ant-live-rotate-me";
        Environment.SetEnvironmentVariable(envVar, null); // 确保构造时未设置
        try
        {
            var handler = new HeaderCapturingHandler();
            var cfg = new ProviderConfig
            {
                Id = "claude",
                DisplayName = "Claude",
                Kind = "AnthropicMessages",
                BaseUrl = "https://api.anthropic.com",
                DefaultModel = "claude-5-sonnet",
                ApiKeyEnvVar = envVar,
                RequiresApiKey = true
            };
            var provider = new ClaudeProvider(new HttpClient(handler), cfg, NullLogger<ClaudeProvider>.Instance);

            // 构造时 key 不存在：第一次请求如实不带头（也没有假 key）
            var resp1 = await provider.ChatAsync(new ChatRequest
            {
                Messages = new[] { new ChatMessage { Role = "user", Content = "hi" } }
            });
            Assert.Equal("Hello!", resp1.Content);
            Assert.False(handler.LastHeaders.ContainsKey("x-api-key"),
                "环境变量未设置时请求不应携带 x-api-key 头");

            // 构造之后才设置环境变量：若 key 是构造期缓存，此头将永远缺失
            Environment.SetEnvironmentVariable(envVar, key);
            var resp2 = await provider.ChatAsync(new ChatRequest
            {
                Messages = new[] { new ChatMessage { Role = "user", Content = "hi" } }
            });
            Assert.Equal("Hello!", resp2.Content);
            Assert.True(handler.LastHeaders.TryGetValue("x-api-key", out var sent),
                "构造后设置的环境变量未被每请求解析读取，请求缺失 x-api-key 头");
            Assert.Equal(key, sent);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, null);
        }
    }
}
