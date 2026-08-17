// Copyright (c) AeroCode V3.0
// SettingsViewModel — UI for editing app settings (theme/providers/model profiles/font/memory caps).
// All edits go back into AppSettings / permissions.json / moa-profiles.json on disk; no in-memory only state.
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AeroAgent.Conversation.Models;
using AeroAgent.Moa.Profiles;
using AeroAgent.Moa.Strategies;
using AeroCode.AI.Configuration;
using AeroCode.AI.Providers;
using AeroCode.App.Configuration;
using AeroCode.App.Services;
using AeroCode.Harness.Permission;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AeroCode.App.ViewModels;

/// <summary>权限决策的下拉选项（枚举值 + 人类可读显示）。</summary>
public sealed record DecisionChoice(PermissionDecision Value, string Display);

/// <summary>
/// 权限规则列表段的一行：工具名（只读）+ 备注（只读）+ 用户决策（可编辑）。
/// 决策三态都可显式选择——"每次询问"是用户的合法意图，保存后即持久化。
/// </summary>
public sealed partial class PermissionRuleItem : ObservableObject
{
    public static readonly IReadOnlyList<DecisionChoice> AllChoices = new[]
    {
        new DecisionChoice(PermissionDecision.Allow, "✅ 允许"),
        new DecisionChoice(PermissionDecision.Deny, "⛔ 拒绝"),
        new DecisionChoice(PermissionDecision.Ask, "❓ 每次询问"),
    };

    public string ToolName { get; }
    public string? Notes { get; }

    /// <summary>绑定到 ComboBox 的选项源（所有行共享同一份）。</summary>
    public IReadOnlyList<DecisionChoice> DecisionChoices => AllChoices;

    [ObservableProperty]
    private DecisionChoice _decision;

    public PermissionRuleItem(string toolName, string? notes, PermissionDecision current)
    {
        ToolName = toolName;
        Notes = notes;
        _decision = AllChoices.FirstOrDefault(c => c.Value == current)
                    ?? AllChoices[2];
    }
}

/// <summary>速度档位的下拉选项。</summary>
public sealed record SpeedChoice(SpeedTier Value, string Display);

/// <summary>
/// MOA 角色绑定的下拉选项：Binding 为 null = 自动分配（按画像）；
/// 孤儿绑定（provider 已删）也会如实成列并标注"未配置"，绝不静默改回自动。
/// </summary>
public sealed record RoleBindingChoice(string Display, ModelBinding? Binding);

/// <summary>单个强项复选项（每个画像行持有独立的 8+ 项集合）。</summary>
public sealed partial class StrengthToggle : ObservableObject
{
    public string Name { get; }

    [ObservableProperty]
    private bool _isChecked;

    public StrengthToggle(string name, bool isChecked)
    {
        Name = name;
        IsChecked = isChecked;
    }
}

/// <summary>
/// 模型画像编辑行：Provider/模型 Id、强项多选、上下文/成本/速度可编辑，
/// 运行统计只读（运行时自学习拥有 Stats 的所有权，UI 不得编辑）。
/// 成本用文本承载——空 = 未知（null，成本核算绝不估算），非法数字在 Save 时如实报错。
/// </summary>
public sealed partial class ProfileEditorItem : ObservableObject
{
    public static readonly IReadOnlyList<SpeedChoice> AllSpeeds = new[]
    {
        new SpeedChoice(SpeedTier.Fast, "⚡ Fast"),
        new SpeedChoice(SpeedTier.Medium, "🚶 Medium"),
        new SpeedChoice(SpeedTier.Slow, "🐢 Slow"),
    };

    [ObservableProperty]
    private string _providerId;

    /// <summary>空串 = 该 provider 的默认模型画像。</summary>
    [ObservableProperty]
    private string _modelId;

    [ObservableProperty]
    private int _contextWindow;

    [ObservableProperty]
    private int _maxOutputTokens;

    /// <summary>每百万输入 token 美元成本；空 = 未知。</summary>
    [ObservableProperty]
    private string _costInText;

    /// <summary>每百万输出 token 美元成本；空 = 未知。</summary>
    [ObservableProperty]
    private string _costOutText;

    [ObservableProperty]
    private SpeedChoice _speed;

    public ObservableCollection<StrengthToggle> Strengths { get; }

    /// <summary>运行自学习统计的只读展示文本。</summary>
    public string StatsText { get; }

    /// <summary>绑定到 ComboBox 的选项源（所有行共享同一份）。</summary>
    public IReadOnlyList<SpeedChoice> SpeedChoices => AllSpeeds;

