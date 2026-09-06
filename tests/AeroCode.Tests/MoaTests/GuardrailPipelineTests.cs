// Copyright (c) AeroCode
// B5（R2 波次 γ）GuardrailPipeline 行为验证：三段路由与全链扫描不短路、
// 默认 MarkOnly 只标记不拦截、Enforce 拦输出/工具段、FactAssertionValidator（只检测不处置）、
// 单验证器异常 [DEGRADED] 跳过、取消传播、工具段适配 PermissionPolicy 挂点的集成语义。
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using AeroAgent.Moa.Guard;
using AeroCode.Harness.EventBus;
using AeroCode.Harness.Permission;
using Xunit;

namespace AeroCode.Tests.MoaTests;

/// <summary>脚本化验证器双：按注入实现返回裁决，记录收到的全部请求（观测路由与负载）。</summary>
internal sealed class StubGuardrailValidator : IGuardrailValidator
{
    private readonly Func<GuardrailRequest, CancellationToken, GuardrailVerdict> _impl;

    public StubGuardrailValidator(string name, Func<GuardrailRequest, CancellationToken, GuardrailVerdict> impl)
    {
        Name = name;
        _impl = impl;
    }

    public string Name { get; }

    public List<GuardrailRequest> Seen { get; } = new();

    public GuardrailVerdict Validate(GuardrailRequest request, CancellationToken cancellationToken)
    {
        Seen.Add(request);
        return _impl(request, cancellationToken);
    }
}

public sealed class GuardrailPipelineTests
{
    private static GuardrailFinding Finding(GuardrailStage stage, bool blocking, string message = "finding")
        => new("stub", stage, blocking ? GuardrailSeverity.Critical : GuardrailSeverity.Info,
            "test.code", message, blocking);

    [Fact]
    public void StageRouting_EachStageRunsOnlyItsOwnValidators_AllPass()
    {
        var pipeline = new GuardrailPipeline();
        var input = new StubGuardrailValidator("in", (_, _) => new GuardrailVerdict { Stage = GuardrailStage.Input });
        var output = new StubGuardrailValidator("out", (_, _) => new GuardrailVerdict { Stage = GuardrailStage.Output });
        var tool = new StubGuardrailValidator("tool", (_, _) => new GuardrailVerdict { Stage = GuardrailStage.ToolCall });
        pipeline.AddValidator(GuardrailStage.Input, input);
        pipeline.AddValidator(GuardrailStage.Output, output);
        pipeline.AddValidator(GuardrailStage.ToolCall, tool);

        var vIn = pipeline.ValidateInput("请求文本");
        var vOut = pipeline.ValidateOutput("产出文本");
        var vTool = pipeline.ValidateToolCall("write_file", null);

        Assert.Single(input.Seen);
        Assert.Equal(GuardrailStage.Input, input.Seen[0].Stage);
        Assert.Equal("请求文本", input.Seen[0].Text);
        Assert.Single(output.Seen);
        Assert.Equal(GuardrailStage.Output, output.Seen[0].Stage);
        Assert.Single(tool.Seen);
        Assert.Equal(GuardrailStage.ToolCall, tool.Seen[0].Stage);
        Assert.Equal("write_file", tool.Seen[0].ToolName);

        // 全通过：无发现、不拦截
        Assert.True(vIn.HasNoFindings);
        Assert.False(vIn.IsBlocked);
        Assert.True(vOut.HasNoFindings);
        Assert.False(vOut.IsBlocked);
        Assert.True(vTool.HasNoFindings);
        Assert.False(vTool.IsBlocked);
    }

    [Fact]
    public void FullScan_NoShortCircuit_AllFindingsAggregated()
    {
        var pipeline = new GuardrailPipeline();
        var first = new StubGuardrailValidator("first",
            (_, _) => new GuardrailVerdict { Stage = GuardrailStage.Output, Findings = new[] { Finding(GuardrailStage.Output, blocking: true, "blocking one") } });
        var second = new StubGuardrailValidator("second",
            (_, _) => new GuardrailVerdict { Stage = GuardrailStage.Output, Findings = new[] { Finding(GuardrailStage.Output, blocking: false, "info two") } });
        pipeline.AddValidator(GuardrailStage.Output, first);
        pipeline.AddValidator(GuardrailStage.Output, second);

        var verdict = pipeline.ValidateOutput("text");

        // 不短路：两个验证器都执行，发现聚合
        Assert.Single(first.Seen);
        Assert.Single(second.Seen);
        Assert.Equal(2, verdict.Findings.Count);
    }

