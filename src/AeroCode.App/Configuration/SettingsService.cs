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

    /// <summary>工作区工具域设置（批次 A：工作区根 / git 工作流 / 启动档位）。</summary>
    [JsonPropertyName("workspace")]
    public WorkspaceSettings Workspace { get; set; } = new();

    /// <summary>子代理派发（批次 B）：字段对照 SubagentOptions，组合根映射注入。</summary>
    [JsonPropertyName("subagent")]
    public SubagentSettings Subagent { get; set; } = new();

    /// <summary>安全控制（批次 B）：守卫链/审批熔断/智能审批/急停哨兵。</summary>
    [JsonPropertyName("safety")]
    public SafetySettings Safety { get; set; } = new();

    /// <summary>事件钩子引擎开关（批次 B）。</summary>
    [JsonPropertyName("hooks")]
    public HooksSettings Hooks { get; set; } = new();

    /// <summary>自动化调度开关（批次 B）。</summary>
    [JsonPropertyName("scheduler")]
    public SchedulerSettings Scheduler { get; set; } = new();

    /// <summary>会话记忆（批次 B G2）：召回条数与自动沉淀开关。</summary>
    [JsonPropertyName("memory")]
    public MemorySettings Memory { get; set; } = new();

    /// <summary>上下文压缩（批次 B G2）：工具循环溢出检测阈值。</summary>
    [JsonPropertyName("compaction")]
    public CompactionSettings Compaction { get; set; } = new();
}

/// <summary>子代理设置节。MaxTurns/MaxCostUsd 由派发方（工具/任务）按需映射进 SubAgentSpec。</summary>
public sealed class SubagentSettings
{
    /// <summary>false 时派发诚实失败（SubAgentRunner 抛 InvalidOperationException）。</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>深度上限（层数含自身；被 SubAgentSpec.MaxDepth=4 硬上限钳制）。</summary>
    [JsonPropertyName("maxDepth")]
    public int MaxDepth { get; set; } = 4;

    /// <summary>同时运行的子代理实例上限（≥1，超限排队）。</summary>
    [JsonPropertyName("maxParallel")]
    public int MaxParallel { get; set; } = 2;

    /// <summary>单次派发的默认工具轮数上限（>0）。</summary>
    [JsonPropertyName("maxTurns")]
    public int MaxTurns { get; set; } = 16;

    /// <summary>单次派发的默认成本上限（美元，≥0；0 = 不设计价上限）。</summary>
    [JsonPropertyName("maxCostUsd")]
    public double MaxCostUsd { get; set; } = 1.0;
}

/// <summary>安全设置节。EstopFile 为空 = 不启用急停哨兵检查；AdvisorModel 为空 = 智能审批建议器不可用。</summary>
public sealed class SafetySettings
{
    /// <summary>doom-loop 阈值：同工具同参数第 N 次升级 Ask（≥2）。</summary>
    [JsonPropertyName("doomLoopThreshold")]
    public int DoomLoopThreshold { get; set; } = 3;

    /// <summary>急停哨兵文件路径；空 = 不启用（不构建 EstopGuard）。</summary>
    [JsonPropertyName("estopFile")]
    public string EstopFile { get; set; } = string.Empty;

    /// <summary>智能审批判定 risk=low 时自动放行（记录在案；false = 一律弹窗）。</summary>
    [JsonPropertyName("autoApproveLowRisk")]
    public bool AutoApproveLowRisk { get; set; }

    /// <summary>审批建议器判定模型（便宜档）；空 = advisor 不可用，审批行为不变。</summary>
    [JsonPropertyName("advisorModel")]
    public string AdvisorModel { get; set; } = string.Empty;

    /// <summary>审批熔断：连续批准次数阈值（≥1）。</summary>
    [JsonPropertyName("approvalBurstLimit")]
    public int ApprovalBurstLimit { get; set; } = 25;

    /// <summary>审批熔断：会话累计成本阈值（美元，>0）。</summary>
    [JsonPropertyName("approvalCostLimitUsd")]
    public double ApprovalCostLimitUsd { get; set; } = 5.0;
}

/// <summary>钩子引擎设置节（hooks.json 缺失 = 空载，不是降级）。</summary>
public sealed class HooksSettings
{
    /// <summary>false = 不加载 hooks.json 也不订阅事件分发。</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;
}

/// <summary>调度服务设置节（jobs.json 缺失 = 空载，不是降级）。</summary>
public sealed class SchedulerSettings
{
    /// <summary>false = 不启动轮询 Timer（注册仍生效，供设置页查看/编辑）。</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;
}

/// <summary>会话记忆设置节（批次 B G2）。</summary>
public sealed class MemorySettings
{
    /// <summary>语义召回 Top-K 条数（0 = 关闭笔记召回，仅注入 MEMORY.md/USER.md）。</summary>
    [JsonPropertyName("recallTopK")]
    public int RecallTopK { get; set; } = 5;

    /// <summary>对话轮结束后自动沉淀经验（真实轨迹/失败教训入 ExperienceStore）。</summary>
    [JsonPropertyName("autoConsolidate")]
    public bool AutoConsolidate { get; set; } = true;
}

/// <summary>上下文压缩设置节（批次 B G2）。ThresholdTokens ≤ 0 = 关闭溢出检测。</summary>
public sealed class CompactionSettings
{
    /// <summary>工具循环上下文 token 估算阈值（4 字符≈1 token 的既有口径）。</summary>
    [JsonPropertyName("thresholdTokens")]
    public int ThresholdTokens { get; set; } = 24000;

    /// <summary>压缩后保留的最近消息条数（Compactor 语义）。</summary>
    [JsonPropertyName("keepRecentMessages")]
    public int KeepRecentMessages { get; set; } = 10;
}

/// <summary>
/// 工作区设置节。Root 为空串 = 使用 Documents/AeroCode-workspace（首次惰性创建）；
/// 指定了 Root 但目录无法创建时组合根诚实降级（不注册 workspace/git 工具域），绝不伪造根路径。
/// </summary>
public sealed class WorkspaceSettings
{
    /// <summary>工作区根目录；空 = 用 Documents/AeroCode-workspace，首次惰性创建。</summary>
    [JsonPropertyName("root")]
    public string Root { get; set; } = string.Empty;

    /// <summary>编辑后自动 git 提交（不在 git 仓时如实跳过，不伪造提交）。</summary>
    [JsonPropertyName("autoCommit")]
    public bool AutoCommit { get; set; }

    /// <summary>脏区保护：存在与本次编辑无关的未暂存改动时不自动提交，等用户决定。</summary>
    [JsonPropertyName("protectDirty")]
    public bool ProtectDirty { get; set; } = true;

    /// <summary>启动即 AcceptEdits 档（文件编辑免逐次确认；shell 与网络仍走原规则）。</summary>
    [JsonPropertyName("autoApproveEdits")]
    public bool AutoApproveEdits { get; set; }

    /// <summary>run_shell 默认超时秒数（超时杀整棵进程树；单条命令可用参数覆盖）。</summary>
    [JsonPropertyName("shellTimeoutSeconds")]
    public int ShellTimeoutSeconds { get; set; } = 60;
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