    private ProfileEditorItem(
        string providerId,
        string modelId,
        IEnumerable<StrengthToggle> strengths,
        int contextWindow,
        int maxOutputTokens,
        string costInText,
        string costOutText,
        SpeedChoice speed,
        string statsText)
    {
        ProviderId = providerId;
        ModelId = modelId;
        Strengths = new ObservableCollection<StrengthToggle>(strengths);
        ContextWindow = contextWindow;
        MaxOutputTokens = maxOutputTokens;
        CostInText = costInText;
        CostOutText = costOutText;
        Speed = speed;
        StatsText = statsText;
    }

    public static ProfileEditorItem FromProfile(ModelProfile p)
    {
        ArgumentNullException.ThrowIfNull(p);
        var normalized = p.Strengths.Select(ModelStrength.Normalize).ToList();
        var checkedSet = new HashSet<string>(normalized, StringComparer.Ordinal);
        var toggles = ModelStrength.All
            .Select(s => new StrengthToggle(s, checkedSet.Contains(s)))
            .ToList();
        // 自定义强项（不在内建 8 项里）如实追加为勾选项，不静默丢弃
        foreach (var custom in normalized
                     .Where(s => !ModelStrength.All.Contains(s))
                     .Distinct(StringComparer.Ordinal))
        {
            toggles.Add(new StrengthToggle(custom, true));
        }

        return new ProfileEditorItem(
            p.ProviderId,
            p.ModelId,
            toggles,
            p.ContextWindow,
            p.MaxOutputTokens,
            FormatCost(p.CostPerMIn),
            FormatCost(p.CostPerMOut),
            AllSpeeds.FirstOrDefault(c => c.Value == p.SpeedTier) ?? AllSpeeds[1],
            FormatStats(p.Stats));
    }

    public static ProfileEditorItem NewEmpty(string providerId) => new(
        providerId,
        string.Empty,
        ModelStrength.All.Select(s => new StrengthToggle(s, s == ModelStrength.General)),
        contextWindow: 0,
        maxOutputTokens: 0,
        costInText: string.Empty,
        costOutText: string.Empty,
        AllSpeeds.Single(c => c.Value == SpeedTier.Medium),
        FormatStats(new ProfileStats()));

    /// <summary>
    /// 从 UI 值构建画像。成本文本非法 / Provider Id 为空时返回 false 并给出错误消息（不抛异常）。
    /// </summary>
    public bool TryBuildProfile(out ModelProfile? profile, out string? error)
    {
        profile = null;
        if (string.IsNullOrWhiteSpace(ProviderId))
        {
            error = "Provider Id 不能为空";
            return false;
        }

        if (!ParseNullableCost(CostInText, out var costIn, out var errIn))
        {
            error = $"输入成本非法：{errIn}";
            return false;
        }

        if (!ParseNullableCost(CostOutText, out var costOut, out var errOut))
        {
            error = $"输出成本非法：{errOut}";
            return false;
        }

        profile = new ModelProfile
        {
            ProviderId = ProviderId.Trim(),
            ModelId = ModelId?.Trim() ?? string.Empty,
            Strengths = Strengths.Where(s => s.IsChecked)
                .Select(s => ModelStrength.Normalize(s.Name))
                .ToList(),
            ContextWindow = Math.Max(0, ContextWindow),
            MaxOutputTokens = Math.Max(0, MaxOutputTokens),
            CostPerMIn = costIn,
            CostPerMOut = costOut,
            SpeedTier = Speed.Value,
        };
        error = null;
        return true;
    }

    private static string FormatCost(double? cost) =>
        cost.HasValue ? cost.Value.ToString(CultureInfo.InvariantCulture) : string.Empty;

    private static string FormatStats(ProfileStats stats) =>
        stats.Calls <= 0
            ? "暂无调用记录"
            : $"调用 {stats.Calls} · 均延 {stats.AvgLatencyMs:F0} ms · 失败 {stats.Failures} ({stats.FailureRate:P1})";

    private static bool ParseNullableCost(string? text, out double? value, out string? error)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = null;
            return true;
        }

        if (double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            && !double.IsNaN(v) && !double.IsInfinity(v))
        {
            if (v < 0)
            {
                error = "成本不能为负";
                return false;
            }

            value = v;
            error = null;
            return true;
        }

        error = $"无法解析 '{text.Trim()}'（示例：0.3，小数点，不变区域格式）";
        return false;
    }
}

