// Copyright (c) AeroCode
// C2（R2 波次 γ）CritiqueLoop 行为验证：判定不信自报（首轮拒绝即触发重试、末轮拒绝不再重试）、
// critique ≤2 轮硬钳制、按策略收敛（AcceptWithFindings / Fail）、reproduce null 语义、
// 取消传播、验证器故障降级为当轮拒绝。
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AeroAgent.Moa.Verify;
using Xunit;

namespace AeroCode.Tests.MoaTests;

/// <summary>脚本化验证器双：按次序出队裁决（空则 Accept），记录全部验证请求；亦可配置为每次调用抛出异常。</summary>
internal sealed class StubVerifier : ICompletionVerifier
{
    private readonly Queue<CompletionVerdict> _script = new();
    private readonly Exception? _throwEach;

    public StubVerifier(params CompletionVerdict[] verdicts)
    {
        foreach (var v in verdicts)
        {
            _script.Enqueue(v);
        }
    }

    public StubVerifier(Exception throwEach) => _throwEach = throwEach;

    public List<CompletionVerificationRequest> Requests { get; } = new();

    public ValueTask<CompletionVerdict> VerifyAsync(CompletionVerificationRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        if (_throwEach is not null)
        {
            throw _throwEach;
        }

        return ValueTask.FromResult(_script.Count > 0 ? _script.Dequeue() : CompletionVerdict.Accept());
    }
}

public sealed class CritiqueLoopTests
{
    private const string Goal = "读取笔记并汇总";
    private const string Initial = "draft v1";

    private static async Task<CritiqueOutcome> RunAsync(
        ICompletionVerifier verifier,
        CritiqueLoopOptions? options = null,
        CompletionRetryProducerAsync? reproduce = null,
        IReadOnlyList<string>? evidence = null)
    {
        var loop = new CritiqueLoop(verifier, options);
        return await loop.RunAsync(Goal, Initial, reproduce, evidence, CancellationToken.None);
    }

    [Fact]
    public async Task AcceptFirstRound_NoRetry()
    {
        var verifier = new StubVerifier(CompletionVerdict.Accept("good"));

        var outcome = await RunAsync(verifier, evidence: new[] { "tool output" });

        Assert.True(outcome.Accepted);
        Assert.True(outcome.VerificationPassed);
        Assert.Equal(Initial, outcome.Output);
        Assert.Equal(1, outcome.CritiqueRoundsUsed);
        Assert.Equal(0, outcome.RetriesUsed);
        Assert.Null(outcome.Summary);

        var request = Assert.Single(verifier.Requests);
        Assert.Equal(Goal, request.TaskGoal);
        Assert.Equal(Initial, request.Output);
        Assert.Equal(0, request.Round);
        Assert.Equal("tool output", Assert.Single(request.Evidence));
    }

    [Fact]
    public async Task RejectThenRetry_Accepts_FeedbackFlowsToProducer()
    {
        var verifier = new StubVerifier(
            CompletionVerdict.Reject("missing detail", "补充细节 X"),
            CompletionVerdict.Accept("ok now"));
        string? receivedFeedback = null;
        var produced = new List<string?>();

        var outcome = await RunAsync(
            verifier,
            reproduce: (feedback, _) =>
            {
                receivedFeedback = feedback;
                produced.Add("fixed v2");
                return ValueTask.FromResult<string?>("fixed v2");
            },
            evidence: new[] { "NOTE_BODY" });

        Assert.Equal("补充细节 X", receivedFeedback);
        Assert.Equal("fixed v2", outcome.Output);
        Assert.True(outcome.Accepted);
        Assert.True(outcome.VerificationPassed);
        Assert.Equal(2, outcome.CritiqueRoundsUsed);
        Assert.Equal(1, outcome.RetriesUsed);

        // 两个候选都被 critique（判定不信自报）：轮次 0（初始）与轮次 1（重试产出）
        Assert.Equal(new[] { 0, 1 }, verifier.Requests.Select(r => r.Round));
        Assert.Equal(Initial, verifier.Requests[0].Output);
        Assert.Equal("fixed v2", verifier.Requests[1].Output);
        Assert.Equal("NOTE_BODY", Assert.Single(verifier.Requests[1].Evidence));
    }

