// Copyright (c) AeroCode V3.0
// MiniMaxProvider — MiniMax M2 / M2.5 / M2.7 / M3 (https://api.minimaxi.com/v1, OpenAI-compatible).
// Key loaded from MINIMAX_API_KEY env var. Configure at:
//   settings.json: { "ai": { "providers": [{ "id": "minimax", "baseUrl": "https://api.minimaxi.com/v1", "apiKeyEnvVar": "MINIMAX_API_KEY", "defaultModel": "MiniMax-M2" } ] } }
using System.Collections.Generic;
using System.Net.Http;
using AeroCode.AI.Configuration;
using AeroCode.AI.Models;
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

    /// <summary>
    /// MiniMax thinking 协议与 DeepSeek 不同：base 发 <c>thinking:{type:"enabled"}</c>，
    /// MiniMax 只接受 <c>type:"adaptive"|"disabled"</c>（实测 HTTP 400：
    /// "invalid thinking.type: \"enabled\" (allowed: adaptive, disabled)"），且不识别 reasoning_effort。
    /// 这里把 thinking 改写为 adaptive 并移除 reasoning_effort。
    /// </summary>
    protected override object BuildRequestBody(ChatRequest request)
    {
        var body = base.BuildRequestBody(request) as Dictionary<string, object?>;
        if (body is null)
        {
            return base.BuildRequestBody(request);
        }

        if (body.ContainsKey("thinking"))
        {
            body["thinking"] = new { type = "adaptive" };
            body.Remove("reasoning_effort"); // MiniMax 不识别该参数
        }

        return body;
    }
}
