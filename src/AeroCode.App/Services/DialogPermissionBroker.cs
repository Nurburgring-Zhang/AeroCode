// Copyright (c) AeroCode V3.0
// DialogPermissionBroker — interactive authorization for tool calls judged "Ask".
// Real Avalonia dialog (allow / deny / remember), decisions persisted to permissions.json.
// 批次 B G3/G5：可选装配 IPermissionAdvisor（LLM 智能审批）——Ask 先问建议器，
// risk=low 且设置允许自动采纳 → 直接 Allow（记录，不打扰用户）；否则弹窗，
// 弹窗显示建议与理由（advice 随 PermissionPrompt 进入两条真实 UI 路径）。
using System.Text.Encodings.Web;
using System.Text.Json;
using AeroAgent.Moa.Safety;
using AeroAgent.Moa.Tools;
using AeroCode.Harness.Permission;
using Microsoft.Extensions.Logging;

namespace AeroCode.App.Services;

/// <summary>授权对话框的输入：工具名 + 人类可读的参数预览（模型将要用这些参数执行）。</summary>
/// <param name="AdvisorNote">智能审批建议（可空）：建议/风险/理由一行式摘要，弹窗展示。</param>
public sealed record PermissionPrompt(string ToolName, string ArgumentsPreview, string? AdvisorNote = null);

/// <summary>授权对话框的输出：是否放行 + 是否记住（记住后写入策略并持久化）。</summary>
public sealed record PermissionDialogResult(bool Approved, bool Remember);

/// <summary>
/// 对话框呈现层抽象：生产实现是真实 Avalonia 窗口（<see cref="AvaloniaPermissionDialogPresenter"/>），
/// 单元测试用可编程实现。返回 null = 对话框被关闭/取消/无界面 → 由 broker 按拒绝处理。
/// </summary>
public interface IPermissionDialogPresenter
{
    Task<PermissionDialogResult?> ShowAsync(PermissionPrompt prompt, CancellationToken ct);
}

/// <summary>
/// 交互式授权代理（<see cref="IPermissionBroker"/> 的 App 实现）：
/// 策略判定 Ask 的工具调用弹窗征求用户决定（允许/拒绝/记住）。
/// 纪律：拿不到用户决定一律 Deny，绝不静默放行；并发 worker 同时请求授权时
/// 用信号量串行弹窗（同一时刻最多一个授权对话框），排队期间若其他 worker
/// 已"记住"该工具的决定，直接沿用、不重复弹窗；"记住"即时写入策略 + permissions.json。
/// </summary>
public sealed class DialogPermissionBroker : IPermissionBroker
{
    /// <summary>参数预览长度上限（超长截断并如实标注）。</summary>
    public const int MaxPreviewLength = 4000;

    /// <summary>预览专用序列化：缩进 + 非 ASCII 原样可读（仅用于对话框展示，不上网络）。</summary>
    private static readonly JsonSerializerOptions PreviewJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly PermissionPolicy _policy;
    private readonly JsonPermissionStore _store;
    private readonly IPermissionDialogPresenter _presenter;
    private readonly IPermissionAdvisor? _advisor;
    private readonly bool _autoApproveLowRisk;
    private readonly bool _tightenAutoAdopt;
    private readonly IReadOnlySet<string>? _autoAdoptWhitelist;
    private readonly Func<bool>? _forceInteractive;
    private readonly ILogger<DialogPermissionBroker>? _logger;
    private readonly SemaphoreSlim _dialogGate = new(1, 1);

