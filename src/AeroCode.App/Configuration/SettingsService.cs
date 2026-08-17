using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using AeroCode.AI.Configuration;
using AeroCode.App.Services;

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
            Current = JsonSerializer.Deserialize<AppSettings>(json) ?? CreateDefaults();
        }
        catch
        {
            Current = CreateDefaults();
        }
    }

    public async Task SaveAsync()
    {
        var json = JsonSerializer.Serialize(Current, JsonOpts);
        await File.WriteAllTextAsync(_paths.SettingsFile, json, Encoding.UTF8);
    }

    /// <summary>获取 AIOptions,直接喂给 ProviderFactory。</summary>
    public AIOptions ToAiOptions()
    {
        var ai = Current.Ai;
        // 兜底:至少保证有 deepseek 占位
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
            Providers = ai.Providers
        };
    }

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
