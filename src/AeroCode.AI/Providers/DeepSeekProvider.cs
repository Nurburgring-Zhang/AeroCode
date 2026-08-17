using AeroCode.AI.Configuration;
using AeroCode.AI.Resilience;
using Microsoft.Extensions.Logging;

namespace AeroCode.AI.Providers;

/// <summary>
/// DeepSeek V4 系列 provider。基于 OpenAI 兼容协议,
/// 必须启用 thinking + reasoning_effort 才能避免 thinking 模式被关闭导致的 400 错误。
/// </summary>
public sealed class DeepSeekProvider : OpenAICompatibleProvider
{
    public DeepSeekProvider(System.Net.Http.HttpClient http, ProviderConfig config, ILogger<DeepSeekProvider> logger, AiResiliencePipeline? resilience = null) : base(http, config, logger, resilience)
    {
    }
}