    [Fact]
    public void DefaultMarkOnly_BlockingFinding_DoesNotBlockFlow()
    {
        // 默认构造 = MarkOnly：标记不阻断流程（IsBlocked 恒 false）。
        var pipeline = new GuardrailPipeline();
        pipeline.AddValidator(GuardrailStage.ToolCall, new StubGuardrailValidator("v",
            (_, _) => new GuardrailVerdict
            {
                Stage = GuardrailStage.ToolCall,
                Findings = new[] { Finding(GuardrailStage.ToolCall, blocking: true, "would block") },
            }));

        var verdict = pipeline.ValidateToolCall("delete_file", null);

        Assert.False(verdict.IsBlocked); // 只标记不拦截
        Assert.Null(verdict.BlockReason);
        Assert.Single(verdict.Findings);
        Assert.True(verdict.Findings[0].Blocking); // 阻断候选被如实标记
    }

    [Fact]
    public void Enforce_OutputStage_Blocked()
    {
        var pipeline = new GuardrailPipeline(new GuardrailOptions { Mode = GuardrailMode.Enforce });
        pipeline.AddValidator(GuardrailStage.Output, new StubGuardrailValidator("v",
            (_, _) => new GuardrailVerdict
            {
                Stage = GuardrailStage.Output,
                Findings = new[]
                {
                    Finding(GuardrailStage.Output, blocking: false, "info"),
                    Finding(GuardrailStage.Output, blocking: true, "harmful output"),
                },
            }));

        var verdict = pipeline.ValidateOutput("output text");

        Assert.True(verdict.IsBlocked);
        Assert.Equal("harmful output", verdict.BlockReason);
        Assert.Equal(2, verdict.Findings.Count); // 阻断不吞非阻断标记（全链聚合）
    }

    [Fact]
    public void Enforce_ToolStage_AdvisorUpgradesPolicyToDeny_OnlyUpgrade()
    {
        // 集成：Moa GuardrailPermissionAdvisor → Harness PermissionPolicy.GuardrailAdvisor 挂点。
        var pipeline = new GuardrailPipeline(new GuardrailOptions { Mode = GuardrailMode.Enforce });
        pipeline.AddValidator(GuardrailStage.ToolCall, new StubGuardrailValidator("v",
            (_, _) => new GuardrailVerdict
            {
                Stage = GuardrailStage.ToolCall,
                Findings = new[] { Finding(GuardrailStage.ToolCall, blocking: true, "tool blocked by guardrail") },
            }));
        var advisor = new GuardrailPermissionAdvisor(pipeline);

        var policy = PermissionPolicy.CreateDefault(new EventBus());
        policy.GuardrailAdvisor = advisor;

        // Ask 基线被咨询升级为 Deny，原因进入 PermissionResult.Reason
        var write = policy.Check("write_file");
        Assert.Equal(PermissionDecision.Deny, write.Decision);
        Assert.Equal("tool blocked by guardrail", write.Reason);

        // Allow 基线同样只升不降（Allow < Deny）
        var read = policy.Check("read_file");
        Assert.Equal(PermissionDecision.Deny, read.Decision);

        // 显式 Deny（write_plan）顶格短路：advisor 不被咨询
        var denied = policy.Check("write_plan");
        Assert.Equal(PermissionDecision.Deny, denied.Decision);
        Assert.Equal("Explicitly denied", denied.Reason);
    }

    [Fact]
    public void MarkOnly_AdvisorRecordsFindings_ReturnsNullConsult_PolicyUnchanged()
    {
        // 默认（MarkOnly）接线：发现只记录，权限裁决链完全不受影响。
        var pipeline = new GuardrailPipeline(); // MarkOnly
        pipeline.AddValidator(GuardrailStage.ToolCall, new StubGuardrailValidator("v",
            (_, _) => new GuardrailVerdict
            {
                Stage = GuardrailStage.ToolCall,
                Findings = new[] { Finding(GuardrailStage.ToolCall, blocking: true, "candidate") },
            }));
        var advisor = new GuardrailPermissionAdvisor(pipeline);

        var consult = advisor.AdviseToolCall("write_file", null, CancellationToken.None);

        Assert.Null(consult); // 只标记不拦截：无意见（原链路）
        var recorded = Assert.Single(advisor.RecentFindings);
        Assert.Equal("stub", recorded.ValidatorName);

        var policy = PermissionPolicy.CreateDefault(new EventBus());
        policy.GuardrailAdvisor = advisor;
        Assert.Equal(PermissionDecision.Ask, policy.Check("write_file").Decision);
    }

    [Fact]
    public void Advisor_RecentFindings_BoundedCapacity()
    {
        var pipeline = new GuardrailPipeline();
        pipeline.AddValidator(GuardrailStage.ToolCall, new StubGuardrailValidator("v",
            (_, _) => new GuardrailVerdict
            {
                Stage = GuardrailStage.ToolCall,
                Findings = new[] { Finding(GuardrailStage.ToolCall, blocking: false) },
            }));
        var advisor = new GuardrailPermissionAdvisor(pipeline);

        for (var i = 0; i < 70; i++)
        {
            advisor.AdviseToolCall("write_file", null, CancellationToken.None);
        }

        Assert.Equal(64, advisor.RecentFindings.Count); // 有界队列：容量 64
    }