    [Fact]
    public async Task AlwaysReject_DefaultPolicy_AcceptsLastCritiquedOutput_WithFindings()
    {
        var verifier = new StubVerifier(
            CompletionVerdict.Reject("bad r0", "fix a"),
            CompletionVerdict.Reject("bad r1", "fix b"));

        var outcome = await RunAsync(
            verifier,
            reproduce: (_, _) => ValueTask.FromResult<string?>("fixed v2"));

        // 默认 AcceptWithFindings：保活不失败，但验证未通过必须如实可见
        Assert.True(outcome.Accepted);
        Assert.False(outcome.VerificationPassed);
        Assert.Equal("fixed v2", outcome.Output); // 最后一个被 critique 的候选
        Assert.Equal(2, outcome.CritiqueRoundsUsed);
        Assert.Equal(1, outcome.RetriesUsed);
        Assert.NotNull(outcome.Summary);
        Assert.Contains("2 critique round(s)", outcome.Summary);

        // 末轮拒绝后不再重试：重试产物将无法被 critique（判定不信自报）
        Assert.Equal(2, verifier.Requests.Count);
    }

    [Fact]
    public async Task AlwaysReject_FailPolicy_HonestFailure()
    {
        var verifier = new StubVerifier(
            CompletionVerdict.Reject("bad r0", "fix a"),
            CompletionVerdict.Reject("bad r1", "fix b"));

        var outcome = await RunAsync(
            verifier,
            options: new CritiqueLoopOptions { ConvergencePolicy = CritiqueConvergencePolicy.Fail },
            reproduce: (_, _) => ValueTask.FromResult<string?>("fixed v2"));

        Assert.False(outcome.Accepted);
        Assert.False(outcome.VerificationPassed);
        Assert.Equal(2, outcome.CritiqueRoundsUsed);
        Assert.Contains("completion not verified", outcome.Summary);
    }

    [Fact]
    public void MaxCritiqueRounds_HardClamped_CannotExceedTwo()
    {
        // 硬边界在本类钉死（不信任配置者）：≤2 轮
        Assert.Equal(2, new CritiqueLoop(new StubVerifier(), new CritiqueLoopOptions { MaxCritiqueRounds = 10 }).MaxCritiqueRounds);
        Assert.Equal(1, new CritiqueLoop(new StubVerifier(), new CritiqueLoopOptions { MaxCritiqueRounds = 0 }).MaxCritiqueRounds);
        Assert.Equal(2, CritiqueLoop.MaxCritiqueRoundsHardLimit);
    }

    [Fact]
    public async Task NoReproduceProducer_FirstRejectConverges()
    {
        var verifier = new StubVerifier(CompletionVerdict.Reject("bad", "fix"));

        var outcome = await RunAsync(verifier, reproduce: null);

        Assert.True(outcome.Accepted); // 默认策略收敛
        Assert.False(outcome.VerificationPassed);
        Assert.Equal(Initial, outcome.Output); // 保持在最后一个被 critique 的候选
        Assert.Equal(1, outcome.CritiqueRoundsUsed);
        Assert.Equal(0, outcome.RetriesUsed);
        Assert.Single(verifier.Requests);
    }

