using System.Net.Http;
using AeroCode.AI.Configuration;
using AeroCode.AI.Resilience;
using Microsoft.Extensions.Logging;

namespace AeroCode.AI.Providers;

/// <summary>OpenRouter — 统一 40+ provider 网关, OpenAI 兼容。</summary>
public sealed class OpenRouterProvider : OpenAICompatibleProvider
{
    public OpenRouterProvider(HttpClient http, ProviderConfig config, ILogger<OpenRouterProvider> logger, AiResiliencePipeline? resilience = null) : base(http, config, logger, resilience) { }
}
