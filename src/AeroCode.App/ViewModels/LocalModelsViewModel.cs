// Copyright (c) AeroCode
// LocalModelsViewModel — 本地模型管理面板 VM（LOCAL_LLM_SPEC P3/P4）。
// 基于 OllamaClient：运行时检测（可达/版本）、已装模型列表、拉取（进度）、删除、更新检查。
// 诚实语义：Ollama 不可达/无模型如实显示，绝不伪造"已连接/已加载"；更新只做"检查+报告"，不静默升级。
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using AeroCode.AI.LocalModels;
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

/// <summary>
/// 本地模型管理 VM。构造注入 <see cref="OllamaClient"/>（DI 单例）。
/// </summary>
public sealed partial class LocalModelsViewModel : ObservableObject
{
    private readonly OllamaClient _client;

    public LocalModelsViewModel(OllamaClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
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

    /// <summary>拉取模型（流式进度）。</summary>
    [RelayCommand]
    public async Task PullModelAsync()
    {
        var name = PullModelName?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(name))
        {
            await OnUiAsync(() => StatusText = "请输入要拉取的模型名（如 qwen2.5:1.5b）");
            return;
        }

        if (IsBusy) return;
        await SetBusyAsync(true);
        try
        {
            await OnUiAsync(() => PullProgress = "开始拉取…");
            var lastError = string.Empty;
            var succeeded = false;

            await foreach (var progress in _client.PullModelAsync(name))
            {
                if (progress.IsDone)
                {
                    succeeded = true;
                }

                var text = progress.Fraction is { } f
                    ? $"{progress.Status} {f:P0}"
                    : progress.Status;
                if (progress.Status.StartsWith("pull failed", StringComparison.OrdinalIgnoreCase) ||
                    progress.Status.StartsWith("pull unreachable", StringComparison.OrdinalIgnoreCase) ||
                    progress.Status.StartsWith("pull timed out", StringComparison.OrdinalIgnoreCase))
                {
                    lastError = progress.Status;
                }

                await OnUiAsync(() => PullProgress = text);
            }

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
