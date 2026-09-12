// Copyright (c) AeroCode
// LocalModelsViewModel — 本地模型管理面板 VM（LOCAL_LLM_SPEC P3/P4）。
// 基于 OllamaClient：运行时检测（可达/版本）、已装模型列表、拉取（进度）、删除、更新检查。
// 诚实语义：Ollama 不可达/无模型如实显示，绝不伪造"已连接/已加载"；更新只做"检查+报告"，不静默升级。
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using AeroCode.AI.LocalModels;
using AeroCode.AI.Providers;
using AeroCode.App.Configuration;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AeroCode.App.ViewModels;

/// <summary>本地模型列表项（包装 OllamaModel，供面板渲染）。</summary>
public sealed class LocalModelItemViewModel
{
    public string Name { get; init; } = string.Empty;
    public string DisplaySize { get; init; } = string.Empty;
    public string Format { get; init; } = string.Empty;
    public string ParameterSize { get; init; } = string.Empty;
    public string Quantization { get; init; } = string.Empty;
    public string Family { get; init; } = string.Empty;

    /// <summary>单行摘要（列表展示）。</summary>
    public string Summary =>
        $"{Name} · {DisplaySize} · {ParameterSize} {Quantization}".Trim() +
        (string.IsNullOrEmpty(Format) ? string.Empty : $" · {Format}");

    public static LocalModelItemViewModel From(OllamaModel m) => new()
    {
        Name = m.Name,
        DisplaySize = m.DisplaySize,
        Format = m.Details?.Format ?? string.Empty,
        ParameterSize = m.Details?.ParameterSize ?? string.Empty,
        Quantization = m.Details?.QuantizationLevel ?? string.Empty,
        Family = m.Details?.Family ?? string.Empty,
    };
}

/// <summary>模型目录条目（精选常用模型，供一键拉取；名称为 Ollama 注册表名）。</summary>
public sealed record CatalogModelItem(string Name, string Size, string Description)
{
    /// <summary>单行摘要（目录列表展示）。</summary>
    public string Summary => $"{Name} · {Size} · {Description}";
}

/// <summary>
/// 本地模型管理 VM。构造注入 <see cref="OllamaClient"/>（DI 单例）；
/// 参数调节另注入 <see cref="SettingsService"/>/<see cref="ProviderFactory"/>（可空，便于单测仅传 client）。
/// </summary>
public sealed partial class LocalModelsViewModel : ObservableObject
{
    /// <summary>承载 Ollama 调参的 provider Id（与 SettingsService 默认种子一致）。</summary>
    public const string OllamaProviderId = "ollama";

    private readonly OllamaClient _client;
    private readonly SettingsService? _settings;
    private readonly ProviderFactory? _providerFactory;

    public LocalModelsViewModel(
        OllamaClient client,
        SettingsService? settings = null,
        ProviderFactory? providerFactory = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _settings = settings;
        _providerFactory = providerFactory;
        HydrateOptions();
    }

    /// <summary>Ollama 是否可达（服务在跑）。</summary>
    [ObservableProperty]
    private bool _isReachable;

    /// <summary>Ollama 版本（可达时）。</summary>
    [ObservableProperty]
    private string _version = string.Empty;

    /// <summary>状态/提示文本（如实）。</summary>
    [ObservableProperty]
    private string _statusText = "尚未检测";

    /// <summary>是否正在执行耗时操作（检测/拉取/删除）。</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>已安装模型列表。</summary>
    public ObservableCollection<LocalModelItemViewModel> Models { get; } = new();

    /// <summary>选中的模型（供删除）。</summary>
    [ObservableProperty]
    private LocalModelItemViewModel? _selectedModel;

    /// <summary>要拉取的模型名（如 "qwen2.5:1.5b"）。</summary>
    [ObservableProperty]
    private string _pullModelName = string.Empty;

    /// <summary>拉取进度文本。</summary>
    [ObservableProperty]
    private string _pullProgress = string.Empty;

    /// <summary>ollama provider 当前默认模型（聊天实际使用的模型；水合自 provider 配置）。</summary>
    [ObservableProperty]
    private string _currentDefaultModel = string.Empty;

