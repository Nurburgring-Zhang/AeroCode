using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using AeroCode.AI.Configuration;
using AeroCode.AI.Resilience;
using Microsoft.Extensions.Logging;

namespace AeroCode.AI.Providers;

/// <summary>
/// Provider 工厂。按 ProviderConfig 创建对应 IAiProvider。
/// 不在代码里硬编码任何 API key / endpoint,只根据 config 装配。
/// </summary>
public sealed class ProviderFactory
{
    private readonly AIOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IHttpClientFactory? _httpFactory;
    private readonly ResilienceOptions _resilienceOptions;
    private readonly Dictionary<string, IAiProvider> _cache = new();
    private readonly Dictionary<string, AiResiliencePipeline> _pipelines = new();

    public ProviderFactory(AIOptions options, ILoggerFactory loggerFactory, IHttpClientFactory? httpFactory = null, ResilienceOptions? resilienceOptions = null)
    {
        _options = options;
        _loggerFactory = loggerFactory;
        _httpFactory = httpFactory;
        _resilienceOptions = resilienceOptions ?? new ResilienceOptions();
    }

    public IAiProvider GetDefault() => Get(_options.DefaultProviderId);

    public IAiProvider Get(string providerId)
    {
        if (_cache.TryGetValue(providerId, out var cached)) return cached;
        var config = _options.Providers.FirstOrDefault(p => p.Id == providerId)
            ?? throw new InvalidOperationException($"Provider '{providerId}' not configured");
        var pipeline = GetOrCreatePipeline(providerId);
        var provider = Create(config, pipeline);
        _cache[providerId] = provider;
        return provider;
    }

    public IEnumerable<IAiProvider> GetAll()
    {
        foreach (var c in _options.Providers) yield return Get(c.Id);
    }

    public IEnumerable<string> ListConfiguredIds() => _options.Providers.Select(p => p.Id);

    private AiResiliencePipeline GetOrCreatePipeline(string providerId)
    {
        if (_pipelines.TryGetValue(providerId, out var p)) return p;
        p = new AiResiliencePipeline(_resilienceOptions);
        _pipelines[providerId] = p;
        return p;
    }

    private IAiProvider Create(ProviderConfig config, AiResiliencePipeline pipeline)
    {
        var http = _httpFactory?.CreateClient($"ai-{config.Id}") ?? new HttpClient();
        var logger = _loggerFactory.CreateLogger(config.Id);
        return config.Kind switch
        {
            "OpenAICompatible" => CreateOpenAICompatible(config, http, logger, pipeline),
            "AnthropicMessages" => new ClaudeProvider(http, config, CastLogger<ClaudeProvider>(logger), pipeline),
            "Custom" => new CustomProvider(http, config, CastLogger<CustomProvider>(logger), pipeline),
            _ => throw new NotSupportedException($"Unknown provider kind: {config.Kind}")
        };
    }

    private IAiProvider CreateOpenAICompatible(ProviderConfig config, HttpClient http, ILogger logger, AiResiliencePipeline pipeline)
    {
        // 依据 id 路由到具体子类, 让每个 provider 可独立扩展
        return config.Id.ToLowerInvariant() switch
        {
            "deepseek" => new DeepSeekProvider(http, config, CastLogger<DeepSeekProvider>(logger), pipeline),
            "qwen" or "dashscope" or "aliyun" => new QwenProvider(http, config, CastLogger<QwenProvider>(logger), pipeline),
            "kimi" or "moonshot" => new KimiProvider(http, config, CastLogger<KimiProvider>(logger), pipeline),
            "glm" or "zhipu" or "bigmodel" => new GlmProvider(http, config, CastLogger<GlmProvider>(logger), pipeline),
            "openai" or "gpt" => new OpenAIProvider(http, config, CastLogger<OpenAIProvider>(logger), pipeline),
            "openrouter" => new OpenRouterProvider(http, config, CastLogger<OpenRouterProvider>(logger), pipeline),
            "ollama" => new OllamaProvider(http, config, CastLogger<OllamaProvider>(logger), pipeline),
            "lmstudio" or "lm-studio" => new LmStudioProvider(http, config, CastLogger<LmStudioProvider>(logger), pipeline),
            "minimax" or "MiniMax" => new MiniMaxProvider(http, config, CastLogger<MiniMaxProvider>(logger), pipeline),
            _ => new CustomProvider(http, config, CastLogger<CustomProvider>(logger), pipeline)
        };
    }

    private static ILogger<T> CastLogger<T>(ILogger logger) where T : class
        => new CategoryLogger<T>(logger);

    private sealed class CategoryLogger<T> : ILogger<T> where T : class
    {
        private readonly ILogger _inner;
        public CategoryLogger(ILogger inner) { _inner = inner; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => _inner.BeginScope(state);
        public bool IsEnabled(LogLevel logLevel) => _inner.IsEnabled(logLevel);
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => _inner.Log(logLevel, eventId, state, exception, formatter);
    }
}
