using System.Net.Http;
using AeroCode.AI.Capabilities;
using AeroCode.AI.Configuration;
using AeroCode.AI.Resilience;
using Microsoft.Extensions.Logging;

namespace AeroCode.AI.Providers;

/// <summary>OpenAI 官方 (GPT-5.6 等)。</summary>
public sealed class OpenAIProvider : OpenAICompatibleProvider
{
    public OpenAIProvider(
        HttpClient http,
        ProviderConfig config,
        ILogger<OpenAIProvider> logger,
        AiResiliencePipeline? resilience = null,
        // R3-δ：可选能力探测注入（xhigh probe 门控；null = 现行为）。
        IVendorCapabilityProbe? capabilityProbe = null)
        : base(http, config, logger, resilience, capabilityProbe) { }
}
