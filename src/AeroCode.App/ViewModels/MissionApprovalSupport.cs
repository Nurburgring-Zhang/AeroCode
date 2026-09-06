// Copyright (c) AeroCode
// MissionApprovalSupport — F-M5 审批/恢复 UI 的纯函数支撑（无 Avalonia、无 UI 线程依赖，可直接单测）：
// 凭据/理由脱敏（契约 B-APPROVAL：凭据永不明文回显）、EscalationRequest → 卡片投影、
// checkpoint 可恢复性探针（与 MissionController 恢复同源判定，fail-closed）。
using System;
using AeroAgent.Moa.LoopGuard;
using AeroAgent.Moa.Tools.Workspace;
using AeroCode.Harness.Curation;

namespace AeroCode.App.ViewModels;

/// <summary>审批卡片脱敏口径（复用既有 SensitiveTextScrubber，不新造第二套脱敏器）。</summary>
public static class MissionApprovalRedaction
{
    /// <summary>
    /// 凭据展示文案：EscalationRequest 结构上没有掩码字段，统一显示此文案，
    /// 绝不把任何凭据原文放进卡片（契约 B-APPROVAL 硬门）。
    /// </summary>
    public const string CredentialMasked = "（凭据已隐藏）";

    /// <summary>
    /// 升级理由展示前过既有敏感形态脱敏器（与 MissionController 日志同一份实现）：
    /// 升级理由可能拼接自模型/工具输出，凭据形态不得原样进入 UI。
    /// </summary>
    public static string MaskReason(string? reason)
        => SensitiveTextScrubber.Scrub(string.IsNullOrWhiteSpace(reason) ? "（无升级理由）" : reason);
}

/// <summary>一条待审批升级的卡片投影（全部为展示安全字段，不含凭据原文）。</summary>
public sealed record MissionEscalationItem(
    string Id,
    int Turn,
    int Strikes,
    string Reason,
    string Credential,
    string RaisedAtLocal)
{
    /// <summary>从真实 EscalationRequest 投影；凭据只出现掩码文案，理由过敏感形态脱敏。</summary>
    public static MissionEscalationItem From(EscalationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new MissionEscalationItem(
            request.Id,
            request.Turn,
            request.Strikes,
            MissionApprovalRedaction.MaskReason(request.Reason),
            MissionApprovalRedaction.CredentialMasked,
            request.RaisedUtc.ToLocalTime().ToString("MM-dd HH:mm:ss"));
    }
}

/// <summary>checkpoint 可恢复性探针（与 MissionController 的恢复路径同源：ResumePlanner 判定）。</summary>
public static class MissionResumeProbe
{
    /// <summary>
    /// 判断 root 下是否存在最近有效 checkpoint（无副作用、结果确定，与
    /// ResumePlanner.TryBuildResumePlan 的 fail-closed 口径一致）。
    /// root 为空 / 无有效 checkpoint / 探针异常（文件系统故障）→ false：
    /// 宁可禁用按钮，不伪造"可恢复"。
    /// </summary>
    public static bool HasCandidate(string? checkpointRoot)
    {
        if (string.IsNullOrWhiteSpace(checkpointRoot))
        {
            return false;
        }

        try
        {
            return new ResumePlanner(checkpointRoot).TryBuildResumePlan() is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
