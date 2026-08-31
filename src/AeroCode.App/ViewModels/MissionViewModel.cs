// Copyright (c) AeroCode
// MissionViewModel — Autonomy Mission 面板（批次 B G2-1 + G5，builder-δ）。
// 直连 MissionController 公开 API（RunAsync 返回终态 MissionRecord，轨迹在 TransitionsJson）：
// 内核零改造——面板只消费其真实产物。轨迹投影在运行结束后按真实 JSON 渲染
// （控制器无逐阶段事件面，运行中如实显示"执行中"，不伪造实时阶段流）。
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AeroAgent.Autonomy.Data;
using AeroAgent.Autonomy.Mission;
using AeroAgent.Moa.Tools;
using AeroCode.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AeroCode.App.ViewModels;

/// <summary>一条状态轨迹的 UI 投影。</summary>
public sealed record MissionTransitionItem(string From, string To, string AtLocal, string Artifact);

/// <summary>
/// Mission 面板 VM：目标输入 → 真实 MissionController 全状态机（分析→澄清→钢人→规划→执行→
/// 校验→复盘→经验沉淀）→ 终态与完整轨迹展示。澄清问题经真实弹窗端口应答。
/// </summary>
public partial class MissionViewModel : ObservableObject
{
    private static readonly JsonSerializerOptions TransitionJsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly MissionController _controller;
    private readonly PresenterClarificationResponder? _clarificationResponder;
    private CancellationTokenSource? _missionCts;

    [ObservableProperty]
    private string _goalInput = string.Empty;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string _statusText = "就绪。输入目标后启动一次完整任务状态机。";

    /// <summary>终局徽章（Pending/成功/失败/取消 + 简要原因）。</summary>
    [ObservableProperty]
    private string _outcomeBadge = string.Empty;

    /// <summary>终态记录的执行摘要（会话/消息数/成本；来自真实 outcome）。</summary>
    [ObservableProperty]
    private string _executionSummary = string.Empty;

    public ObservableCollection<MissionTransitionItem> Transitions { get; } = new();

    public MissionViewModel(
        MissionController controller,
        IClarificationPresenter? clarificationPresenter = null)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _clarificationResponder = clarificationPresenter is null
            ? null
            : new PresenterClarificationResponder(clarificationPresenter);
    }

    /// <summary>启动一次任务。运行中重入被拒（同一控制器串行语义）。</summary>
    [RelayCommand]
    private async Task StartMissionAsync()
    {
        var goal = GoalInput.Trim();
        if (goal.Length == 0)
        {
            StatusText = "目标不能为空";
            return;
        }

        if (IsRunning)
        {
            return;
        }

        IsRunning = true;
        OutcomeBadge = string.Empty;
        ExecutionSummary = string.Empty;
        Transitions.Clear();
        StatusText = "任务运行中…（分析→澄清→钢人→规划→执行→校验→复盘）";
        _missionCts = new CancellationTokenSource();
        try
        {
            var record = await _controller.RunAsync(
                goal,
                new MissionRunOptions { ClarificationResponder = _clarificationResponder },
                _missionCts.Token);
            ProjectRecord(record);
        }
        catch (OperationCanceledException)
        {
            StatusText = "任务已取消";
        }
        catch (Exception ex)
        {
            StatusText = $"任务失败：{ex.Message}";
        }
        finally
        {
            IsRunning = false;
            _missionCts.Dispose();
            _missionCts = null;
        }
    }

    /// <summary>
    /// 终止当前运行中的任务（G5 终止按钮）：经真实 CancellationToken 贯穿
    /// MissionController 全状态机（控制器把取消转为 Cancelled 终态落库，不伪造终局）。
    /// 无运行中任务时如实提示，不静默。
    /// </summary>
    [RelayCommand]
    private void StopMission()
    {
        if (!IsRunning)
        {
            StatusText = "没有正在运行的任务";
            return;
        }

        StatusText = "正在终止任务…";
        _missionCts?.Cancel();
    }

    /// <summary>把终态记录投影为轨迹列表与徽章（全部来自 record 真实字段）。</summary>
    public void ProjectRecord(MissionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        foreach (var t in DeserializeTransitions(record.TransitionsJson))
        {
            Transitions.Add(new MissionTransitionItem(
                t.From.ToString(),
                t.To.ToString(),
                t.AtUtc.ToLocalTime().ToString("MM-dd HH:mm:ss"),
                t.Artifact ?? string.Empty));
        }

        OutcomeBadge = record.Outcome switch
        {
            MissionOutcome.Succeeded => $"✅ 成功（{record.State}）",
            MissionOutcome.Failed => $"❌ 失败（{record.Error ?? record.State.ToString()}）",
            MissionOutcome.Cancelled => "⏹ 已取消",
            _ => $"状态 {record.State}",
        };
        ExecutionSummary = string.IsNullOrWhiteSpace(record.ExecutionJson)
            ? string.Empty
            : TrySummarizeExecution(record.ExecutionJson);
        StatusText = record.Outcome == MissionOutcome.Succeeded
            ? "任务完成"
            : $"任务结束（{record.Outcome}）";
    }

    private static string TrySummarizeExecution(string executionJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(executionJson);
            var root = doc.RootElement;
            var session = root.TryGetProperty("SessionId", out var s) && s.ValueKind == JsonValueKind.String
                ? s.GetString()
                : null;
            var messages = root.TryGetProperty("AssistantMessages", out var m) && m.ValueKind == JsonValueKind.Number
                ? m.GetInt32()
                : 0;
            var cost = root.TryGetProperty("TotalCostUsd", out var c) && c.ValueKind == JsonValueKind.Number
                ? c.GetDouble()
                : 0;
            return $"执行会话 {Truncate(session, 12) ?? "-"} · 助手消息 {messages} 条 · 成本 ${cost:F4}";
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    private static List<MissionTransition> DeserializeTransitions(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new List<MissionTransition>();
        }

        try
        {
            return JsonSerializer.Deserialize<List<MissionTransition>>(json, TransitionJsonOpts)
                   ?? new List<MissionTransition>();
        }
        catch (JsonException)
        {
            // 轨迹 JSON 损坏如实呈现为空列表（终态徽章/错误文本仍然可见），不伪造轨迹。
            return new List<MissionTransition>();
        }
    }

    private static string? Truncate(string? s, int max)
        => string.IsNullOrEmpty(s) ? s : (s.Length <= max ? s : s[..max]);
}
