using System.Net.Http;
using AeroCode.AI.Configuration;
using AeroCode.AI.Resilience;
using Microsoft.Extensions.Logging;

namespace AeroCode.AI.Providers;

/// <summary>
/// LM Studio 本地服务 (默认 http://localhost:1234/v1)。OpenAI 兼容, 不需要 API key。
/// </summary>
public sealed class LmStudioProvider : OpenAICompatibleProvider
{
    public LmStudioProvider(HttpClient http, ProviderConfig config, ILogger<LmStudioProvider> logger, AiResiliencePipeline? resilience = null) : base(http, config, logger, resilience) { }

    public override bool SupportsThinking => false;
}
