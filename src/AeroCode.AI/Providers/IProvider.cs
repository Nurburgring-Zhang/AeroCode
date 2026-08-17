using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AeroCode.AI.Models;

namespace AeroCode.AI.Providers;

/// <summary>
/// Provider 类型。所有内置/自定义 Provider 必须实现此接口。
/// </summary>
public enum ProviderKind
{
    OpenAICompatible, // DeepSeek / Qwen / Kimi / GLM / OpenAI / OpenRouter / Ollama / LMStudio / RunningHub
    AnthropicMessages, // Claude (Messages API, 独立协议)
    Custom            // 用户自定义 endpoint
}

/// <summary>
/// 统一 AI Provider 抽象。所有 10+ 内置 Provider 实现此接口,提供:
/// 1. 同步 Chat
/// 2. 流式 Chat
/// 3. 健康检查
/// 4. 列出模型 (可选)
/// </summary>
public interface IAiProvider
{
    string ProviderId { get; }
    string DisplayName { get; }
    ProviderKind Kind { get; }
    bool SupportsStreaming { get; }
    bool SupportsToolCalling { get; }
    bool SupportsThinking { get; }

    Task<ChatResponse> ChatAsync(ChatRequest request, CancellationToken ct = default);

    IAsyncEnumerable<ChatChunk> StreamChatAsync(ChatRequest request, CancellationToken ct = default);

    Task<bool> HealthCheckAsync(CancellationToken ct = default);
}
