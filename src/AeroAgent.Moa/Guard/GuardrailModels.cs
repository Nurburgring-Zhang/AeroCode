// Copyright (c) AeroCode
// B5 GuardrailPipeline 数据模型（R2 波次 γ）。纯数据记录，无行为。
// 默认语义：MarkOnly（只标记不拦截）——拦截开关（Enforce）翻转只发生在设置/组合根（缝合阶段），
// 本层只留可选注入与配置载体。
namespace AeroAgent.Moa.Guard;

/// <summary>验证器链分段：输入段（送入模型的请求文本）→ 输出段（模型产出文本）→ 工具调用段（工具裁决前）。</summary>
public enum GuardrailStage
{
    /// <summary>输入段：请求文本（含 FactAssertionDetector 等佐证性信号检测）。</summary>
    Input = 0,
    /// <summary>输出段：模型产出文本。</summary>
    Output = 1,
    /// <summary>工具调用段：接 Harness PermissionPolicy 挂点（只升不降）。</summary>
    ToolCall = 2,
}

/// <summary>
/// Guardrail 工作模式。<see cref="MarkOnly"/> 为默认：发现只标记、记录、不改变任何流程走向；
/// <see cref="Enforce"/> 时阻断性发现升级为拦截（工具段 = 更审慎的 PermissionDecision 咨询）。
/// 翻转只允许发生在设置/组合根（缝合阶段），Moa/Harness 层不自行翻转。
/// </summary>
public enum GuardrailMode
{
    /// <summary>默认：只标记不拦截（标记不阻断流程）。</summary>
    MarkOnly = 0,
    /// <summary>阻断性发现升级为拦截（仅组合根/设置翻转后生效）。</summary>
    Enforce = 1,
}

/// <summary>发现严重度。Critical = 阻断候选（是否真阻断由 GuardrailMode 决定）。</summary>
public enum GuardrailSeverity
{
    /// <summary>提示性（只标记）。</summary>
    Info = 0,
    /// <summary>警告（只标记）。</summary>
    Warning = 1,
    /// <summary>阻断候选：Enforce 模式下触发拦截。</summary>
    Critical = 2,
}

/// <summary>一条 guardrail 发现（标记）。文本字段由验证器负责脱敏（不得含密钥等敏感形态）。</summary>
/// <param name="ValidatorName">产出发现的验证器名（审计）。</param>
/// <param name="Stage">所属段。</param>
/// <param name="Severity">严重度。</param>
/// <param name="Code">机器可读代码（如 "fact-assertion.ungrounded"）。</param>
/// <param name="Message">人类可读说明（脱敏责任在验证器）。</param>
/// <param name="Blocking">是否阻断候选（Enforce 模式下才可能真阻断）。</param>
public sealed record GuardrailFinding(
    string ValidatorName,
    GuardrailStage Stage,
    GuardrailSeverity Severity,
    string Code,
    string Message,
    bool Blocking);

/// <summary>一次 guardrail 验证请求（<see cref="Stage"/> 指明段；负载按段填充）。</summary>
public sealed record GuardrailRequest
{
    /// <summary>所属段。</summary>
    public required GuardrailStage Stage { get; init; }

    /// <summary>Input/Output 段：被验证文本。</summary>
    public string? Text { get; init; }

    /// <summary>Input/Output 段：佐证文本集合（工具输出/已保留上下文等），事实断言比对用。</summary>
    public IReadOnlyList<string> GroundedEvidence { get; init; } = Array.Empty<string>();

    /// <summary>ToolCall 段：工具名。</summary>
    public string? ToolName { get; init; }

    /// <summary>ToolCall 段：物化参数（与 PermissionPolicy.Override 的入参同形；可为 null）。</summary>
    public IReadOnlyDictionary<string, object?>? ToolArguments { get; init; }
}

/// <summary>验证裁决。<see cref="IsBlocked"/> 只在 Enforce 模式且存在阻断性发现时为 true（MarkOnly 恒 false）。</summary>
public sealed record GuardrailVerdict
{
    /// <summary>所属段。</summary>
    public required GuardrailStage Stage { get; init; }

    /// <summary>是否拦截（Enforce + 阻断性发现）。MarkOnly 模式恒 false——标记不阻断流程。</summary>
    public bool IsBlocked { get; init; }

    /// <summary>本段全部发现（含非阻断标记）。</summary>
    public IReadOnlyList<GuardrailFinding> Findings { get; init; } = Array.Empty<GuardrailFinding>();

    /// <summary>拦截原因（IsBlocked 时非空；脱敏责任在验证器）。</summary>
    public string? BlockReason { get; init; }

    /// <summary>是否没有任何发现。</summary>
    public bool HasNoFindings => Findings.Count == 0;
}

/// <summary>GuardrailPipeline 配置。默认值即安全值：MarkOnly（只标记不拦截）。</summary>
public sealed record GuardrailOptions
{
    /// <summary>工作模式。默认 MarkOnly；翻转 Enforce 只允许发生在设置/组合根（缝合阶段）。</summary>
    public GuardrailMode Mode { get; init; } = GuardrailMode.MarkOnly;
}
