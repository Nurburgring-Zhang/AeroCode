using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Net.Http;
using AeroCode.AI.Configuration;
using AeroCode.AI.Resilience;
using Microsoft.Extensions.Logging;

namespace AeroCode.AI.Providers;

/// <summary>
/// Provider 工厂。按 ProviderConfig 创建对应 IAiProvider。
/// 不在代码里硬编码任何 API key / endpoint,只根据 config 装配。
/// 支持 <see cref="Reload"/> 热重载：设置保存后无需重启即生效。
/// </summary>
public sealed class ProviderFactory : IProviderRegistry, IDisposable
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly IHttpClientFactory? _httpFactory;
    private readonly ResilienceOptions _resilienceOptions;
    private readonly object _sync = new();
    private readonly Dictionary<string, IAiProvider> _cache = new();
    private readonly Dictionary<string, AiResiliencePipeline> _pipelines = new();
    private readonly Dictionary<string, HttpClient> _ownedHttp = new();
    private HttpClient? _probeHttp;
    private AIOptions _options;

    /// <summary>配置热重载完成（provider 缓存已清空，UI 应刷新下拉列表等）。</summary>
    public event Action? ProvidersChanged;

    public ProviderFactory(AIOptions options, ILoggerFactory loggerFactory, IHttpClientFactory? httpFactory = null, ResilienceOptions? resilienceOptions = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _loggerFactory = loggerFactory;
        _httpFactory = httpFactory;
        _resilienceOptions = resilienceOptions ?? new ResilienceOptions();
    }

    /// <summary>
    /// 热重载配置：替换选项并清空 provider/弹性管线缓存（下次 Get 按新配置重建）。
    /// 熔断器状态随管线一并重置——配置变更（如换了 endpoint）后旧熔断统计不再有意义。
    /// 无 IHttpClientFactory 时自建的 HttpClient 一并释放，避免反复热重载泄漏套接字；
    /// 被替换 provider 上的在途请求会如实失败（热重载语义）。
    /// </summary>
    public void Reload(AIOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        List<HttpClient> toDispose;
        lock (_sync)
        {
            _options = options;
            _cache.Clear();
            _pipelines.Clear();
            toDispose = new List<HttpClient>(_ownedHttp.Values);
            _ownedHttp.Clear();
        }
        foreach (var client in toDispose) client.Dispose();

        ProvidersChanged?.Invoke();
    }

    /// <summary>释放自建 HttpClient（工厂提供的客户端由工厂管理，不在此释放）。</summary>
    public void Dispose()
    {
        lock (_sync)
        {
            foreach (var client in _ownedHttp.Values) client.Dispose();
            _ownedHttp.Clear();
            _probeHttp?.Dispose();
            _probeHttp = null;
            _cache.Clear();
            _pipelines.Clear();
        }
    }

    public IAiProvider GetDefault() => Get(DefaultProviderId);

    public IAiProvider Get(string providerId)
    {
        lock (_sync)
        {
            if (_cache.TryGetValue(providerId, out var cached)) return cached;
            var config = _options.Providers.FirstOrDefault(p => p.Id == providerId)
                ?? throw new InvalidOperationException($"Provider '{providerId}' not configured");
            var pipeline = GetOrCreatePipeline(providerId);
            var provider = Create(config, pipeline);
            _cache[providerId] = provider;
            return provider;
        }
    }

    public IEnumerable<IAiProvider> GetAll()
    {
        List<string> ids;
        lock (_sync)
        {
            ids = _options.Providers.Select(p => p.Id).ToList();
        }

        foreach (var id in ids) yield return Get(id);
    }

    public IEnumerable<string> ListConfiguredIds()
    {
        lock (_sync)
        {
            return _options.Providers.Select(p => p.Id).ToList();
        }
    }

    /// <summary>查询 provider 配置（编排层解析默认模型用）。未配置返回 false。</summary>
    public bool TryGetConfig(string providerId, [NotNullWhen(true)] out ProviderConfig? config)
    {
        lock (_sync)
        {
            var found = _options.Providers.FirstOrDefault(p => p.Id == providerId);
            config = found;
            return found is not null;
        }
    }

    /// <summary>
    /// 按给定配置（可以是未保存/编辑中的）构建一次性探针实例：
    /// 不进缓存、独立弹性管线——设置页单个 provider 连通性测试专用，
    /// 不干扰运行中编排使用的缓存实例，也不改变已加载配置。
    /// 探针共用一个懒加载 HttpClient，避免每次点击泄漏客户端
    /// （探针为一次性短请求；并发探测时 Timeout 以最后设置者为准）。
    /// </summary>
    public IAiProvider CreateProbe(ProviderConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        HttpClient probe;
        lock (_sync)
        {
            _probeHttp ??= new HttpClient();
            probe = _probeHttp;
        }
        return Create(config, new AiResiliencePipeline(_resilienceOptions), probe);
    }

    /// <summary>全局默认 provider 的 Id。</summary>
    public string DefaultProviderId
    {
        get { lock (_sync) return _options.DefaultProviderId; }
    }

    private AiResiliencePipeline GetOrCreatePipeline(string providerId)
    {
        if (_pipelines.TryGetValue(providerId, out var p)) return p;
        p = new AiResiliencePipeline(_resilienceOptions);
        _pipelines[providerId] = p;
        return p;
    }

    private IAiProvider Create(ProviderConfig config, AiResiliencePipeline pipeline, HttpClient? httpOverride = null)
    {
        // 有 IHttpClientFactory 时客户端归工厂管理（不可自行释放）；
        // 否则自建并登记，Reload/Dispose 时统一释放防套接字泄漏。
        var http = httpOverride
            ?? _httpFactory?.CreateClient($"ai-{config.Id}")
            ?? NewOwnedHttp(config.Id);
        var logger = _loggerFactory.CreateLogger(config.Id);
        return config.Kind switch
        {
            "OpenAICompatible" => CreateOpenAICompatible(config, http, logger, pipeline),
            "AnthropicMessages" => new ClaudeProvider(http, config, CastLogger<ClaudeProvider>(logger), pipeline),
            "Custom" => new CustomProvider(http, config, CastLogger<CustomProvider>(logger), pipeline),
            _ => throw new NotSupportedException($"Unknown provider kind: {config.Kind}")
        };
    }

    private HttpClient NewOwnedHttp(string providerId)
    {
        var client = new HttpClient();
        _ownedHttp[providerId] = client;
        return client;
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
