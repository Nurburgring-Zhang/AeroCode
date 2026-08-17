using System.Net.Http;
using AeroCode.AI.Configuration;
using AeroCode.AI.Resilience;
using Microsoft.Extensions.Logging;

namespace AeroCode.AI.Providers;

/// <summary>
/// Ollama 本地服务 (默认 http://localhost:11434/v1)。不需要 API key。
/// </summary>
public sealed class OllamaProvider : OpenAICompatibleProvider
{
    public OllamaProvider(HttpClient http, ProviderConfig config, ILogger<OllamaProvider> logger, AiResiliencePipeline? resilience = null) : base(http, config, logger, resilience) { }

    public override bool SupportsThinking => false; // Ollama 模型不通过 thinking 字段开启推理
}
