// Copyright (c) AeroCode
// B5 GuardrailPipeline 验证器契约（R2 波次 γ）。
// 设计取舍：v0 验证器全部为无 IO 的纯启发式（如 FactAssertionDetector），接口取同步返回——
// 避免 Harness PermissionPolicy.Check 同步路径出现 GetAwaiter().GetResult() 同步阻塞
// （正是 L2 要消灭的形态）。CancellationToken 仍在契约内全程透传：未来 IO 型验证器
// 必须观察取消；当前纯函数实现观察到取消即意味着调用方已放弃（正常返回即可）。
namespace AeroAgent.Moa.Guard;

/// <summary>
/// 单个 guardrail 验证器。实现约束：
/// 1. 无副作用（允许自身发现收集/标记记录）且线程安全（MOA 并行 worker 并发验证）；
/// 2. 快速返回（纯裁决，不做 IO/不做模型调用——有模型参与的场景归 C2 CritiqueLoop 域）；
/// 3. 不得抛出非取消异常（Pipeline 对单验证器异常按 [DEGRADED] 跳过并继续全链）；
///    OperationCanceledException 必须向上传播（取消不吞）；
/// 4. 文本字段（Message 等）自行脱敏，不得携带密钥等敏感形态。
/// </summary>
public interface IGuardrailValidator
{
    /// <summary>验证器名（审计与日志）。</summary>
    string Name { get; }

    /// <summary>
    /// 对一次请求验证。<see cref="GuardrailVerdict.IsBlocked"/> 表达的是「存在阻断候选」——
    /// 是否真拦截由 Pipeline 的 <see cref="GuardrailMode"/> 决定（MarkOnly 下恒不阻断）。
    /// </summary>
    GuardrailVerdict Validate(GuardrailRequest request, CancellationToken cancellationToken);
}
