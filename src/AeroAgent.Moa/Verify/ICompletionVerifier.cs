// Copyright (c) AeroCode
// C2 完成判定验证器契约（R2 波次 γ）。
// 硬语义：critique 与产出路径分离——判定不信自报。验证器是独立于产出方的裁决通道，
// 不得复用产出方的自评结论；佐证（Evidence）必须来自产出路径之外的独立观测（工具输出等）。
namespace AeroAgent.Moa.Verify;

/// <summary>
/// 完成判定验证器（独立 critique 通道）。实现约束：
/// 1. 独立性：与产出路径分离，判定不得采信产出方自报的完成声明；
/// 2. 线程安全（MOA 并行 worker 并发验证）；
/// 3. 取消全程透传：OperationCanceledException 必须向上传播（不得吞）；
/// 4. 非取消异常由调用方（CritiqueLoop）按策略处理，验证器自行保证异常消息脱敏（不得含密钥）。
/// </summary>
public interface ICompletionVerifier
{
    /// <summary>对一次产出做完成判定。拒绝时应在 <see cref="CompletionVerdict.Feedback"/> 给出可执行的修复反馈。</summary>
    ValueTask<CompletionVerdict> VerifyAsync(CompletionVerificationRequest request, CancellationToken cancellationToken);
}
