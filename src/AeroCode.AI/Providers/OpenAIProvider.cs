using System.Net.Http;
using AeroCode.AI.Configuration;
using AeroCode.AI.Resilience;
using Microsoft.Extensions.Logging;

namespace AeroCode.AI.Providers;

/// <summary>OpenAI 官方 (GPT-5.6 等)。</summary>
public sealed class OpenAIProvider : OpenAICompatibleProvider
{
    public OpenAIProvider(HttpClient http, ProviderConfig config, ILogger<OpenAIProvider> logger, AiResiliencePipeline? resilience = null) : base(http, config, logger, resilience) { }
}