    /// <summary>精选模型目录（Ollama 注册表常用模型，供一键拉取）。</summary>
    public IReadOnlyList<CatalogModelItem> Catalog { get; } = new[]
    {
        new CatalogModelItem("qwen2.5:0.5b", "~0.4GB", "Qwen2.5 0.5B，极轻量，CPU 快速"),
        new CatalogModelItem("qwen2.5:1.5b", "~0.9GB", "Qwen2.5 1.5B，轻量均衡"),
        new CatalogModelItem("qwen2.5:3b", "~1.8GB", "Qwen2.5 3B，更强"),
        new CatalogModelItem("qwen2.5:7b", "~4.4GB", "Qwen2.5 7B，强（需较大内存）"),
        new CatalogModelItem("llama3.2:1b", "~0.7GB", "Llama3.2 1B，轻量"),
        new CatalogModelItem("llama3.2:3b", "~1.9GB", "Llama3.2 3B"),
        new CatalogModelItem("gemma2:2b", "~1.5GB", "Gemma2 2B"),
        new CatalogModelItem("phi3:mini", "~2.2GB", "Phi-3 Mini 3.8B"),
        new CatalogModelItem("deepseek-r1:1.5b", "~0.9GB", "DeepSeek-R1-Distill 1.5B，推理型"),
        new CatalogModelItem("deepseek-r1:7b", "~4.3GB", "DeepSeek-R1-Distill 7B，推理型"),
    };

    /// <summary>目录中选中的模型（供一键拉取）。</summary>
    [ObservableProperty]
    private CatalogModelItem? _selectedCatalogModel;

    /// <summary>要导入的本地 .gguf 文件绝对路径。</summary>
    [ObservableProperty]
    private string _importGgufPath = string.Empty;

    /// <summary>导入模型的名称（如 my-model:latest）。</summary>
    [ObservableProperty]
    private string _importGgufName = string.Empty;

    // ---------------- Ollama 调参（LOCAL_LLM_SPEC §4，持久化到 ollama provider 的 ExtraBody） ----------------

    /// <summary>上下文长度 num_ctx（0 = 用模型默认）。</summary>
    [ObservableProperty]
    private int _numCtx = 4096;

    /// <summary>GPU 卸载层数 num_gpu（0 = 纯 CPU；本机集显收益有限）。</summary>
    [ObservableProperty]
    private int _numGpu = 0;

    /// <summary>CPU 线程数 num_thread（0 = Ollama 默认）。</summary>
    [ObservableProperty]
    private int _numThread = 0;

    /// <summary>采样温度 temperature。</summary>
    [ObservableProperty]
    private double _temperature = 0.7;

    /// <summary>核采样 top_p。</summary>
    [ObservableProperty]
    private double _topP = 0.9;

    /// <summary>top_k（0 = 默认）。</summary>
    [ObservableProperty]
    private int _topK = 40;

    /// <summary>重复惩罚 repeat_penalty。</summary>
    [ObservableProperty]
    private double _repeatPenalty = 1.1;

    /// <summary>单次最大生成 token 数 num_predict（-1 = 不限）。</summary>
    [ObservableProperty]
    private int _numPredict = 512;

