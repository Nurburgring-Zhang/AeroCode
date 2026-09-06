// Copyright (c) AeroCode
// B5 GuardrailPipeline（R2 波次 γ）：输入/输出/工具调用三段验证器链。
// 语义：
// 1. 全链扫描不短路（与 Moa ToolGuardChain 同思想）：每个验证器总是执行，发现聚合返回；
// 2. 默认 MarkOnly 只标记不拦截——标记不阻断流程；Enforce 时阻断性发现才升级为拦截，
//    开关翻转只发生在设置/组合根（缝合阶段）；
// 3. 单验证器异常按 [DEGRADED] 跳过并继续全链，OperationCanceledException 一律向上传播（取消不吞）；
// 4. 工具段经 GuardrailPermissionAdvisor 适配进 Harness PermissionPolicy 挂点（只升不降），
//    依赖方向 Moa → Harness，本文件是实现侧。
using AeroAgent.Moa.Curation;
using AeroCode.Harness.Curation;
using AeroCode.Harness.Permission;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AeroAgent.Moa.Guard;

/// <summary>
/// 三段验证器链。注册顺序即执行顺序；每段全链扫描取聚合发现（无短路）。
/// 线程安全：AddValidator 与 Validate* 可并发（内部锁），验证器自身须无副作用。
/// </summary>
public sealed class GuardrailPipeline
{
    private readonly GuardrailOptions _options;
    private readonly ILogger _logger;
    private readonly object _sync = new();
    private readonly List<IGuardrailValidator>[] _stages =
    {
        new(), // Input
        new(), // Output
        new(), // ToolCall
    };

    public GuardrailPipeline(GuardrailOptions? options = null, ILogger<GuardrailPipeline>? logger = null)
    {
        _options = options ?? new GuardrailOptions();
        _logger = logger ?? NullLogger<GuardrailPipeline>.Instance;
    }

    /// <summary>工作模式（构造时钉死；Enforce 翻转只发生在设置/组合根）。</summary>
    public GuardrailMode Mode => _options.Mode;

    /// <summary>向指定段注册验证器（注册顺序 = 执行顺序）。</summary>
    public void AddValidator(GuardrailStage stage, IGuardrailValidator validator)
    {
        ArgumentNullException.ThrowIfNull(validator);
        lock (_sync)
        {
            _stages[(int)stage].Add(validator);
        }
    }

    /// <summary>输入段验证：请求文本 + 佐证集合（FactAssertionDetector 等佐证性信号）。</summary>
    public GuardrailVerdict ValidateInput(
        string? text,
        IReadOnlyList<string>? groundedEvidence = null,
        CancellationToken cancellationToken = default)
        => ValidateStage(GuardrailStage.Input, new GuardrailRequest
        {
            Stage = GuardrailStage.Input,
            Text = text,
            GroundedEvidence = groundedEvidence ?? Array.Empty<string>(),
        }, cancellationToken);

    /// <summary>输出段验证：模型产出文本 + 佐证集合。</summary>
    public GuardrailVerdict ValidateOutput(
        string? text,
        IReadOnlyList<string>? groundedEvidence = null,
        CancellationToken cancellationToken = default)
        => ValidateStage(GuardrailStage.Output, new GuardrailRequest
        {
            Stage = GuardrailStage.Output,
            Text = text,
            GroundedEvidence = groundedEvidence ?? Array.Empty<string>(),
        }, cancellationToken);

    /// <summary>工具调用段验证：工具名 + 物化参数（与 PermissionPolicy.Override 入参同形）。</summary>
    public GuardrailVerdict ValidateToolCall(
        string toolName,
        IReadOnlyDictionary<string, object?>? args = null,
        CancellationToken cancellationToken = default)
        => ValidateStage(GuardrailStage.ToolCall, new GuardrailRequest
        {
            Stage = GuardrailStage.ToolCall,
            ToolName = toolName,
            ToolArguments = args,
        }, cancellationToken);

    private GuardrailVerdict ValidateStage(GuardrailStage stage, GuardrailRequest request, CancellationToken cancellationToken)
    {
        IGuardrailValidator[] validators;
        lock (_sync)
        {
            validators = _stages[(int)stage].ToArray();
        }

        var findings = new List<GuardrailFinding>();
        foreach (var validator in validators)
        {
            GuardrailVerdict verdict;
            try
            {
                verdict = validator.Validate(request, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw; // 取消不吞：向上传播（安全硬门 #2）
            }
            catch (Exception ex)
            {
                // 单验证器故障不得打断验证链：跳过并继续（下一验证器/原流程兜底），降级必须可见。
                _logger.LogWarning(
                    "[DEGRADED] guardrail validator '{Validator}' failed at stage {Stage}, skipped: {Error}",
                    validator.Name, stage, ex.Message);
                continue;
            }

            findings.AddRange(verdict.Findings);
        }

        var blocking = _options.Mode == GuardrailMode.Enforce
            ? findings.FirstOrDefault(f => f.Blocking)
            : null;

        return new GuardrailVerdict
        {
            Stage = stage,
            IsBlocked = blocking is not null,
            Findings = findings,
            BlockReason = blocking?.Message,
        };
    }
}

/// <summary>
/// 输入段内置验证器：FactAssertionDetector（R1 β，「只检测不处置」约定）的 guardrail 接入——
/// 未佐证事实形断言 → Warning 级标记。<see cref="GuardrailFinding.Blocking"/> 恒 false：
/// 即使 Enforce 也不拦截，维持 R1 只检测语义（拦截开关翻转归组合根，且不适用于本验证器）。
/// </summary>
public sealed class FactAssertionValidator : IGuardrailValidator
{
    public const string ValidatorName = "fact-assertion";

