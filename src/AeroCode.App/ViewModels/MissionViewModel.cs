// Copyright (c) AeroCode
// MissionViewModel — Autonomy Mission 面板（批次 B G2-1 + G5，builder-δ；F-M5 审批/恢复 UI，R3 β）。
// 直连 MissionController 公开 API（RunAsync 返回终态 MissionRecord，轨迹在 TransitionsJson）：
// 内核零改造——面板只消费其真实产物。轨迹投影在运行结束后按真实 JSON 渲染。
// R4 β：订阅 MissionController.MissionEventRaised 实时事件流（状态机转换/步骤进展/升级产生）
//   → StatusText 运行中实时呈现（View 已绑定；事件 Detail 凭据 id 已在控制器侧截断）。
// F-M5 新增（默认=现行为铁律：不改变任何既有面板行为，新 UI 只在有待审批项/用户点击时出现）：
//   审批卡：订阅 EscalationReceived（事件 → Dispatcher.UIThread）→ OverlayService 弹卡
//           （reason 过敏感脱敏 + 凭据恒为掩码）→ 批准 = TryApproveEscalation 一次性消费；
//   恢复入口：「恢复上次检查点」→ ResumeLatestCheckpointAsync；无候选 checkpoint 时
//           按钮禁用（探针与控制器恢复同源：ResumePlanner fail-closed 判定）；执行中防重入。
// R4 β LOW 修复：
//   F-LOW-1 拒绝记忆——用户显式拒绝过的升级凭据按本 VM 生命周期记忆，补弹种子不再重新入卡
//           （凭据在控制器侧仍未消费、不丢失，批准 API 仍可达；只是不再重复打扰）；
//   F-LOW-2 卡片去重——同一凭据的覆盖层呈现中（ShowAsync 未返回）不再重复弹第二张。
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AeroAgent.Autonomy.Data;
using AeroAgent.Autonomy.Mission;
using AeroAgent.Moa.LoopGuard;
using AeroAgent.Moa.Tools;
using AeroAgent.Moa.Tools.Workspace;
using AeroCode.App.Services;
using AeroCode.App.Views;
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
    private readonly OverlayService? _overlay;
    private readonly CheckpointStore? _checkpoints;
    private readonly Action<Action> _marshalToUi;
    private readonly Func<long?, CancellationToken, Task<MissionResumeResult>> _resumeInvoker;
    private CancellationTokenSource? _missionCts;

    // F-LOW-1：拒绝记忆（本 VM 生命周期内，被用户显式拒绝的凭据 id 不再重新入卡）。
    private readonly HashSet<string> _rejectedEscalationIds = new(StringComparer.Ordinal);

    // F-LOW-2：呈现中（ShowAsync 未返回）的卡片 id——同一凭据不重复弹第二张覆盖层。
    private readonly HashSet<string> _inFlightOverlayIds = new(StringComparer.Ordinal);

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

    /// <summary>恢复命令执行中（防重入：执行中按钮禁用 + 二次点击如实拒绝）。</summary>
    [ObservableProperty]
    private bool _isResuming;

    /// <summary>是否存在可恢复的最近检查点（探针与控制器恢复同源；null store = 可用性未知，点击后由控制器如实判定）。</summary>
    [ObservableProperty]
    private bool _canResumeCheckpoint;

    /// <summary>恢复按钮的工具提示（探测结论/禁用原因，如实说明）。</summary>
    [ObservableProperty]
    private string _resumeHint = "恢复可用性未探测";

    /// <summary>当前是否有待审批卡片（决策后移除）。</summary>
    [ObservableProperty]
    private bool _hasPendingApprovals;

    /// <summary>待审批升级卡片（每项经 OverlayService 呈现；宿主未挂载时留队列，后续触发补弹）。</summary>
    public ObservableCollection<MissionEscalationCardViewModel> PendingApprovalCards { get; } = new();

    public ObservableCollection<MissionTransitionItem> Transitions { get; } = new();

    /// <summary>复制执行摘要 + 状态轨迹到剪贴板。</summary>
    [RelayCommand]
    private async Task CopyTrajectoryAsync()
    {
        if (Transitions.Count == 0 && string.IsNullOrWhiteSpace(ExecutionSummary))
        {
            StatusText = "当前无任务轨迹可复制";
            return;
        }
        try
        {
            var clipboard = Avalonia.Application.Current?.ApplicationLifetime
                is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime d
                ? d.MainWindow?.Clipboard : null;
            if (clipboard is null) { StatusText = "✗ 剪贴板不可用"; return; }
            var sb = new System.Text.StringBuilder();
            if (!string.IsNullOrWhiteSpace(ExecutionSummary))
            {
                sb.AppendLine(ExecutionSummary).AppendLine();
            }
            foreach (var t in Transitions)
            {
                sb.Append(t.From).Append(" → ").Append(t.To);
                if (!string.IsNullOrWhiteSpace(t.Artifact)) sb.Append("  [").Append(t.Artifact).Append(']');
                sb.Append("  (").Append(t.AtLocal).AppendLine(")");
            }
            await clipboard.SetTextAsync(sb.ToString().TrimEnd());
            StatusText = "✓ 已复制任务轨迹";
        }
        catch (Exception ex) { StatusText = $"✗ 复制失败：{ex.Message}"; }
    }

    /// <summary>
    /// <paramref name="overlayService"/>：审批卡片承载（null = 卡片留队列不弹，诚实降级）；
    /// <paramref name="checkpointStore"/>：恢复可用性探针数据源（DI 未注册 = null，可用性未知）；
    /// <paramref name="uiMarshaller"/>：升级事件 → UI 线程（默认 Avalonia Dispatcher）；
    /// <paramref name="resumeInvoker"/>：恢复调用点（默认直连 controller.ResumeLatestCheckpointAsync；
    /// 测试注入闸门用，产品路径零改动）。全部可选参数——DI 未注册时按默认值解析，组合根零改动。
    /// </summary>
    public MissionViewModel(
        MissionController controller,
        IClarificationPresenter? clarificationPresenter = null,
        OverlayService? overlayService = null,
        CheckpointStore? checkpointStore = null,
        Action<Action>? uiMarshaller = null,
        Func<long?, CancellationToken, Task<MissionResumeResult>>? resumeInvoker = null)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _clarificationResponder = clarificationPresenter is null
            ? null
            : new PresenterClarificationResponder(clarificationPresenter);
        _overlay = overlayService;
        _checkpoints = checkpointStore;
        _marshalToUi = uiMarshaller ?? (action => Avalonia.Threading.Dispatcher.UIThread.Post(action));
        _resumeInvoker = resumeInvoker ?? ((seq, ct) => _controller.ResumeLatestCheckpointAsync(seq, ct));

        // F-M5：升级事件可能来自工具循环任意线程 → 统一经 UI 线程回展示逻辑；
        // 订阅方异常不阻断受理链路（控制器侧已兜底，这里同样自容）。
        _controller.EscalationReceived += OnEscalationReceived;

        // R4 β：实时事件流（状态机转换/步骤进展/升级产生）→ StatusText 实时呈现。
        // 事件 Detail 由控制器保证展示安全（凭据 id 截断、理由在卡片侧另行脱敏）。
        _controller.MissionEventRaised += OnMissionEvent;
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IsRunning) || e.PropertyName == nameof(IsResuming))
            {
                ResumeLastCheckpointCommand.NotifyCanExecuteChanged();
            }
        };

        // 种子化既有待审批快照（控制器可能先于 UI 受理过升级）+ 首次恢复可用性探测。
        foreach (var pending in _controller.PendingEscalations)
        {
            AcceptEscalation(pending);
        }

        RefreshResumeAvailability();
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
            // 运行期间可能写入 checkpoint / 受理升级：结束后刷新恢复可用性并补弹未呈现的审批卡。
            RefreshResumeAvailability();
            PresentPendingCards();
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
        PresentPendingCards();
        RefreshResumeAvailability();
    }

    // ============ F-M5：升级审批卡片（PendingEscalations → 卡片 → TryApproveEscalation 全链 UI 可达）============

    /// <summary>升级事件入口：工具循环任意线程触发，统一经 UI 线程回展示逻辑（自容，不阻断受理链路）。</summary>
    private void OnEscalationReceived(EscalationRequest request)
    {
        _marshalToUi(() =>
        {
            try
            {
                AcceptEscalation(request);
                PresentPendingCards();
                RefreshResumeAvailability();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[DEGRADED] 审批卡片呈现失败（升级 {request.Id} 已在控制器待审批队列，不丢失）: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// 实时事件流入口（R4 β）：状态机转换/步骤进展/升级产生 → StatusText 实时呈现。
    /// 事件 Detail 由控制器保证展示安全（凭据 id 截断）；终态文案仍由 ProjectRecord 收口，
    /// 运行结束后 ProjectRecord 的 StatusText 覆盖最后一条实时事件（语义不变）。
    /// </summary>
    private void OnMissionEvent(object? sender, MissionEvent evt)
    {
        _marshalToUi(() =>
        {
            try
            {
                StatusText = evt.State is { } state
                    ? $"[{state}] {evt.Detail}"
                    : evt.Detail;
            }
            catch
            {
                // 呈现失败不阻断 mission（控制器侧已逐订阅者兜底，这里同样自容）。
            }
        });
    }

    /// <summary>
    /// 按 Id 去重入队一张卡片（投影全程经 MissionEscalationItem：理由脱敏、凭据恒为掩码）。
    /// F-LOW-1：被用户显式拒绝过的凭据不再重新入卡（拒绝记忆；凭据在控制器侧仍未消费、不丢失）。
    /// </summary>
    private void AcceptEscalation(EscalationRequest request)
    {
        if (_rejectedEscalationIds.Contains(request.Id))
        {
            return;
        }

        if (PendingApprovalCards.Any(c => c.Id == request.Id))
        {
            return;
        }

        PendingApprovalCards.Add(new MissionEscalationCardViewModel(
            MissionEscalationItem.From(request),
            ApproveEscalation,
            RejectEscalation));
        HasPendingApprovals = PendingApprovalCards.Count > 0;
    }

    /// <summary>
    /// 呈现所有未决策卡片：先以控制器真实待审批队列重新种子（被拒绝的凭据由 F-LOW-1
    /// 拒绝记忆挡在入卡之外），再逐张经 OverlayService 弹出（呈现中的卡片不重复弹，F-LOW-2）。
    /// </summary>
    private void PresentPendingCards()
    {
        foreach (var pending in _controller.PendingEscalations)
        {
            AcceptEscalation(pending);
        }

        foreach (var card in PendingApprovalCards)
        {
            TryPresentCard(card);
        }
    }

    /// <summary>
    /// 宿主已挂载时弹卡；未挂载（MainView 未 Loaded / 宿主缺失）→ 卡片留队列，后续触发补弹。
    /// F-LOW-2：同一凭据已有覆盖层在呈现中（ShowAsync 未返回）→ 不重复弹第二张。
    /// </summary>
    private void TryPresentCard(MissionEscalationCardViewModel card)
    {
        if (_overlay is null || !_overlay.HasHost)
        {
            return;
        }

        if (!_inFlightOverlayIds.Add(card.Id))
        {
            return;
        }

        var overlay = _overlay;
        var border = MissionApprovalCards.Build(card, b => overlay.CloseOverlay(b));
        _ = PresentCardAsync(card.Id, border);
    }

    private async Task PresentCardAsync(string cardId, Avalonia.Controls.Border border)
    {
        try
        {
            await _overlay!.ShowAsync(border);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[DEGRADED] 审批卡片呈现失败: {ex.Message}");
        }
        finally
        {
            _inFlightOverlayIds.Remove(cardId);
        }

        // ShowAsync 返回 = 覆盖层已移除（含返回键 TryCloseTop）：未决策的卡片仍留队列，
        // 由下一次升级事件/任务结束触发补弹（不静默丢失）。
    }

    /// <summary>批准回调：真实消费一次性凭据（controller.TryApproveEscalation），结果如实可见。</summary>
    private bool ApproveEscalation(string escalationId)
    {
        var approved = _controller.TryApproveEscalation(escalationId);
        var card = PendingApprovalCards.FirstOrDefault(c => c.Id == escalationId);
        if (card is not null)
        {
            PendingApprovalCards.Remove(card);
        }

        HasPendingApprovals = PendingApprovalCards.Count > 0;
        StatusText = approved
            ? $"✅ 升级凭据 {ShortId(escalationId)} 已批准（一次性消费）。可用「恢复上次检查点」回到最近留存状态。"
            : $"⚠️ 升级凭据 {ShortId(escalationId)} 审批无效（不存在或已被消费，一次性语义不重放）。";
        return approved;
    }

    /// <summary>
    /// 拒绝回调：不消费凭据（凭据仍在控制器待审批队列），卡片移出展示队列。
    /// F-LOW-1：凭据 id 入拒绝记忆——后续补弹种子不再重新入卡（不重复打扰；
    /// 凭据未消费、不丢失，controller.TryApproveEscalation 仍可达）。
    /// </summary>
    private void RejectEscalation(MissionEscalationCardViewModel card)
    {
        _rejectedEscalationIds.Add(card.Id);
        PendingApprovalCards.Remove(card);
        HasPendingApprovals = PendingApprovalCards.Count > 0;
        StatusText = $"✕ 升级 {ShortId(card.Id)} 已拒绝：凭据未消费、仍在待审批队列（本面板不再重复弹出该项）。";
    }

    private static string ShortId(string escalationId)
        => escalationId.Length <= 12 ? escalationId : escalationId[..12];

    // ============ F-M5：恢复入口（恢复按钮 → ResumeLatestCheckpointAsync 必须可达）============

    /// <summary>「恢复上次检查点」：真实 ResumeLatestCheckpointAsync；执行中/任务运行中防重入。</summary>
    [RelayCommand(CanExecute = nameof(CanResumeCheckpointNow))]
    private async Task ResumeLastCheckpointAsync()
    {
        if (IsResuming)
        {
            return;
        }

        IsResuming = true;
        StatusText = "正在恢复最近检查点…";
        try
        {
            var result = await _resumeInvoker(null, CancellationToken.None);
            StatusText = result.Succeeded
                ? $"✅ {result.Summary}"
                : $"❌ 恢复未完成：{result.Error ?? result.Summary ?? "未知原因"}";
        }
        catch (Exception ex)
        {
            StatusText = $"❌ 恢复异常：{ex.Message}";
        }
        finally
        {
            IsResuming = false;
            RefreshResumeAvailability();
        }
    }

    private bool CanResumeCheckpointNow() => CanResumeCheckpoint && !IsResuming && !IsRunning;

    /// <summary>
    /// 恢复可用性探测（无副作用、幂等）：checkpoint 存储已注入 → 用与控制器恢复同源的
    /// ResumePlanner fail-closed 判定（无候选 → 按钮禁用，绝不伪造可恢复）；未注入
    ///（DI 未注册 = LoopGuard 关闭等基线场景）→ 可用性未知，保持可点，点击后由控制器
    /// 如实返回失败结果（不虚假禁用可达路径，契约 B-APPROVAL：恢复按钮必须可达）。
    /// </summary>
    public void RefreshResumeAvailability()
    {
        if (_checkpoints is null)
        {
            CanResumeCheckpoint = true;
            ResumeHint = "恢复可用性未知：checkpoint 存储未注入（点击后由控制器如实判定）";
            return;
        }

        var has = MissionResumeProbe.HasCandidate(_checkpoints.Root);
        CanResumeCheckpoint = has;
        ResumeHint = has
            ? "检测到可恢复的最近检查点"
            : "暂无可恢复的检查点（检查点目录为空或 manifest 无效）";
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
