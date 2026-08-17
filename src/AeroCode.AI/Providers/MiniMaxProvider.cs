// Copyright (c) AeroCode V3.0
// MiniMaxProvider — MiniMax M2 / M2.5 / M2.7 / M3 (https://api.minimaxi.com/v1, OpenAI-compatible).
// Key loaded from MINIMAX_API_KEY env var. Configure at:
//   settings.json: { "ai": { "providers": [{ "id": "minimax", "baseUrl": "https://api.minimaxi.com/v1", "apiKeyEnvVar": "MINIMAX_API_KEY", "defaultModel": "MiniMax-M2" } ] } }
using System.Net.Http;
using AeroCode.AI.Configuration;
using AeroCode.AI.Resilience;
using Microsoft.Extensions.Logging;

namespace AeroCode.AI.Providers;

/// <summary>
/// MiniMax M-series provider. Uses the OpenAI-compatible endpoint at api.minimaxi.com/v1.
/// Model names: MiniMax-M2 / MiniMax-M2.5 / MiniMax-M2.7 / MiniMax-M3 etc.
/// </summary>
public sealed class MiniMaxProvider : OpenAICompatibleProvider
{
    public MiniMaxProvider(HttpClient http, ProviderConfig config, ILogger<MiniMaxProvider> logger, AiResiliencePipeline? resilience = null)
        : base(http, config, logger, resilience) { }
}
