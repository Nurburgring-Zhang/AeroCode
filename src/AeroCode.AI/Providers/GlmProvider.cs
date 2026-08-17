using System.Net.Http;
using AeroCode.AI.Configuration;
using AeroCode.AI.Resilience;
using Microsoft.Extensions.Logging;

namespace AeroCode.AI.Providers;

/// <summary>智谱 GLM 5.2。OpenAI 兼容。</summary>
public sealed class GlmProvider : OpenAICompatibleProvider
{
    public GlmProvider(HttpClient http, ProviderConfig config, ILogger<GlmProvider> logger, AiResiliencePipeline? resilience = null) : base(http, config, logger, resilience) { }
}
