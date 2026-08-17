using System.Net.Http;
using AeroCode.AI.Configuration;
using AeroCode.AI.Resilience;
using Microsoft.Extensions.Logging;

namespace AeroCode.AI.Providers;

/// <summary>Moonshot Kimi K3。OpenAI 兼容。</summary>
public sealed class KimiProvider : OpenAICompatibleProvider
{
    public KimiProvider(HttpClient http, ProviderConfig config, ILogger<KimiProvider> logger, AiResiliencePipeline? resilience = null) : base(http, config, logger, resilience) { }
}