    /// <inheritdoc />
    public string Name => ValidatorName;

    /// <inheritdoc />
    public GuardrailVerdict Validate(GuardrailRequest request, CancellationToken cancellationToken)
    {
        var assertions = FactAssertionDetector.Detect(request.Text, request.GroundedEvidence);
        if (assertions.Count == 0)
        {
            return new GuardrailVerdict { Stage = request.Stage };
        }

        var findings = new List<GuardrailFinding>(assertions.Count);
        foreach (var a in assertions)
        {
            findings.Add(new GuardrailFinding(
                ValidatorName,
                request.Stage,
                GuardrailSeverity.Warning,
                "fact-assertion.ungrounded",
                // 与 R1 ContextCurator 的敏感形态过滤同口径：标记文本先过 Scrubber 再入库。
                SensitiveTextScrubber.Scrub($"{KindLabel(a.Kind)}「{a.MatchedText}」：{a.Excerpt}"),
                Blocking: false));
        }

        return new GuardrailVerdict { Stage = request.Stage, Findings = findings };
    }

    private static string KindLabel(FactAssertionKind kind) => kind switch
    {
        FactAssertionKind.Date => "日期断言",
        FactAssertionKind.NamedEntity => "实体断言",
        _ => "数字断言",
    };
}

/// <summary>
/// 把 <see cref="GuardrailPipeline"/> 工具段接进 Harness <see cref="IPermissionGuardrailAdvisor"/>
/// 挂点的适配器（B5 接线在依赖方向正确的一侧：Moa 实现 Harness 契约）。
/// MarkOnly：发现只记录（<see cref="RecentFindings"/> 供审计/测试观测），返回 null = 无意见（原链路）；
/// Enforce：阻断性发现 → Deny 咨询（PermissionPolicy 按审慎度只升不降，绝不降级既有基线）。
/// </summary>
public sealed class GuardrailPermissionAdvisor : IPermissionGuardrailAdvisor
{
    private const int RecentCapacity = 64;
    private readonly GuardrailPipeline _pipeline;
    private readonly ILogger _logger;
    private readonly object _sync = new();
    private readonly Queue<GuardrailFinding> _recent = new();

    public GuardrailPermissionAdvisor(GuardrailPipeline pipeline, ILogger? logger = null)
    {
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>最近记录的标记（有界队列快照，容量 64；审计与测试观测用）。</summary>
    public IReadOnlyList<GuardrailFinding> RecentFindings
    {
        get
        {
            lock (_sync)
            {
                return _recent.ToList();
            }
        }
    }

    /// <inheritdoc />
    public GuardrailConsult? AdviseToolCall(
        string toolName,
        IReadOnlyDictionary<string, object?>? args,
        CancellationToken cancellationToken)
    {
        GuardrailVerdict verdict;
        try
        {
            verdict = _pipeline.ValidateToolCall(toolName, args, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw; // 取消不吞：向上传播（安全硬门 #2），不得伪装成「无意见」。
        }
        catch (Exception)
        {
            // guardrail 自身故障不得打断权限裁决链：按无意见交还原链路（[DEGRADED] 可见性由 Pipeline 日志保证）。
            return null;
        }

        foreach (var f in verdict.Findings)
        {
            lock (_sync)
            {
                _recent.Enqueue(f);
                while (_recent.Count > RecentCapacity)
                {
                    _recent.Dequeue();
                }
            }
        }

        // R3 修复（F-MED-3）：MarkOnly 默认态 findings 不得只进内存队列——逐条 WARN 使其可观测。
        // finding 文本的脱敏责任在验证器侧（ToolCallGuardrailValidator 已保证），此处直接记录。
        foreach (var f in verdict.Findings)
        {
            _logger.LogWarning(
                "[guardrail:{Stage}] {Severity} {Code} ({Validator}): {Message} tool={Tool}",
                f.Stage, f.Severity, f.Code, f.ValidatorName, f.Message, toolName);
        }

        if (!verdict.IsBlocked)
        {
            return null; // MarkOnly（默认）或无阻断候选：只标记不拦截。
        }

        return new GuardrailConsult(
            PermissionDecision.Deny,
            SensitiveTextScrubber.Scrub(verdict.BlockReason) ?? "guardrail blocked");
    }
}