public sealed partial class SettingsViewModel : ObservableObject
{
    /// <summary>连通性测试探针的总超时（本地/远端端点都不得把 UI 挂死）。</summary>
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);

    private readonly SettingsService _settings;
    private readonly ThemeService _theme;
    private readonly ProviderFactory _providerFactory;
    private readonly PermissionPolicy _permission;
    private readonly JsonPermissionStore _permissionStore;
    private readonly IModelProfileCatalog _profileCatalog;
    private readonly MoaOptions _moaOptions;
    private readonly JsonMoaOptionsStore _moaOptionsStore;
    private readonly ILogger<SettingsViewModel> _logger;

    /// <summary>
    /// JSON 非法而未能提交的 ExtraHeaders/ExtraBody 文本（按 config 实例暂存）：
    /// 切换 provider 或重开水合都不丢用户原文，Save 时如实报错并整体阻止落盘；
    /// 解析成功后立即清除。
    /// </summary>
    private readonly Dictionary<ProviderConfig, PendingExtraTexts> _pendingExtras = new();

    private sealed record PendingExtraTexts(string Headers, string Body, string Error);

    private static readonly JsonSerializerOptions ExtraJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [ObservableProperty]
    private string _selectedTheme = ThemeService.Dark;

    [ObservableProperty]
    private int _fontSize = 14;

    [ObservableProperty]
    private int _memoryMaxChars = 2200;       // MEMORY.md cap (Hermes)
    [ObservableProperty]
    private int _userProfileMaxChars = 1375;  // USER.md cap (Hermes)

    [ObservableProperty]
    private string _defaultProviderId = "deepseek";
    [ObservableProperty]
    private string _defaultModel = string.Empty;

    [ObservableProperty]
    private string _statusText = string.Empty;
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>当前选中 provider 的 ExtraHeaders JSON 文本（切换/保存时提交回 config）。</summary>
    [ObservableProperty]
    private string _extraHeadersJson = string.Empty;

    /// <summary>当前选中 provider 的 ExtraBody JSON 文本（切换/保存时提交回 config）。</summary>
    [ObservableProperty]
    private string _extraBodyJson = string.Empty;

    /// <summary>模型画像编辑段（Save 时 Upsert/合并删除并落盘 moa-profiles.json）。</summary>
    public ObservableCollection<ProfileEditorItem> ProfileEditors { get; } = new();

    [ObservableProperty]
    private ProfileEditorItem? _selectedProfile;

    /// <summary>水合时刻的画像键快照——Save 的合并删除基线（运行时新增画像不受牵连）。</summary>
    private List<(string ProviderId, string ModelId)> _hydratedProfileKeys = new();

    // ==================== MOA 编排策略段 ====================

    /// <summary>默认策略候选（全部编排策略枚举值）。</summary>
    public ObservableCollection<OrchestrationStrategy> StrategyChoices { get; } =
        new(Enum.GetValues<OrchestrationStrategy>());

    /// <summary>新会话默认策略（Save 写回 MoaOptions，ChatViewModel 经 OptionsChanged 刷新）。</summary>
    [ObservableProperty]
    private OrchestrationStrategy _defaultStrategy;

    /// <summary>四角色共用的绑定选项列表（自动分配 + 每个 provider 默认模型 + 画像里的具体模型）。</summary>
    public ObservableCollection<RoleBindingChoice> RoleBindingChoices { get; } = new();

    [ObservableProperty]
    private RoleBindingChoice? _routerChoice;
    [ObservableProperty]
    private RoleBindingChoice? _plannerChoice;
    [ObservableProperty]
    private RoleBindingChoice? _synthesizerChoice;
    [ObservableProperty]
    private RoleBindingChoice? _judgeChoice;

    /// <summary>Ensemble 并行作答模型数（Save 时钳制到 2..4）。</summary>
    [ObservableProperty]
    private int _ensembleSize = 2;

    /// <summary>单轮成本上限文本（美元）；留空 = 不限制，非法数字 Save 时如实报错。</summary>
    [ObservableProperty]
    private string _maxUsdText = string.Empty;

    /// <summary>工具循环开关（worker 是否携带 tools 多轮执行）。</summary>
    [ObservableProperty]
    private bool _toolsEnabled = true;

    public ObservableCollection<ThemeChoice> Themes { get; } = new()
    {
        new(ThemeService.Light, "☀️ Light"),
        new(ThemeService.Dark, "🌙 Dark"),
        new(ThemeService.System, "🖥️ Follow System"),
    };

    public ObservableCollection<string> AvailableProviderIds { get; } = new();

    public ObservableCollection<ProviderConfig> Providers { get; } = new();

    [ObservableProperty]
    private ProviderConfig? _selectedProvider;

    /// <summary>工具权限规则列表段（策略快照；Save 时写回策略并持久化）。</summary>
    public ObservableCollection<PermissionRuleItem> PermissionRules { get; } = new();

    public SettingsViewModel(
        SettingsService settings,
        ThemeService theme,
        ProviderFactory providerFactory,
        PermissionPolicy permission,
        JsonPermissionStore permissionStore,
        IModelProfileCatalog profileCatalog,
        MoaOptions moaOptions,
        JsonMoaOptionsStore moaOptionsStore,
        ILogger<SettingsViewModel> logger)
    {
        _settings = settings;
        _theme = theme;
        _providerFactory = providerFactory;
        _permission = permission ?? throw new ArgumentNullException(nameof(permission));
        _permissionStore = permissionStore ?? throw new ArgumentNullException(nameof(permissionStore));
        _profileCatalog = profileCatalog ?? throw new ArgumentNullException(nameof(profileCatalog));
        _moaOptions = moaOptions ?? throw new ArgumentNullException(nameof(moaOptions));
        _moaOptionsStore = moaOptionsStore ?? throw new ArgumentNullException(nameof(moaOptionsStore));
        _logger = logger;
        HydrateFromSettings();
        HydratePermissionRules();
        HydrateProfiles();
        HydrateMoaSection();
    }

    /// <summary>从当前策略快照重建规则行（构造时 + Reload + 授权对话框改动后再打开设置页）。</summary>
    public void HydratePermissionRules()
    {
        PermissionRules.Clear();
        foreach (var rule in _permission.ListRules())
        {
            PermissionRules.Add(new PermissionRuleItem(rule.ToolName, rule.Notes, rule.DefaultDecision));
        }
    }

    /// <summary>从画像目录快照重建编辑行（构造时 + Reload + 每次开窗）。</summary>
    public void HydrateProfiles()
    {
        ProfileEditors.Clear();
        _hydratedProfileKeys.Clear();
        foreach (var p in _profileCatalog.List())
        {
            _hydratedProfileKeys.Add((p.ProviderId, p.ModelId));
            ProfileEditors.Add(ProfileEditorItem.FromProfile(p));
        }

        SelectedProfile = ProfileEditors.FirstOrDefault();
    }

    /// <summary>
    /// 从生效中的 MoaOptions 单例重建 MOA 段（构造时 + Reload + 每次开窗）。
    /// 策略在每轮直接读单例字段，因此这里读到的就是运行时真相。
    /// </summary>
    public void HydrateMoaSection()
    {
        DefaultStrategy = _moaOptions.DefaultStrategy;
        EnsembleSize = Math.Clamp(_moaOptions.EnsembleSize, 2, 4);
        MaxUsdText = _moaOptions.MaxUsdPerTurn.HasValue
            ? _moaOptions.MaxUsdPerTurn.Value.ToString(CultureInfo.InvariantCulture)
            : string.Empty;
        ToolsEnabled = _moaOptions.ToolsEnabled;

        RebuildRoleBindingChoices();
        RouterChoice = ResolveChoice(_moaOptions.Router);
        PlannerChoice = ResolveChoice(_moaOptions.Planner);
        SynthesizerChoice = ResolveChoice(_moaOptions.Synthesizer);
        JudgeChoice = ResolveChoice(_moaOptions.Judge);
    }

    /// <summary>
    /// 重建四角色共用的绑定选项：自动分配 + 每个 provider 的默认模型 +
    /// 画像目录里的具体模型（含运行时自学习新增的）。增删 provider 后同样调用，
    /// 保证角色下拉与 provider 列表同步；已有选择按绑定值相等恢复。
    /// </summary>
    public void RebuildRoleBindingChoices()
    {
        var current = new[]
        {
            RouterChoice?.Binding, PlannerChoice?.Binding,
            SynthesizerChoice?.Binding, JudgeChoice?.Binding,
        };

        RoleBindingChoices.Clear();
        RoleBindingChoices.Add(new RoleBindingChoice("🤖 自动分配（按画像）", null));
        foreach (var p in Providers)
        {
            RoleBindingChoices.Add(new RoleBindingChoice($"{p.Id}（默认模型）", new ModelBinding(p.Id, null)));
        }

        foreach (var profile in _profileCatalog.List())
        {
            if (string.IsNullOrWhiteSpace(profile.ModelId))
            {
                continue; // 默认模型画像已由 provider 行覆盖
            }

            var binding = new ModelBinding(profile.ProviderId, profile.ModelId);
            if (RoleBindingChoices.Any(c => c.Binding == binding))
            {
                continue;
            }

            RoleBindingChoices.Add(new RoleBindingChoice($"{profile.ProviderId} :: {profile.ModelId}", binding));
        }

        RouterChoice = ResolveChoice(current[0]);
        PlannerChoice = ResolveChoice(current[1]);
        SynthesizerChoice = ResolveChoice(current[2]);
        JudgeChoice = ResolveChoice(current[3]);
    }

    /// <summary>把绑定映射到下拉项；孤儿绑定（provider 已删）如实追加"未配置"项，绝不静默改回自动分配。</summary>
    private RoleBindingChoice ResolveChoice(ModelBinding? binding)
    {
        if (binding is null)
        {
            return RoleBindingChoices[0];
        }

        var found = RoleBindingChoices.FirstOrDefault(c => c.Binding == binding);
        if (found is not null)
        {
            return found;
        }

        var modelText = string.IsNullOrWhiteSpace(binding.ModelId) ? "默认模型" : binding.ModelId;
        var orphan = new RoleBindingChoice($"{binding.ProviderId} :: {modelText}（未配置）", binding);
        RoleBindingChoices.Add(orphan);
        return orphan;
    }

    /// <summary>
    /// 每次打开设置窗口前必须调用：本 VM 是单例，窗口关闭后快照即过期。
    /// 不刷新就 Save 会用陈旧快照合并，可能擦掉期间"记住"的授权决策或运行时自学习画像。
    /// </summary>
    public void RefreshFromSources()
    {
        HydrateFromSettings();
        HydratePermissionRules();
        HydrateProfiles();
        HydrateMoaSection();
    }

    private void HydrateFromSettings()
    {
        var s = _settings.Current;
        SelectedTheme = string.IsNullOrWhiteSpace(s.Ui.Theme) ? ThemeService.Dark : s.Ui.Theme;
        FontSize = Math.Clamp(s.Ui.FontSize, 10, 22);
        MemoryMaxChars = Math.Clamp(s.Ui.MemoryMaxChars > 0 ? s.Ui.MemoryMaxChars : 2200, 200, 20000);
        UserProfileMaxChars = Math.Clamp(s.Ui.UserProfileMaxChars > 0 ? s.Ui.UserProfileMaxChars : 1375, 200, 10000);
        DefaultProviderId = s.Ai.DefaultProviderId;
        DefaultModel = s.Ai.DefaultModel;
        AvailableProviderIds.Clear();
        foreach (var p in s.Ai.Providers) AvailableProviderIds.Add(p.Id);
        Providers.Clear();
        foreach (var p in s.Ai.Providers) Providers.Add(p);
        SelectedProvider = Providers.FirstOrDefault(p => p.Id == DefaultProviderId) ?? Providers.FirstOrDefault();
        // 重水合 = 配置全部来自磁盘/当前设置，此前未提交的非法文本已过期作废
        _pendingExtras.Clear();
    }

    /// <summary>切走 provider 前把在编辑的 Extra* 文本提交回旧 config（合法写字，非法暂存原文）。</summary>
    partial void OnSelectedProviderChanging(ProviderConfig? oldValue, ProviderConfig? newValue)
    {
        CommitExtraTexts(oldValue);
    }

    /// <summary>选中新 provider 后把它的 Extra* 载入编辑文本框。</summary>
    partial void OnSelectedProviderChanged(ProviderConfig? value)
    {
        LoadExtraTexts(value);
    }

    private void LoadExtraTexts(ProviderConfig? config)
    {
        if (config is null)
        {
            ExtraHeadersJson = string.Empty;
            ExtraBodyJson = string.Empty;
            return;
        }

        if (_pendingExtras.TryGetValue(config, out var pending))
        {
            // 有尚未成功提交的非法文本：显示用户原文（而非 config 里的旧字典），继续给修正机会
            ExtraHeadersJson = pending.Headers;
            ExtraBodyJson = pending.Body;
            return;
        }

        ExtraHeadersJson = SerializeExtra(config.ExtraHeaders);
        ExtraBodyJson = SerializeExtra(config.ExtraBody);
    }

    private void CommitExtraTexts(ProviderConfig? config)
    {
        if (config is null) return;

        var okHeaders = TryParseExtraHeaders(ExtraHeadersJson, out var headers, out var headersError);
        var okBody = TryParseExtraBody(ExtraBodyJson, out var body, out var bodyError);
        if (okHeaders && okBody)
        {
            config.ExtraHeaders = headers;
            config.ExtraBody = body;
            _pendingExtras.Remove(config);
        }
        else
        {
            var errors = string.Join("；", new[] { headersError, bodyError }.Where(e => e is not null));
            _pendingExtras[config] = new PendingExtraTexts(ExtraHeadersJson, ExtraBodyJson, errors);
        }
    }

    private static string SerializeExtra<T>(Dictionary<string, T>? dict)
    {
        if (dict is null || dict.Count == 0) return string.Empty;
        return JsonSerializer.Serialize(dict, ExtraJsonOptions);
    }

    /// <summary>解析 ExtraHeaders 文本：空 = 未设置；必须是 JSON 对象、字符串值、键非空。</summary>
    internal static bool TryParseExtraHeaders(string? text, out Dictionary<string, string>? headers, out string? error)
    {
        headers = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = null;
            return true;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(text);
            if (parsed is null)
            {
                error = "ExtraHeaders 根必须是 JSON 对象";
                return false;
            }

            foreach (var kv in parsed)
            {
                if (string.IsNullOrWhiteSpace(kv.Key))
                {
                    error = "Header 名不能为空";
                    return false;
                }

                if (kv.Value is null)
                {
                    error = $"Header '{kv.Key}' 的值不能为 null";
                    return false;
                }
            }

            headers = parsed;
            error = null;
            return true;
        }
        catch (JsonException ex)
        {
            error = $"ExtraHeaders JSON 非法：{ex.Message}";
            return false;
        }
    }

    /// <summary>解析 ExtraBody 文本：空 = 未设置；必须是 JSON 对象、键非空（值任意类型，原样合并进请求 body）。</summary>
    internal static bool TryParseExtraBody(string? text, out Dictionary<string, object>? body, out string? error)
    {
        body = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = null;
            return true;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, object>>(text);
            if (parsed is null)
            {
                error = "ExtraBody 根必须是 JSON 对象";
                return false;
            }

            foreach (var kv in parsed)
            {
                if (string.IsNullOrWhiteSpace(kv.Key))
                {
                    error = "ExtraBody 字段名不能为空";
                    return false;
                }
            }

            body = parsed;
            error = null;
            return true;
        }
        catch (JsonException ex)
        {
            error = $"ExtraBody JSON 非法：{ex.Message}";
            return false;
        }
    }

    [RelayCommand]
    public void SelectProvider(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        var p = Providers.FirstOrDefault(x => x.Id == id);
        if (p is not null) SelectedProvider = p;
    }

    [RelayCommand]
    public void AddProvider()
    {
        var p = new ProviderConfig
        {
            Id = $"custom-{Guid.NewGuid().ToString("N").Substring(0, 6)}",
            DisplayName = "New Provider",
            Kind = "OpenAICompatible",
            BaseUrl = "https://api.example.com/v1",
            DefaultModel = "model-name",
            ApiKeyEnvVar = "MY_API_KEY",
            RequiresApiKey = true
        };
        Providers.Add(p);
        AvailableProviderIds.Add(p.Id);
        RebuildRoleBindingChoices();
        SelectedProvider = p;
    }

    [RelayCommand]
    public void RemoveSelectedProvider()
    {
        if (SelectedProvider is null) return;
        if (Providers.Count <= 1)
        {
            StatusText = "⚠️ 至少保留一个 Provider";
            return;
        }
        var removed = SelectedProvider;
        var id = removed.Id;
        Providers.Remove(removed);
        AvailableProviderIds.Remove(id);
        _pendingExtras.Remove(removed);
        RebuildRoleBindingChoices();
        if (DefaultProviderId == id)
            DefaultProviderId = Providers[0].Id;
        SelectedProvider = Providers[0];
    }

    /// <summary>
    /// 单个 provider 连通性测试：用编辑中（可未保存）的配置构建一次性探针，
    /// 发真实最小补全请求。结果如实呈现——成功标绿含耗时，失败标红说明可能原因。
    /// </summary>
    [RelayCommand]
    public async Task TestSelectedProviderAsync()
    {
        var p = SelectedProvider;
        if (p is null)
        {
            StatusText = "⚠️ 请先选择要测试的 Provider";
            return;
        }

        CommitExtraTexts(p);
        if (_pendingExtras.TryGetValue(p, out var pending))
        {
            StatusText = $"❌ 无法测试 Provider '{p.Id}'：{pending.Error}";
            return;
        }

        try
        {
            IsBusy = true;
            StatusText = $"⏳ 正在测试 {p.Id} 连通性（真实最小请求，≤{ProbeTimeout.TotalSeconds:0}s）...";
            var probe = _providerFactory.CreateProbe(p);
            var sw = Stopwatch.StartNew();
            using var cts = new CancellationTokenSource(ProbeTimeout);
            var ok = await probe.HealthCheckAsync(cts.Token);
            StatusText = ok
                ? $"🟢 {p.Id} 连通正常（{sw.ElapsedMilliseconds} ms）"
                : $"🔴 {p.Id} 连通失败（端点不可达 / 鉴权未通过 / 模型名不存在，详见日志）";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Probe creation failed for {Id}", p.Id);
            StatusText = $"🔴 {p.Id} 测试异常：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task SaveAsync()
    {
        // 重入守卫：IsBusy 之前只被设置从未被检查——双击 Save 会并发跑两份保存，
        // 并发写同一 settings.json 可能损坏文件。UI 单线程，此检查即够。
        if (IsBusy)
        {
            return;
        }

        try
        {
            IsBusy = true;

            // ── 校验门 0：Provider Id 合法性（空/重复会让工厂检索与角色绑定语义含混 → 整体拒存）──
            foreach (var p in Providers)
            {
                if (string.IsNullOrWhiteSpace(p.Id))
                {
                    StatusText = "❌ Provider Id 不能为空";
                    return;
                }
            }

            var dupId = Providers.GroupBy(p => p.Id, StringComparer.Ordinal)
                .FirstOrDefault(g => g.Count() > 1);
            if (dupId is not null)
            {
                StatusText = $"❌ Provider Id 重复：{dupId.Key}（每个 provider 必须唯一）";
                return;
            }

            // ── 校验门 1：提交当前选中 provider 的 Extra* 文本；任何 provider 有非法 JSON → 整体阻止落盘 ──
            CommitExtraTexts(SelectedProvider);
            foreach (var p in Providers)
            {
                if (_pendingExtras.TryGetValue(p, out var pending))
                {
                    StatusText = $"❌ Provider '{p.Id}' 无法保存：{pending.Error}";
                    return;
                }
            }

            // ── 校验门 2：画像行全量构建（成本非法 / Provider Id 为空 / 键重复 → 如实报错）──
            var profiles = new List<ModelProfile>(ProfileEditors.Count);
            foreach (var item in ProfileEditors)
            {
                if (!item.TryBuildProfile(out var profile, out var profileError))
                {
                    StatusText = $"❌ 模型画像 '{item.ProviderId}::{item.ModelId}' 无法保存：{profileError}";
                    return;
                }

                profiles.Add(profile!);
            }

            var duplicate = profiles.GroupBy(p => p.Key).FirstOrDefault(g => g.Count() > 1);
            if (duplicate is not null)
            {
                StatusText = $"❌ 模型画像键重复：{duplicate.Key}（同一 Provider::模型 只能有一条画像）";
                return;
            }

            // ── 校验门 3：MOA 单轮预算文本（留空 = 不限制；非法/非正数 → 整体阻止落盘）──
            if (!TryParseMaxUsd(MaxUsdText, out var maxUsd, out var budgetError))
            {
                StatusText = $"❌ MOA 单轮预算非法：{budgetError}";
                return;
            }

            // ── UI + Provider 设置落盘 ──
            var s = _settings.Current;
            s.Ui.Theme = SelectedTheme;
            s.Ui.FontSize = Math.Clamp(FontSize, 10, 22);
            s.Ui.MemoryMaxChars = Math.Clamp(MemoryMaxChars, 200, 20000);
            s.Ui.UserProfileMaxChars = Math.Clamp(UserProfileMaxChars, 200, 10000);
            s.Ai.DefaultProviderId = DefaultProviderId;
            s.Ai.DefaultModel = string.IsNullOrWhiteSpace(DefaultModel) ? "deepseek-v4-flash" : DefaultModel;
            s.Ai.Providers.Clear();
            foreach (var p in Providers) s.Ai.Providers.Add(p);
            await _settings.SaveAsync();
            _theme.Apply(SelectedTheme);

            // ── 热重载：provider 缓存按新配置重建并触发 ProvidersChanged（ChatViewModel 刷新下拉）──
            _providerFactory.Reload(_settings.ToAiOptions());

            // ── 权限段：以"保存时刻的活跃策略"为基线合并 UI 编辑——
            // 设置页打开期间授权对话框新记住的决策不会被全量写擦除；
            // 列表内行 = 用户显式编辑，优先于基线。写回内存策略立即生效 + 落盘。
            var merged = _permission.ListRules()
                .ToDictionary(r => r.ToolName, r => r.DefaultDecision, StringComparer.Ordinal);
            foreach (var item in PermissionRules)
            {
                merged[item.ToolName] = item.Decision.Value;
                _permission.SetDefaultDecision(item.ToolName, item.Decision.Value);
            }
            await _permissionStore.SaveAsync(new PermissionSettings { ToolDecisions = merged });

            // ── 画像段：UI 行 Upsert（运行统计归运行时所有，保留窗口期间新记录的调用）；
            // 水合后被移除的行按基线合并删除——运行期间自学习新增的画像不受牵连。
            foreach (var profile in profiles)
            {
                var live = _profileCatalog.Find(profile.ProviderId, profile.ModelId);
                if (live is not null)
                {
                    profile.Stats = live.Stats;
                }

                _profileCatalog.Upsert(profile);
            }

            var currentKeys = profiles.Select(p => (p.ProviderId, p.ModelId)).ToHashSet();
            foreach (var key in _hydratedProfileKeys)
            {
                if (!currentKeys.Contains((key.ProviderId, key.ModelId)))
                {
                    _profileCatalog.Remove(key.ProviderId, key.ModelId);
                }
            }

            await _profileCatalog.SaveAsync();
            _hydratedProfileKeys = profiles.Select(p => (p.ProviderId, p.ModelId)).ToList();

            // ── MOA 段：就地写回单例（策略每轮直接读字段 → 下一轮即热生效），
            // 落盘 moa-options.json 后广播 OptionsChanged（ChatViewModel 刷新新会话默认策略）。
            _moaOptions.DefaultStrategy = DefaultStrategy;
            _moaOptions.Router = RouterChoice?.Binding;
            _moaOptions.Planner = PlannerChoice?.Binding;
            _moaOptions.Synthesizer = SynthesizerChoice?.Binding;
            _moaOptions.Judge = JudgeChoice?.Binding;
            _moaOptions.EnsembleSize = Math.Clamp(EnsembleSize, 2, 4);
            _moaOptions.MaxUsdPerTurn = maxUsd;
            _moaOptions.ToolsEnabled = ToolsEnabled;
            await _moaOptionsStore.SaveAsync(_moaOptions);
            _moaOptions.RaiseOptionsChanged();

            StatusText = $"✅ 已保存（Provider {Providers.Count} · 权限规则 {merged.Count} · 画像 {profiles.Count} · MOA {DefaultStrategy}）@ {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Save settings failed（内存策略可能已部分变更而磁盘未落盘，请重试保存）");
            StatusText = $"❌ 保存失败: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task ReloadAsync()
    {
        try
        {
            await _settings.LoadAsync();
            HydrateFromSettings();
            // 权限取当前生效策略（含授权对话框期间新记住的决策），而非仅磁盘快照。
            HydratePermissionRules();
            HydrateProfiles();
            HydrateMoaSection();
            StatusText = "🔄 已从磁盘重新加载";
        }
        catch (Exception ex)
        {
            StatusText = $"❌ 重载失败: {ex.Message}";
        }
    }

    [RelayCommand]
    public async Task HealthCheckAllAsync()
    {
        try
        {
            IsBusy = true;
            StatusText = "⏳ 正在 ping 所有 provider...";
            CommitExtraTexts(SelectedProvider);
            var healthy = 0;
            var total = 0;
            foreach (var p in Providers)
            {
                total++;
                try
                {
                    // 探针用编辑中的配置（含未保存修改），与单点测试同路径
                    var prov = _providerFactory.CreateProbe(p);
                    using var cts = new CancellationTokenSource(ProbeTimeout);
                    if (await prov.HealthCheckAsync(cts.Token)) healthy++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Health check failed for {Id}", p.Id);
                }
            }
            StatusText = $"🏥 Provider 健康: {healthy}/{total} 在线";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public void AddProfile()
    {
        var item = ProfileEditorItem.NewEmpty(SelectedProvider?.Id ?? DefaultProviderId);
        ProfileEditors.Add(item);
        SelectedProfile = item;
    }

    /// <summary>从编辑列表移除选中画像行；真实删除在 Save 时按水合基线合并落盘。</summary>
    [RelayCommand]
    public void RemoveSelectedProfile()
    {
        if (SelectedProfile is null) return;
        ProfileEditors.Remove(SelectedProfile);
        SelectedProfile = ProfileEditors.FirstOrDefault();
    }

    /// <summary>
    /// 解析单轮预算文本：空 = 不限制（null）；必须是正数（TurnBudget 对 0 直接抛异常，
    /// 负数/NaN/无法解析如实报错，绝不静默放行或估算）。
    /// </summary>
    internal static bool TryParseMaxUsd(string? text, out double? value, out string? error)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = null;
            return true;
        }

        if (double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            && !double.IsNaN(v) && !double.IsInfinity(v))
        {
            if (v <= 0)
            {
                error = "预算必须为正数（留空 = 不限制）";
                return false;
            }

            value = v;
            error = null;
            return true;
        }

        error = $"无法解析 '{text.Trim()}'（示例：0.5，单位美元/轮）";
        return false;
    }

    public sealed record ThemeChoice(string Id, string Display);
}
