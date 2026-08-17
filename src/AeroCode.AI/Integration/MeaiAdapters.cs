// Copyright (c) AeroCode V3.2
// MeaiAdapters — 真 Microsoft.Extensions.AI (MEAI) 集成。
// 包装 AeroCode 的 IAiProvider 暴露为 IChatClient（MEAI 抽象），
// 让任何 MEAI-aware 工具（Semantic Kernel、Microsoft Agent Framework、AG-UI）能直接用 AeroCode 的 LLM 路由。
// 零假装：每次 GetResponseAsync 真的走 IAiProvider.ChatAsync，没有任何 mock。
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using AeroCode.AI.Providers;
using Microsoft.Extensions.AI;
using MeaiMsg = Microsoft.Extensions.AI.ChatMessage;
using AeroMsg = AeroCode.AI.Models.ChatMessage;
using MeaiResp = Microsoft.Extensions.AI.ChatResponse;
using MeaiUpdate = Microsoft.Extensions.AI.ChatResponseUpdate;

namespace AeroCode.AI.Integration;

/// <summary>
/// Adapter: AeroCode IAiProvider → MEAI IChatClient. Real calls, no mocks.
/// </summary>
public sealed class MeaiChatClient : IChatClient
{
    private readonly IAiProvider _provider;
    public MeaiChatClient(IAiProvider provider) { _provider = provider; }

    public ChatClientMetadata Metadata { get; } = new("AeroCode");

    public async Task<MeaiResp> GetResponseAsync(
        IEnumerable<MeaiMsg> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var req = new AeroCode.AI.Models.ChatRequest
        {
            Model = options?.ModelId ?? string.Empty,
            Messages = messages.Select(MapToAeroCode).ToArray(),
            Stream = false,
            Temperature = (double)(options?.Temperature ?? 0.7f),
            MaxTokens = options?.MaxOutputTokens ?? 4096,
            EnableThinking = false,
        };
        var resp = await _provider.ChatAsync(req, cancellationToken);
        return new MeaiResp(new MeaiMsg(ChatRole.Assistant, resp.Content ?? ""));
    }

    public async IAsyncEnumerable<MeaiUpdate> GetStreamingResponseAsync(
        IEnumerable<MeaiMsg> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var req = new AeroCode.AI.Models.ChatRequest
        {
            Model = options?.ModelId ?? string.Empty,
            Messages = messages.Select(MapToAeroCode).ToArray(),
            Stream = true,
            Temperature = (double)(options?.Temperature ?? 0.7f),
            MaxTokens = options?.MaxOutputTokens ?? 4096,
            EnableThinking = false,
        };
        await foreach (var chunk in _provider.StreamChatAsync(req, cancellationToken))
        {
            var text = chunk.DeltaContent ?? chunk.DeltaReasoning ?? "";
            if (text.Length > 0)
                yield return new MeaiUpdate(ChatRole.Assistant, text);
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType == typeof(IAiProvider) ? _provider : null;

    public void Dispose() { }

    private static AeroMsg MapToAeroCode(MeaiMsg m)
    {
        var text = m.Contents.OfType<TextContent>().FirstOrDefault()?.Text ?? "";
        return new AeroMsg { Role = m.Role.Value, Content = text };
    }
}

/// <summary>Convenience: build a MeaiChatClient from a provider id via the factory.</summary>
public static class MeaiChatClientExtensions
{
    public static IChatClient AsMeaiChatClient(this IAiProvider provider) => new MeaiChatClient(provider);
    public static IChatClient AsMeaiChatClient(this ProviderFactory factory, string providerId) =>
        new MeaiChatClient(factory.Get(providerId));
}
