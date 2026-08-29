using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using AeroCode.AI.Configuration;
using AeroCode.App.Services;
using AeroCode.Mcp.Client;

namespace AeroCode.App.Configuration;

/// <summary>
/// 应用设置:从 %LOCALAPPDATA%\AeroCode\settings.json 加载/保存。
/// 包含 AI provider 配置 + UI 偏好。绝不硬编码 API key。
/// </summary>
public sealed class AppSettings
{
    [JsonPropertyName("ai")]
    public AISettings Ai { get; set; } = new();

    [JsonPropertyName("ui")]
    public UiSettings Ui { get; set; } = new();

    /// <summary>MCP server 连接配置（stdio 子进程）。空 = 未接入任何外部工具服务器。</summary>
    [JsonPropertyName("mcpServers")]
    public System.Collections.Generic.List<McpServerConfig> McpServers { get; set; } = new();
}

public sealed class AISettings
{
    [JsonPropertyName("defaultProviderId")]
    public string DefaultProviderId { get; set; } = "deepseek";

    [JsonPropertyName("defaultModel")]
    public string DefaultModel { get; set; } = "deepseek-v4-flash";

    [JsonPropertyName("providers")]
    public System.Collections.Generic.List<ProviderConfig> Providers { get; set; } = new();
}

public sealed class UiSettings
{
    [JsonPropertyName("theme")]
    public string Theme { get; set; } = "Dark";

    [JsonPropertyName("fontSize")]
    public int FontSize { get; set; } = 14;

    [JsonPropertyName("memoryMaxChars")]
    public int MemoryMaxChars { get; set; } = 2200;        // Hermes MEMORY.md cap

    [JsonPropertyName("userProfileMaxChars")]
    public int UserProfileMaxChars { get; set; } = 1375;   // Hermes USER.md cap
}

/// <summary>
/// 读写 settings.json。API key 不直接存文件,只存 env var 名 + 用 DPAPI 加密实际值(可选)。
/// </summary>
public sealed class SettingsService
{
    private readonly AppDataPaths _paths;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>读取选项：写入侧是 camelCase，读取必须大小写不敏感，
    /// 否则 provider 字段（id/baseUrl/...）在重载时全部静默丢失。</summary>
    private static readonly JsonSerializerOptions ReadOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public AppSettings Current { get; private set; } = new();

    public SettingsService(AppDataPaths paths)
    {
        _paths = paths;
        _paths.EnsureAll();
    }

    public async Task LoadAsync()
    {
        var path = _paths.SettingsFile;
        if (!File.Exists(path))
        {
            Current = CreateDefaults();
            await SaveAsync();
            return;
        }
        try
        {
            var json = await File.ReadAllTextAsync(path);
            Current = JsonSerializer.Deserialize<AppSettings>(json, ReadOpts) ?? CreateDefaults();
        }
        catch (JsonException)
        {
            // 仅内容损坏如实降级默认；IOException/UnauthorizedAccessException 等
            // 环境故障大声上抛（组合根记 WARN）——静默吞掉会让下次 Save
            // 用默认配置覆盖用户真实文件。
            Current = CreateDefaults();
        }
    }

    public async Task SaveAsync()
    {
        var json = JsonSerializer.Serialize(Current, JsonOpts);
        // 原子写（与 moa-options/permissions/profiles 三个存储同策略）：
        // 随机临时名 + Move 覆盖。直接 WriteAllText 写到一半进程崩溃/断电
        // 会留下半截文件，下次 Load 只能回退默认 → 用户 provider 配置静默全丢。
        var tmp = $"{_paths.SettingsFile}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(tmp, json, Encoding.UTF8);
        File.Move(tmp, _paths.SettingsFile, overwrite: true);
    }