    public DialogPermissionBroker(
        PermissionPolicy policy,
        JsonPermissionStore store,
        IPermissionDialogPresenter presenter,
        ILogger<DialogPermissionBroker>? logger = null,
        IPermissionAdvisor? advisor = null,
        bool autoApproveLowRisk = false,
        bool tightenAutoAdopt = false,
        IEnumerable<string>? autoAdoptModifiedArgsWhitelist = null,
        Func<bool>? forceInteractive = null)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));
        _logger = logger;
        _advisor = advisor;
        _autoApproveLowRisk = autoApproveLowRisk;
        // 批次 C 安全切片：收紧开关默认 false = 现行为逐字节一致（现状无自动采纳白名单概念，
        // 收紧逻辑只在显式启用时生效）。白名单 = 允许"被脱敏后仍参与自动采纳判定"的参数名集合。
        _tightenAutoAdopt = tightenAutoAdopt;
        _autoAdoptWhitelist = autoAdoptModifiedArgsWhitelist is null
            ? null
            : new HashSet<string>(autoAdoptModifiedArgsWhitelist, StringComparer.Ordinal);
        // R3 修复（S-MED-2）：熔断强制人工信号。组合根把它接到 ApprovalCircuitBreaker.IsBroken——
        // 熔断后该信号为真，advisor risk=low 的自动放行分支被跳过，直接弹窗人工裁决，
        // 使"连续批准限制"对自动通道真正生效（默认 null = 无信号，行为与现行为一致）。
        _forceInteractive = forceInteractive;
    }

    public async ValueTask<PermissionDecision> ResolveAsync(
        string toolName,
        IReadOnlyDictionary<string, object?>? args,
        CancellationToken ct)
    {
        var preview = FormatArguments(args);

        try
        {
            await _dialogGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // 门内复检：排队等待期间另一个 worker 可能已为该工具"记住"了决定。
                // Override（危险模式探测）仍会保持 Ask——那种情况照常弹窗。
                var rechecked = _policy.Check(toolName, args);
                if (rechecked.Decision != PermissionDecision.Ask)
                {
                    return rechecked.Decision == PermissionDecision.Allow
                        ? PermissionDecision.Allow
                        : PermissionDecision.Deny;
                }

                // ---- 智能审批（G3/G5）：Ask 先问建议器；任何失败=无建议，不阻塞审批主链 ----
                string? advisorNote = null;
                if (_advisor is { IsAvailable: true } advisor)
                {
                    var advice = await advisor.AdviseAsync(toolName, args, ct).ConfigureAwait(false);
                    if (advice is not null && !string.Equals(advice.Risk, "unknown", StringComparison.Ordinal))
                    {
                        advisorNote = $"AI 建议：{advice.Recommend}（风险 {advice.Risk}）— {advice.Reason}";
                        // R3 修复（S-MED-2）：熔断强制人工——熔断后该信号为真时，advisor risk=low
                        // 的自动放行分支整体跳过，直接走弹窗人工裁决（advisor 建议仍随弹窗展示供参考）。
                        var forcedInteractive = _forceInteractive?.Invoke() == true;
                        if (forcedInteractive)
                        {
                            _logger?.LogWarning(
                                "工具 '{Tool}' 触发强制人工信号（审批熔断中）：跳过 advisor 自动放行，转弹窗人工裁决",
                                toolName);
                        }
                        else if (_autoApproveLowRisk
                            && string.Equals(advice.Risk, "low", StringComparison.Ordinal)
                            && !string.Equals(advice.Recommend, "deny", StringComparison.Ordinal))
                        {
                            if (!_tightenAutoAdopt)
                            {
                                // 现行为（收紧开关关闭）：与既有实现逐字节一致。
                                _logger?.LogInformation(
                                    "工具 '{Tool}' 智能审批自动放行（risk=low，AutoApproveLowRisk）：{Reason}",
                                    toolName, advice.Reason);
                                return PermissionDecision.Allow;
                            }

                            // 批次 C 收紧：args 无修改 → 采纳；被脱敏 → 须全部被脱敏参数在白名单内；
                            // 否则转既有审批路径（弹窗人工裁决）——不静默丢弃、不静默放行。
                            var gate = AdvisorAutoAdoptGate.Evaluate(AdvisorArgsSanitizer.Sanitize(args), _autoAdoptWhitelist);
                            if (gate.Allow)
                            {
                                _logger?.LogInformation(
                                    "工具 '{Tool}' 智能审批自动放行（risk=low，AutoApproveLowRisk，收紧校验通过：{GateReason}）：{Reason}",
                                    toolName, gate.Reason, advice.Reason);
                                return PermissionDecision.Allow;
                            }

                            _logger?.LogInformation(
                                "工具 '{Tool}' 自动采纳被收紧门拒绝（{GateReason}）→ 转人工审批",
                                toolName, gate.Reason);
                            advisorNote = $"{advisorNote}\n[自动采纳收紧] {gate.Reason}";
                        }
                    }
                }

                var prompt = new PermissionPrompt(toolName, preview, advisorNote);

                PermissionDialogResult? result;
                try
                {
                    result = await _presenter.ShowAsync(prompt, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw; // 取消统一走外层 OCE → Deny
                }
                catch (Exception ex)
                {
                    // 呈现层异常 = 拿不到用户决定 → 诚实拒绝，绝不放行。
                    _logger?.LogWarning(
                        "[DEGRADED] 授权对话框呈现层异常，工具 '{Tool}' 按拒绝处理：{Error}",
                        toolName, ex.Message);
                    return PermissionDecision.Deny;
                }

                if (ct.IsCancellationRequested)
                {
                    // 对话轮已取消：用户决定作废，按拒绝收尾（不留悬空授权）。
                    return PermissionDecision.Deny;
                }

                if (result is null)
                {
                    _logger?.LogWarning(
                        "工具 '{Tool}' 授权对话框未产生决定（被关闭/取消/无界面）→ 按拒绝处理", toolName);
                    return PermissionDecision.Deny;
                }

                var decision = result.Approved ? PermissionDecision.Allow : PermissionDecision.Deny;
                if (result.Remember)
                {
                    // 记住必须在门内完成落库：否则排队的下一个 worker 复检时
                    // 决定尚未生效，会重复弹窗。
                    _policy.SetDefaultDecision(toolName, decision);
                    await PersistDecisionAsync(toolName, decision).ConfigureAwait(false);
                }

                return decision;
            }
            finally
            {
                _dialogGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // 等门/弹窗期间对话轮被取消：无用户决定 → 诚实拒绝，绝不静默放行。
            return PermissionDecision.Deny;
        }
    }

    /// <summary>持久化失败只降级记录，不影响本次已做出的授权决定。</summary>
    private async Task PersistDecisionAsync(string toolName, PermissionDecision decision)
    {
        try
        {
            var settings = await _store.LoadAsync().ConfigureAwait(false);
            settings.ToolDecisions[toolName] = decision;
            await _store.SaveAsync(settings).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("[DEGRADED] 权限决策持久化失败（本次决定仍然生效）：{Error}", ex.Message);
        }
    }

    /// <summary>物化参数 → 对话框可读预览；无参数时如实标注。</summary>
    internal static string FormatArguments(IReadOnlyDictionary<string, object?>? args)
    {
        if (args is null || args.Count == 0)
        {
            return "(无参数)";
        }

        string text;
        try
        {
            // 预览是给用户看的知情同意材料：中文等非 ASCII 必须原样可读，
            // 不能用默认 Encoder 转义成 \uXXXX。
            text = JsonSerializer.Serialize(args, PreviewJsonOptions);
        }
        catch (Exception)
        {
            // 参数含不可序列化对象时退化为逐项列举——绝不吞掉信息。
            text = string.Join(Environment.NewLine,
                args.Select(kv => $"{kv.Key} = {kv.Value ?? "null"}"));
        }

        return text.Length <= MaxPreviewLength
            ? text
            : text[..MaxPreviewLength] + Environment.NewLine + "…(参数过长已截断，完整内容仍会传给工具)";
    }
}