    [Fact]
    public void FactAssertionValidator_UngroundedNumber_MarkedWarning_NeverBlocking()
    {
        var validator = new FactAssertionValidator();
        var request = new GuardrailRequest
        {
            Stage = GuardrailStage.Input,
            Text = "查询完成，共 12345 条记录。",
            GroundedEvidence = Array.Empty<string>(),
        };

        var verdict = validator.Validate(request, CancellationToken.None);

        var finding = Assert.Single(verdict.Findings);
        Assert.Equal(FactAssertionValidator.ValidatorName, finding.ValidatorName);
        Assert.Equal(GuardrailSeverity.Warning, finding.Severity);
        Assert.Equal("fact-assertion.ungrounded", finding.Code);
        Assert.False(finding.Blocking); // R1「只检测不处置」：恒为非阻断
        Assert.Contains("12345", finding.Message);
    }

    [Fact]
    public void FactAssertionValidator_GroundedEvidence_NoFindings()
    {
        var validator = new FactAssertionValidator();
        var request = new GuardrailRequest
        {
            Stage = GuardrailStage.Input,
            Text = "查询完成，共 12345 条记录。",
            GroundedEvidence = new[] { "工具输出：该表共 12345 条记录，状态正常。" },
        };

        var verdict = validator.Validate(request, CancellationToken.None);

        Assert.True(verdict.HasNoFindings);
    }

    [Fact]
    public void FactAssertionValidator_EvenEnforce_NeverBlocks()
    {
        // Enforce 模式下 FactAssertion 段也只标记：拦截开关不适用于 R1 只检测语义。
        var pipeline = new GuardrailPipeline(new GuardrailOptions { Mode = GuardrailMode.Enforce });
        pipeline.AddValidator(GuardrailStage.Input, new FactAssertionValidator());

        var verdict = pipeline.ValidateInput("查询完成，共 12345 条记录。");

        Assert.False(verdict.IsBlocked);
        Assert.Single(verdict.Findings);
    }

    [Fact]
    public void ValidatorException_SkippedAndChainContinues()
    {
        var pipeline = new GuardrailPipeline();
        pipeline.AddValidator(GuardrailStage.Output, new StubGuardrailValidator("boom",
            (_, _) => throw new InvalidOperationException("validator exploded")));
        pipeline.AddValidator(GuardrailStage.Output, new StubGuardrailValidator("healthy",
            (_, _) => new GuardrailVerdict
            {
                Stage = GuardrailStage.Output,
                Findings = new[] { Finding(GuardrailStage.Output, blocking: false) },
            }));

        var verdict = pipeline.ValidateOutput("text");

        // 单验证器故障不得打断验证链：跳过继续，健康验证器的发现照常聚合
        Assert.Single(verdict.Findings);
        Assert.False(verdict.IsBlocked);
    }

    [Fact]
    public void ValidatorCancellation_PropagatesNotSwallowed()
    {
        var pipeline = new GuardrailPipeline();
        pipeline.AddValidator(GuardrailStage.Output, new StubGuardrailValidator("v",
            (_, ct) => throw new OperationCanceledException(ct)));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // 取消不吞：向上传播（安全硬门）
        Assert.Throws<OperationCanceledException>(() => pipeline.ValidateOutput("text", null, cts.Token));
    }

    [Fact]
    public void Advisor_ValidatorCancellation_PropagatesNotSwallowed()
    {
        var pipeline = new GuardrailPipeline();
        pipeline.AddValidator(GuardrailStage.ToolCall, new StubGuardrailValidator("v",
            (_, ct) => throw new OperationCanceledException(ct)));
        var advisor = new GuardrailPermissionAdvisor(pipeline);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // advisor 不得把取消伪装成「无意见」
        Assert.Throws<OperationCanceledException>(() => advisor.AdviseToolCall("t", null, cts.Token));
    }

    [Fact]
    public void Advisor_EnforceBlockedReason_ScrubbedInConsult()
    {
        // Enforce 阻断原因进入咨询前过 Scrubber：密钥形态不得外泄（安全硬门 #4）。
        var pipeline = new GuardrailPipeline(new GuardrailOptions { Mode = GuardrailMode.Enforce });
        pipeline.AddValidator(GuardrailStage.ToolCall, new StubGuardrailValidator("v",
            (_, _) => new GuardrailVerdict
            {
                Stage = GuardrailStage.ToolCall,
                Findings = new[] { Finding(GuardrailStage.ToolCall, blocking: true, "leaked key sk-abcdefghij0123456789 in args") },
            }));
        var advisor = new GuardrailPermissionAdvisor(pipeline);

        var consult = advisor.AdviseToolCall("write_file", null, CancellationToken.None);

        Assert.NotNull(consult);
        Assert.Equal(PermissionDecision.Deny, consult!.Decision);
        Assert.DoesNotContain("sk-abcdefghij0123456789", consult.Reason);
        Assert.Contains("[REDACTED]", consult.Reason);
    }
}