    /// <summary>获取 AIOptions,直接喂给 ProviderFactory。</summary>
    public AIOptions ToAiOptions()
    {
        var ai = Current.Ai;
        // 兜底:一个 provider 都未配置时，给出可直接使用的 DeepSeek 默认配置
        // （配上 DEEPSEEK_API_KEY 环境变量即可连通，不是占位假配置）。
        if (ai.Providers.Count == 0)
        {
            ai.Providers.Add(new ProviderConfig
            {
                Id = "deepseek", DisplayName = "DeepSeek V4",
                Kind = "OpenAICompatible", BaseUrl = "https://api.deepseek.com/v1",
                DefaultModel = ai.DefaultModel, ApiKeyEnvVar = "DEEPSEEK_API_KEY"
            });
        }
        return new AIOptions
        {
            DefaultProviderId = ai.DefaultProviderId,
            DefaultModel = ai.DefaultModel,
            // 深拷贝快照：运行时只应看见"最近一次保存时点的配置"。
            // 共享活引用会让设置页未保存的编辑（如 BaseUrl）泄漏进 provider 发出的请求，
            // 违背热重载契约（保存 → Reload 才改变运行时）。
            Providers = ai.Providers.Select(Copy).ToList()
        };
    }

    private static ProviderConfig Copy(ProviderConfig p) => new()
    {
        Id = p.Id,
        DisplayName = p.DisplayName,
        Kind = p.Kind,
        BaseUrl = p.BaseUrl,
        DefaultModel = p.DefaultModel,
        ApiKeyEnvVar = p.ApiKeyEnvVar,
        RequiresApiKey = p.RequiresApiKey,
        SupportsStreaming = p.SupportsStreaming,
        SupportsToolCalling = p.SupportsToolCalling,
        SupportsThinking = p.SupportsThinking,
        ThinkingEfforts = p.ThinkingEfforts,
        TimeoutSeconds = p.TimeoutSeconds,
        ExtraHeaders = p.ExtraHeaders is null
            ? null
            : new Dictionary<string, string>(p.ExtraHeaders, StringComparer.Ordinal),
        ExtraBody = p.ExtraBody is null
            ? null
            : new Dictionary<string, object>(p.ExtraBody, StringComparer.Ordinal),
    };

    private static AppSettings CreateDefaults()
    {
        var s = new AppSettings();
        s.Ai.Providers.Add(new ProviderConfig
        {
            Id = "deepseek", DisplayName = "DeepSeek V4 (default)",
            Kind = "OpenAICompatible", BaseUrl = "https://api.deepseek.com/v1",
            DefaultModel = "deepseek-v4-flash", ApiKeyEnvVar = "DEEPSEEK_API_KEY"
        });
        s.Ai.Providers.Add(new ProviderConfig
        {
            Id = "minimax", DisplayName = "MiniMax M2 (minimaxi.com)",
            Kind = "OpenAICompatible", BaseUrl = "https://api.minimaxi.com/v1",
            DefaultModel = "MiniMax-M2", ApiKeyEnvVar = "MINIMAX_API_KEY",
            // reasoning_split=true: 把 thinking 分离到 reasoning_content 字段, content 是干净输出
            ExtraBody = new System.Collections.Generic.Dictionary<string, object>
            {
                ["reasoning_split"] = true
            }
        });
        s.Ai.Providers.Add(new ProviderConfig
        {
            Id = "qwen", DisplayName = "Qwen (DashScope)",
            Kind = "OpenAICompatible", BaseUrl = "https://dashscope.aliyuncs.com/compatible-mode/v1",
            DefaultModel = "qwen3-max", ApiKeyEnvVar = "DASHSCOPE_API_KEY"
        });
        s.Ai.Providers.Add(new ProviderConfig
        {
            Id = "ollama", DisplayName = "Ollama (local)",
            Kind = "OpenAICompatible", BaseUrl = "http://localhost:11434/v1",
            DefaultModel = "qwen2.5:7b", RequiresApiKey = false
        });
        return s;
    }
}
