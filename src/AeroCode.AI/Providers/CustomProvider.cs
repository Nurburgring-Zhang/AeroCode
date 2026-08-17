using System.Net.Http;
using AeroCode.AI.Configuration;
using AeroCode.AI.Resilience;
using Microsoft.Extensions.Logging;

namespace AeroCode.AI.Providers;

/// <summary>
/// 用户自定义 endpoint 走 OpenAI 兼容协议 (大多数自部署 LLM 都兼容)。
/// 在 settings.json 里配 BaseUrl + Model 即可。
/// </summary>
public sealed class CustomProvider : OpenAICompatibleProvider
{
    public CustomProvider(HttpClient http, ProviderConfig config, ILogger<CustomProvider> logger, AiResiliencePipeline? resilience = null)
        : base(http, config, logger, resilience) { }
}
