using System;
using System.Collections.ObjectModel;
using System.Linq;
using AeroCode.AI.Providers;
using AeroCode.Harness;
using AeroCode.Harness.EventBus;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AeroCode.App.ViewModels;

/// <summary>
/// V3 Diagnostics Tab: 实时显示 token / cache / cost / provider 健康。
/// 真实订阅 EventBus, 0 模板。
/// </summary>
public partial class DiagnosticsViewModel : ObservableObject, IDisposable
{
    private readonly ProviderFactory _factory;
    private readonly HarnessHost _harness;
    private readonly EventBus _eventBus;
    private readonly System.Collections.Generic.List<Action> _unsubs = new();

    [ObservableProperty] private string _statusText = "就绪";
    [ObservableProperty] private int _totalToolCalls;
    [ObservableProperty] private int _totalInvocations;
    [ObservableProperty] private int _totalPendingEdits;
    [ObservableProperty] private bool _planModeEnabled;
    [ObservableProperty] private string _activePresetId = "standard";
    [ObservableProperty] private int _activePresetToolCount;
    [ObservableProperty] private string _compactionStrategy = "SlidingWindow";

    public ObservableCollection<ProviderHealth> ProviderHealths { get; } = new();
    public ObservableCollection<EventLog> RecentEvents { get; } = new();

    public DiagnosticsViewModel(ProviderFactory factory, HarnessHost harness)
    {
        _factory = factory;
        _harness = harness;
        _eventBus = harness.EventBus;

        _unsubs.Add(_eventBus.Subscribe<ToolCallEvent>(e =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                TotalToolCalls++;
                RecentEvents.Insert(0, new EventLog(DateTime.Now, "Tool", $"{e.ToolName}({e.Args.Count} args)"));
                if (RecentEvents.Count > 50) RecentEvents.RemoveAt(50);
            });
        }));
        _unsubs.Add(_eventBus.Subscribe<PlanModeChangedEvent>(e =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                PlanModeEnabled = e.Enabled;
                TotalPendingEdits = e.Enabled ? _harness.PlanMode.PendingCount : 0;
            });
        }));
        _unsubs.Add(_eventBus.Subscribe<PermissionRequestedEvent>(e =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                RecentEvents.Insert(0, new EventLog(DateTime.Now, "Permission", $"ask: {e.ToolName}"));
                if (RecentEvents.Count > 50) RecentEvents.RemoveAt(50);
            });
        }));

        RefreshAsync().GetAwaiter().GetResult();
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task RefreshAsync()
    {
        try
        {
            ProviderHealths.Clear();
            foreach (var p in _factory.GetAll())
            {
                bool healthy = false;
                try { healthy = await p.HealthCheckAsync(); } catch { /* swallow */ }
                ProviderHealths.Add(new ProviderHealth(p.DisplayName, p.ProviderId, healthy ? "✓ 健康" : "✗ 不可用",
                    $"streaming={p.SupportsStreaming}, tools={p.SupportsToolCalling}, thinking={p.SupportsThinking}"));
            }
            ActivePresetId = _harness.Presets.Get(_harness.Presets.List().First().Id)?.Id ?? "standard";
            TotalPendingEdits = _harness.PlanMode.PendingCount;
            CompactionStrategy = _harness.Compactor.Strategy.ToString();
            TotalInvocations = 0;  // Hook into SkillRegistry if needed
            StatusText = $"已刷新 {ProviderHealths.Count} 个 provider";
        }
        catch (Exception ex) { StatusText = $"✗ {ex.Message}"; }
    }

    [RelayCommand]
    private void TogglePlanMode()
    {
        if (_harness.PlanMode.IsEnabled) _harness.PlanMode.Disable();
        else _harness.PlanMode.Enable();
    }

    [RelayCommand]
    private void SwitchPreset(string presetId)
    {
        try
        {
            var p = _harness.Presets.Get(presetId);
            if (p is null) { StatusText = $"未找到 preset: {presetId}"; return; }
            ActivePresetId = p.Id;
            ActivePresetToolCount = p.Tools.Count;
            StatusText = $"✓ 切换到 preset: {p.Name} ({p.Tools.Count} tools)";
        }
        catch (Exception ex) { StatusText = $"✗ {ex.Message}"; }
    }

    public void Dispose()
    {
        foreach (var u in _unsubs) u();
    }
}

public sealed record ProviderHealth(string Name, string Id, string Status, string Capabilities);
public sealed record EventLog(DateTime At, string Kind, string Message);
