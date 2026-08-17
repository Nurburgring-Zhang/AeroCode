using System;
using System.Collections.Generic;

namespace AeroCode.AI.Configuration;

/// <summary>
/// 单个 Provider 配置。从 settings.json 加载,绝不在代码里硬编码。
/// </summary>
public sealed class ProviderConfig
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    /// <summary>OpenAICompatible / AnthropicMessages / Custom</summary>
    public string Kind { get; set; } = "OpenAICompatible";
    /// <summary>OpenAI 兼容 API 端点 (例如 https://api.deepseek.com/v1)</summary>
    public string BaseUrl { get; set; } = string.Empty;
    /// <summary>默认模型,例如 deepseek-v4-flash</summary>
    public string DefaultModel { get; set; } = string.Empty;
    /// <summary>API key 从环境变量读取的 key 名,例如 DEEPSEEK_API_KEY</summary>
    public string? ApiKeyEnvVar { get; set; }
    /// <summary>是否需要 API key (Ollama / LMStudio 本地一般不需要)</summary>
    public bool RequiresApiKey { get; set; } = true;
    /// <summary>是否支持流式</summary>
    public bool SupportsStreaming { get; set; } = true;
    public bool SupportsToolCalling { get; set; } = true;
    public bool SupportsThinking { get; set; } = true;
    /// <summary>thinking 强度档位,逗号分隔,例如 "low,medium,high,max"</summary>
    public string? ThinkingEfforts { get; set; }
    /// <summary>HTTP 请求超时 (秒)</summary>
    public int TimeoutSeconds { get; set; } = 120;
    /// <summary>额外 HTTP header (用于自定义认证/代理)</summary>
    public Dictionary<string, string>? ExtraHeaders { get; set; }
    /// <summary>额外 JSON 字段,合并到请求 body 根级 (provider-specific 选项如 reasoning_split=true)</summary>
    public Dictionary<string, object>? ExtraBody { get; set; }
}

/// <summary>
/// 顶层 AI 配置。含 provider 列表 + 全局默认。
/// </summary>
public sealed class AIOptions
{
    public const string SectionName = "AI";

    public string DefaultProviderId { get; set; } = "deepseek";
    public string DefaultModel { get; set; } = "deepseek-v4-flash";
    public List<ProviderConfig> Providers { get; set; } = new();
}