    /// <summary>检测 Ollama 并刷新模型列表。</summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (IsBusy) return;
        await SetBusyAsync(true);
        try
        {
            var reachable = await _client.IsReachableAsync();
            await OnUiAsync(() => IsReachable = reachable);

            if (!reachable)
            {
                await OnUiAsync(() =>
                {
                    Version = string.Empty;
                    Models.Clear();
                    StatusText = $"Ollama 未运行或未安装（{_client.BaseUrl}）。请启动 Ollama 后重试。";
                });
                return;
            }

            var version = await _client.GetVersionAsync();
            var list = await _client.ListModelsAsync();

            await OnUiAsync(() =>
            {
                Version = version.IsSuccess ? version.Value!.Version : "(未知版本)";
                Models.Clear();
                if (list.IsSuccess && list.Value is not null)
                {
                    foreach (var m in list.Value)
                    {
                        Models.Add(LocalModelItemViewModel.From(m));
                    }

                    StatusText = Models.Count == 0
                        ? $"Ollama v{Version} 已连接，但尚未安装任何模型。可在下方拉取。"
                        : $"Ollama v{Version} 已连接，{Models.Count} 个已装模型。";
                }
                else
                {
                    StatusText = $"Ollama v{Version} 已连接，但列出模型失败：{list.Error}";
                }
            });
        }
        catch (Exception ex)
        {
            await OnUiAsync(() => StatusText = $"检测失败：{ex.Message}");
        }
        finally
        {
            await SetBusyAsync(false);
        }
    }

    /// <summary>拉取模型（流式进度）。支持 Ollama 注册表名与 hf.co/ HuggingFace 前缀。</summary>
    [RelayCommand]
    public async Task PullModelAsync()
    {
        var name = PullModelName?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(name))
        {
            await OnUiAsync(() => StatusText = "请输入要拉取的模型名（如 qwen2.5:1.5b，或 hf.co/user/repo:Q4_K_M）");
            return;
        }

        await PullByNameAsync(name);
    }

    /// <summary>拉取目录中选中的模型（一键）。</summary>
    [RelayCommand]
    public async Task PullCatalogModelAsync()
    {
        var sel = SelectedCatalogModel;
        if (sel is null || string.IsNullOrWhiteSpace(sel.Name))
        {
            await OnUiAsync(() => StatusText = "请先在模型目录中选择一个模型");
            return;
        }

        await PullByNameAsync(sel.Name);
    }

    /// <summary>导入本地 .gguf 文件（从网站如 HuggingFace 下载后载入），经 /api/create。</summary>
    [RelayCommand]
    public async Task ImportGgufAsync()
    {
        var path = ImportGgufPath?.Trim() ?? string.Empty;
        var name = ImportGgufName?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(path))
        {
            await OnUiAsync(() => StatusText = "请输入本地 .gguf 文件的绝对路径");
            return;
        }

        if (string.IsNullOrEmpty(name))
        {
            await OnUiAsync(() => StatusText = "请为导入的模型命名（如 my-model:latest）");
            return;
        }

        if (IsBusy) return;
        await SetBusyAsync(true);
        try
        {
            var (succeeded, lastError) = await RunProgressLoopAsync(
                _client.CreateModelAsync(name, path), "开始导入…");

            await OnUiAsync(() =>
            {
                PullProgress = succeeded ? "导入完成" : string.Empty;
                StatusText = succeeded
                    ? $"已导入本地模型 {name}。"
                    : $"导入 {name} 失败：{(string.IsNullOrEmpty(lastError) ? "未知原因" : lastError)}";
            });

            if (succeeded)
            {
                await RefreshCoreAsync();
            }
        }
        catch (Exception ex)
        {
            await OnUiAsync(() => StatusText = $"导入失败：{ex.Message}");
        }
        finally
        {
            await SetBusyAsync(false);
        }
    }

    /// <summary>按名称拉取（注册表名或 hf.co/ 前缀），复用进度循环。</summary>
    private async Task PullByNameAsync(string name)
    {
        if (IsBusy) return;
        await SetBusyAsync(true);
        try
        {
            var (succeeded, lastError) = await RunProgressLoopAsync(
                _client.PullModelAsync(name), "开始拉取…");

            await OnUiAsync(() =>
            {
                PullProgress = succeeded ? "拉取完成" : string.Empty;
                StatusText = succeeded
                    ? $"模型 {name} 拉取完成。"
                    : $"拉取 {name} 失败：{(string.IsNullOrEmpty(lastError) ? "未知原因" : lastError)}";
            });

            if (succeeded)
            {
                await RefreshCoreAsync();
            }
        }
        catch (Exception ex)
        {
            await OnUiAsync(() => StatusText = $"拉取失败：{ex.Message}");
        }
        finally
        {
            await SetBusyAsync(false);
        }
    }

    /// <summary>共享进度循环：逐条消费进度并更新 PullProgress，返回（是否成功，最后错误）。</summary>
    private async Task<(bool Succeeded, string LastError)> RunProgressLoopAsync(
        IAsyncEnumerable<OllamaPullProgress> source, string startMsg)
    {
        await OnUiAsync(() => PullProgress = startMsg);
        var lastError = string.Empty;
        var succeeded = false;

        await foreach (var progress in source)
        {
            if (progress.IsDone)
            {
                succeeded = true;
            }

            var text = progress.Fraction is { } f
                ? $"{progress.Status} {f:P0}"
                : progress.Status;
            if (progress.Status.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
                progress.Status.Contains("unreachable", StringComparison.OrdinalIgnoreCase) ||
                progress.Status.Contains("timed out", StringComparison.OrdinalIgnoreCase))
            {
                lastError = progress.Status;
            }

            await OnUiAsync(() => PullProgress = text);
        }

        return (succeeded, lastError);
    }

    /// <summary>删除选中的模型。</summary>
    [RelayCommand]
    public async Task DeleteSelectedModelAsync()
    {
        var target = SelectedModel;
        if (target is null)
        {
            await OnUiAsync(() => StatusText = "请先在列表中选择一个模型");
            return;
        }

        if (IsBusy) return;
        await SetBusyAsync(true);
        try
        {
            var result = await _client.DeleteModelAsync(target.Name);
            await OnUiAsync(() => StatusText = result.IsSuccess
                ? $"已删除模型 {target.Name}。"
                : $"删除 {target.Name} 失败：{result.Error}");

            if (result.IsSuccess)
            {
                await RefreshCoreAsync();
            }
        }
        catch (Exception ex)
        {
            await OnUiAsync(() => StatusText = $"删除失败：{ex.Message}");
        }
        finally
        {
            await SetBusyAsync(false);
        }
    }

    /// <summary>
    /// 检查更新（诚实：仅报告当前版本与更新方式，不做静默自动升级——升级运行时风险高，须用户手动）。
    /// </summary>
    [RelayCommand]
    public async Task CheckForUpdatesAsync()
    {
        if (IsBusy) return;
        await SetBusyAsync(true);
        try
        {
            var version = await _client.GetVersionAsync();
            await OnUiAsync(() => StatusText = version.IsSuccess
                ? $"当前 Ollama v{version.Value!.Version}。更新请到 ollama.com 下载最新安装包手动升级（本应用不做静默自动升级）。"
                : $"无法获取版本（Ollama 不可达）：{version.Error}");
        }
        catch (Exception ex)
        {
            await OnUiAsync(() => StatusText = $"检查更新失败：{ex.Message}");
        }
        finally
        {
            await SetBusyAsync(false);
        }
    }

    // ---------------- Ollama 调参：水合 / 应用 ----------------

    /// <summary>从 ollama provider 水合：默认模型 + ExtraBody 调参项（构造时 + 检测时）。</summary>
    public void HydrateOptions()
    {
        var ollama = _settings?.Current.Ai.Providers.FirstOrDefault(p =>
            string.Equals(p.Id, OllamaProviderId, StringComparison.OrdinalIgnoreCase));
        CurrentDefaultModel = ollama?.DefaultModel ?? string.Empty;

        var extra = ollama?.ExtraBody;
        if (extra is null)
        {
            return; // 无 ollama provider / 无 ExtraBody：保持默认值。
        }

        NumCtx = ReadInt(extra, "num_ctx", NumCtx);
        NumGpu = ReadInt(extra, "num_gpu", NumGpu);
        NumThread = ReadInt(extra, "num_thread", NumThread);
        Temperature = ReadDouble(extra, "temperature", Temperature);
        TopP = ReadDouble(extra, "top_p", TopP);
        TopK = ReadInt(extra, "top_k", TopK);
        RepeatPenalty = ReadDouble(extra, "repeat_penalty", RepeatPenalty);
        NumPredict = ReadInt(extra, "num_predict", NumPredict);
    }

    /// <summary>
    /// 应用调参：把当前调参写入 ollama provider 的 ExtraBody，落盘并热重载 ProviderFactory
    /// （清空缓存 + ProvidersChanged），使运行中的 provider 无需重启即按新参数请求。
    /// </summary>
    [RelayCommand]
    public async Task ApplyOptionsAsync()
    {
        if (_settings is null)
        {
            await OnUiAsync(() => StatusText = "设置服务不可用，无法保存调参");
            return;
        }

        try
        {
            var providers = _settings.Current.Ai.Providers;
            var ollama = providers.FirstOrDefault(p =>
                string.Equals(p.Id, OllamaProviderId, StringComparison.OrdinalIgnoreCase));
            if (ollama is null)
            {
                await OnUiAsync(() => StatusText = "未找到 ollama provider 配置，无法保存调参");
                return;
            }

            ollama.ExtraBody = BuildExtraBody();
            await _settings.SaveAsync();

            // 热重载：provider 缓存按新配置重建（含新 ExtraBody），无需重启。
            _providerFactory?.Reload(_settings.ToAiOptions());

            await OnUiAsync(() => StatusText = "调参已保存并生效（ollama provider 已热重载）。");
        }
        catch (Exception ex)
        {
            await OnUiAsync(() => StatusText = $"保存调参失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 把选中的已装模型设为 ollama provider 的默认模型（聊天实际使用的模型），
    /// 落盘并热重载，无需重启。闭合"种子默认模型（如 qwen2.5:7b）未必已安装"的缺口。
    /// </summary>
    [RelayCommand]
    public async Task SetAsDefaultModelAsync()
    {
        var target = SelectedModel;
        if (target is null || string.IsNullOrWhiteSpace(target.Name))
        {
            await OnUiAsync(() => StatusText = "请先在列表中选择一个已装模型");
            return;
        }

        if (_settings is null)
        {
            await OnUiAsync(() => StatusText = "设置服务不可用，无法设置默认模型");
            return;
        }

        try
        {
            var ollama = _settings.Current.Ai.Providers.FirstOrDefault(p =>
                string.Equals(p.Id, OllamaProviderId, StringComparison.OrdinalIgnoreCase));
            if (ollama is null)
            {
                await OnUiAsync(() => StatusText = "未找到 ollama provider 配置，无法设置默认模型");
                return;
            }

            ollama.DefaultModel = target.Name;
            await _settings.SaveAsync();
            _providerFactory?.Reload(_settings.ToAiOptions());

            await OnUiAsync(() =>
            {
                CurrentDefaultModel = target.Name;
                StatusText = $"已将 {target.Name} 设为聊天默认本地模型（热重载生效）。";
            });
        }
        catch (Exception ex)
        {
            await OnUiAsync(() => StatusText = $"设置默认模型失败：{ex.Message}");
        }
    }

    /// <summary>把当前调参构建为 ExtraBody 字典（Ollama 选项键）。</summary>
    public Dictionary<string, object> BuildExtraBody() => new()
    {
        ["num_ctx"] = NumCtx,
        ["num_gpu"] = NumGpu,
        ["num_thread"] = NumThread,
        ["temperature"] = Temperature,
        ["top_p"] = TopP,
        ["top_k"] = TopK,
        ["repeat_penalty"] = RepeatPenalty,
        ["num_predict"] = NumPredict,
    };

    private Dictionary<string, object>? GetOllamaExtraBody()
    {
        var ollama = _settings?.Current.Ai.Providers.FirstOrDefault(p =>
            string.Equals(p.Id, OllamaProviderId, StringComparison.OrdinalIgnoreCase));
        return ollama?.ExtraBody;
    }

    private static int ReadInt(Dictionary<string, object> extra, string key, int fallback)
    {
        if (!extra.TryGetValue(key, out var value) || value is null) return fallback;
        return value switch
        {
            int i => i,
            long l => (int)l,
            double d => (int)d,
            System.Text.Json.JsonElement je when je.ValueKind == System.Text.Json.JsonValueKind.Number => je.GetInt32(),
            _ => fallback,
        };
    }

    private static double ReadDouble(Dictionary<string, object> extra, string key, double fallback)
    {
        if (!extra.TryGetValue(key, out var value) || value is null) return fallback;
        return value switch
        {
            double d => d,
            int i => i,
            long l => l,
            float f => f,
            System.Text.Json.JsonElement je when je.ValueKind == System.Text.Json.JsonValueKind.Number => je.GetDouble(),
            _ => fallback,
        };
    }

    // ---------------- 内部 ----------------

    /// <summary>刷新列表核心（不改动 IsBusy，供拉取/删除后复用）。</summary>
    private async Task RefreshCoreAsync()
    {
        var list = await _client.ListModelsAsync();
        await OnUiAsync(() =>
        {
            Models.Clear();
            if (list.IsSuccess && list.Value is not null)
            {
                foreach (var m in list.Value)
                {
                    Models.Add(LocalModelItemViewModel.From(m));
                }
            }
        });
    }

    private Task SetBusyAsync(bool value) => OnUiAsync(() => IsBusy = value);

    /// <summary>把属性/集合变更封送回 UI 线程（OllamaClient 用 ConfigureAwait(false)，续接可能在后台线程）。</summary>
    private static Task OnUiAsync(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return Dispatcher.UIThread.InvokeAsync(action).GetTask();
    }
}
