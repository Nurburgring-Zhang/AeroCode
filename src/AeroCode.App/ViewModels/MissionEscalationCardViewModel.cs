// Copyright (c) AeroCode
// MissionEscalationCardViewModel — 单张升级审批卡片的 VM（F-M5）。
// 决策语义：批准 = 一次性消费 MissionController 的审批凭据（TryApproveEscalation 真实结果）；
// 拒绝 = 不消费凭据（凭据仍在控制器待审批队列，后续触发会再次呈现）。
// 决策只允许一次（IsDecided 守卫，防双击重放一次性凭据）。
using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AeroCode.App.ViewModels;

/// <summary>
/// 单张升级审批卡片：展示升级理由（已脱敏）+ 凭据掩码。批准/拒绝回调由宿主
///（MissionViewModel）注入：批准回调返回 MissionController.TryApproveEscalation 的真实结果。
/// </summary>
public sealed partial class MissionEscalationCardViewModel : ObservableObject
{
    private readonly Func<string, bool> _approve;
    private readonly Action<MissionEscalationCardViewModel> _reject;

    public MissionEscalationCardViewModel(
        MissionEscalationItem item,
        Func<string, bool> approve,
        Action<MissionEscalationCardViewModel> reject)
    {
        Item = item ?? throw new ArgumentNullException(nameof(item));
        _approve = approve ?? throw new ArgumentNullException(nameof(approve));
        _reject = reject ?? throw new ArgumentNullException(nameof(reject));
    }

    /// <summary>卡片投影（展示安全字段）。</summary>
    public MissionEscalationItem Item { get; }

    public string Id => Item.Id;

    /// <summary>卡片标题（turn/strikes 来自真实升级上下文）。</summary>
    public string Title => $"任务偏离升级（turn {Item.Turn} · strikes {Item.Strikes}）";

    /// <summary>升级理由（已经 MissionApprovalRedaction.MaskReason 脱敏）。</summary>
    public string Reason => Item.Reason;

    /// <summary>凭据展示文案（恒为掩码，绝不回显原文）。</summary>
    public string Credential => Item.Credential;

    public string RaisedAt => Item.RaisedAtLocal;

    /// <summary>是否已决策（批准或拒绝任一）；决策后按钮不再可点。</summary>
    [ObservableProperty]
    private bool _isDecided;

    /// <summary>批准回调的真实返回（TryApproveEscalation 结果；拒绝时无意义）。</summary>
    public bool ApproveResult { get; private set; }

    private bool CanDecide() => !IsDecided;

    /// <summary>批准：守卫决策一次 → 真实调用宿主注入的审批回调（最终打到 controller.TryApproveEscalation）。</summary>
    [RelayCommand(CanExecute = nameof(CanDecide))]
    private void Approve()
    {
        if (IsDecided)
        {
            return;
        }

        IsDecided = true;
        ApproveResult = _approve(Id);
        ApproveCommand.NotifyCanExecuteChanged();
        RejectCommand.NotifyCanExecuteChanged();
    }

    /// <summary>拒绝：守卫决策一次 → 宿主收层（凭据不消费，仍在待审批队列）。</summary>
    [RelayCommand(CanExecute = nameof(CanDecide))]
    private void Reject()
    {
        if (IsDecided)
        {
            return;
        }

        IsDecided = true;
        _reject(this);
        ApproveCommand.NotifyCanExecuteChanged();
        RejectCommand.NotifyCanExecuteChanged();
    }
}
