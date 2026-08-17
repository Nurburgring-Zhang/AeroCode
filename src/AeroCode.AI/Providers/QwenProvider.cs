using System.Net.Http;
using AeroCode.AI.Configuration;
using AeroCode.AI.Resilience;
using Microsoft.Extensions.Logging;

namespace AeroCode.AI.Providers;

/// <summary>
/// 阿里 Qwen3-Max / Qwen3-Coder 等。DashScope 提供 OpenAI 兼容端点。
/// </summary>
public sealed class QwenProvider : OpenAICompatibleProvider
{
    public QwenProvider(HttpClient http, ProviderConfig config, ILogger<QwenProvider> logger, AiResiliencePipeline? resilience = null) : base(http, config, logger, resilience) { }
}