    [Fact]
    public async Task ReproduceReturnsNull_StopsRetrying_HonestConverge()
    {
        // reproduce 返回 null = 调用方判定不可再重试（如预算耗尽）→ 立即收敛。
        var verifier = new StubVerifier(
            CompletionVerdict.Reject("bad r0", "fix"),
            CompletionVerdict.Accept("never reached"));
        var producerCalled = 0;

        var outcome = await RunAsync(
            verifier,
            reproduce: (_, _) =>
            {
                producerCalled++;
                return ValueTask.FromResult<string?>(null);
            });

        Assert.Equal(1, producerCalled);
        Assert.Equal(Initial, outcome.Output);
        Assert.False(outcome.VerificationPassed);
        Assert.True(outcome.Accepted);
        Assert.Single(verifier.Requests); // 第二轮 critique 未发生

        // R2 修复 LOW-B：未实际产出不计入 RetriesUsed（只数真实发生的重试）
        Assert.Equal(0, outcome.RetriesUsed);
    }

    [Fact]
    public async Task ReproduceCountsOnlyActualProductions_ProductionCountedOnce()
    {
        // R2 修复 LOW-B：RetriesUsed 只数真实发生的产出。真实产出 1 次（第 1 轮拒绝后），
        // 第 2 轮拒绝为末轮不再重试 → producerCalls==1 且 RetriesUsed==1（轮数上限仍由
        // verdicts.Count 钉死，≤2 轮硬边界不受影响）。
        // 对照：reproduce 首次即返回 null 时 RetriesUsed==0（见 ReproduceReturnsNull 测试）——
        // 未产出不计次，与「实际发生的有界重试次数」契约一致。
        var verifier = new StubVerifier(
            CompletionVerdict.Reject("bad r0", "fix a"),
            CompletionVerdict.Reject("bad r1", "fix b"));
        var producerCalls = 0;

        var outcome = await RunAsync(
            verifier,
            reproduce: (_, _) =>
            {
                producerCalls++;
                return ValueTask.FromResult<string?>("fixed v2");
            });

        Assert.Equal(1, producerCalls); // 末轮拒绝后不再重试（判定不信自报）
        Assert.Equal("fixed v2", outcome.Output); // 保持在最后一个被 critique 的候选
        Assert.Equal(2, outcome.CritiqueRoundsUsed); // 轮数 = critique 次数（硬边界不变）
        Assert.Equal(1, outcome.RetriesUsed); // 唯一一次真实产出计一次
        Assert.False(outcome.VerificationPassed);
        Assert.True(outcome.Accepted);
        Assert.Equal(2, verifier.Requests.Count);
    }

    [Fact]
    public async Task VerifierCrash_TreatedAsRoundReject_DegradedConverge()
    {
        // 验证器非取消异常不得打断调用方任务循环：视同当轮拒绝（诚实不冒充通过）。
        var verifier = new StubVerifier(new InvalidOperationException("critic offline"));

        var outcome = await RunAsync(verifier);

        Assert.True(outcome.Accepted);
        Assert.False(outcome.VerificationPassed);
        Assert.Contains("verifier failed", outcome.Summary);
    }

    [Fact]
    public async Task VerifierCrashMessage_ThroughSensitiveScrubber_NoSecretLeak()
    {
        // R2 修复 LOW-A：验证器异常消息进裁决文本前过 SensitiveTextScrubber——
        // 密钥形态被打码为 [REDACTED]，"verifier failed" 前缀保持可读（降级原因不丢失）。
        var verifier = new StubVerifier(
            new InvalidOperationException("verify failed with key sk-abcdef1234567890abcd in body"));

        var outcome = await RunAsync(verifier);

        Assert.False(outcome.VerificationPassed);
        Assert.Contains("verifier failed:", outcome.Summary);
        Assert.Contains("[REDACTED]", outcome.Summary);
        Assert.DoesNotContain("sk-abcdef1234567890abcd", outcome.Summary);
    }

    [Fact]
    public async Task VerifierCancellation_PropagatesNotSwallowed()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var verifier = new StubVerifier(new OperationCanceledException(cts.Token));

        var loop = new CritiqueLoop(verifier);
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => loop.RunAsync(Goal, Initial, null, null, cts.Token));
    }
}
