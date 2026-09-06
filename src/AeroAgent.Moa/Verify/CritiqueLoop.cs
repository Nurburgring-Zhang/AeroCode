// Copyright (c) AeroCode
// C2 CritiqueLoop（R2 波次 γ）：完成判定链 = 自评产出（调用方已判 done）→ 独立 critique →
// 有界重试 → 轮耗尽按策略收敛。
// 硬语义：
// 1. 判定不信自报——每个候选产出都必须经独立 critique，绝不接受未经 critique 的产出
//    （因此最后一轮 critique 拒绝后不再重试：其结果将无法被 critique）；
// 2. critique 循环 ≤2 轮——MaxCritiqueRounds 构造时硬钳制 [1, 2]，配置无法突破；
// 3. 重试消耗计入既有任务预算语义——重试产出由调用方 reproduce 委托产生，其内部 provider
//    调用自带 TurnBudget.AddActual / mission 闸门 ReportUsage 全套核算（本类不重复计价也不绕过）；
//    reproduce 返回 null = 调用方判定不可再重试（如预算耗尽）→ 立即按策略收敛；
// 4. 取消全程透传——ct 传给 verifier 与 reproduce，OperationCanceledException 向上传播不吞；
// 5. 验证器非取消异常按 [DEGRADED] 收敛处理（拒绝视同当轮裁决），不得打断调用方任务循环。
using AeroCode.Harness.Curation;
namespace AeroAgent.Moa.Verify;

/// <summary>
/// 有界重试的产出委托：输入 critique 修复反馈，输出新的候选产出文本。
/// 返回 null = 调用方判定不可再重试（如任务预算耗尽——重试消耗计入预算语义，不绕过）；
/// 返回空串 = 一次合法（大概率被拒绝的）产出，会照常进入下一轮 critique。
/// 实现内部必须自行完成真实用量/成本/闸门核算与取消处理。
/// </summary>
public delegate ValueTask<string?> CompletionRetryProducerAsync(string? critiqueFeedback, CancellationToken cancellationToken);

/// <summary>critique 循环引擎（无 IO、无 logger；观测由调用方与 Verdicts 列表承载）。</summary>
public sealed class CritiqueLoop
{
    /// <summary>critique 轮硬上限（「critique 循环 ≤2 轮」）。</summary>
    public const int MaxCritiqueRoundsHardLimit = 2;

    private readonly ICompletionVerifier _verifier;
    private readonly CritiqueLoopOptions _options;

    public CritiqueLoop(ICompletionVerifier verifier, CritiqueLoopOptions? options = null)
    {
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _options = options ?? new CritiqueLoopOptions();
        // 硬边界在本类钉死（不信任配置者）：≤2 轮。
        MaxCritiqueRounds = Math.Clamp(_options.MaxCritiqueRounds, 1, MaxCritiqueRoundsHardLimit);
    }

    /// <summary>生效的 critique 轮上限（构造时硬钳制后的值）。</summary>
    public int MaxCritiqueRounds { get; }

    /// <summary>生效的收敛策略。</summary>
    public CritiqueConvergencePolicy ConvergencePolicy => _options.ConvergencePolicy;

    /// <summary>
    /// 运行完成判定链。<paramref name="initialOutput"/> 是产出方自评完成的初始产出；
    /// 每轮经 <see cref="ICompletionVerifier"/>（独立通道）裁决，拒绝且有界余量时经
    /// <paramref name="reproduce"/> 重试；轮耗尽/不可重试时按策略收敛。
    /// </summary>
    /// <param name="taskGoal">任务目标（critique 判定基准）。</param>
    /// <param name="initialOutput">初始产出文本。</param>
    /// <param name="reproduce">有界重试产出委托（null = 不具备重试能力，首轮拒绝即收敛）。</param>
    /// <param name="evidence">独立佐证集合（工具输出等；判定不信自报）。</param>
    /// <param name="cancellationToken">取消令牌（全程透传）。</param>
    public async Task<CritiqueOutcome> RunAsync(
        string taskGoal,
        string initialOutput,
        CompletionRetryProducerAsync? reproduce,
        IReadOnlyList<string>? evidence,
        CancellationToken cancellationToken)
    {
        var output = initialOutput;
        var verdicts = new List<CompletionVerdict>();
        var retries = 0;

        for (var round = 0; ; round++)
        {
            var verdict = await VerifyQuietlyAsync(taskGoal, output, evidence, round, cancellationToken)
                .ConfigureAwait(false);
            verdicts.Add(verdict);

            if (verdict.Accepted)
            {
                return new CritiqueOutcome
                {
                    Output = output,
                    Accepted = true,
                    VerificationPassed = true,
                    CritiqueRoundsUsed = verdicts.Count,
                    RetriesUsed = retries,
                    Verdicts = verdicts,
                };
            }

            // 轮耗尽（本轮是最后一轮）→ 按策略收敛；不再重试——重试产物将无法被 critique
            // （判定不信自报：不接受任何未经 critique 的产出）。
            if (verdicts.Count >= MaxCritiqueRounds || reproduce is null)
            {
                return Converge(output, verdicts, retries);
            }

            // 有界重试：反馈交还调用方产出（其内部自带预算/闸门核算，消耗计入既有语义不绕过）。
            var next = await reproduce(verdict.Feedback, cancellationToken).ConfigureAwait(false);
            if (next is null)
            {
                // 调用方判定不可再重试（如预算耗尽）：立即按策略收敛，产出保持在最后一个已 critique 的候选。
                // R2 修复 LOW-B：未实际产出不计入 RetriesUsed（只数真实发生的重试；
                // 轮数上限仍由 verdicts.Count 钉死，≤2 轮硬边界与收敛语义不受影响）。
                return Converge(output, verdicts, retries);
            }

            output = next;
            retries++;
        }
    }

    /// <summary>单轮 critique；验证器非取消异常视同当轮拒绝（[DEGRADED] 语义），取消照常向上传播。</summary>
    private async Task<CompletionVerdict> VerifyQuietlyAsync(
        string taskGoal,
        string output,
        IReadOnlyList<string>? evidence,
        int round,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _verifier.VerifyAsync(new CompletionVerificationRequest
            {
                TaskGoal = taskGoal,
                Output = output,
                Evidence = evidence ?? Array.Empty<string>(),
                Round = round,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw; // 取消不吞。
        }
        catch (Exception ex)
        {
            // 验证器故障不得打断调用方任务循环：视同当轮拒绝（走收敛路径，诚实不冒充通过）。
            // R2 修复 LOW-A：异常消息可能夹带敏感形态（如堆栈里的密钥）——先过 Scrubber 再进裁决文本。
            return CompletionVerdict.Reject($"verifier failed: {SensitiveTextScrubber.Scrub(ex.Message)}");
        }
    }

    private CritiqueOutcome Converge(string output, List<CompletionVerdict> verdicts, int retries)
    {
        var last = verdicts[^1];
        var summary =
            $"completion not verified after {verdicts.Count} critique round(s): {last.Reason ?? "rejected"}";
        return new CritiqueOutcome
        {
            Output = output,
            Accepted = _options.ConvergencePolicy != CritiqueConvergencePolicy.Fail,
            VerificationPassed = false,
            CritiqueRoundsUsed = verdicts.Count,
            RetriesUsed = retries,
            Verdicts = verdicts,
            Summary = summary,
        };
    }
}
