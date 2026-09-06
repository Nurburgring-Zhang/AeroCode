// Copyright (c) AeroCode
// ToolCallGuardrailValidator 测试（批次 C 安全切片，builder-γ）：R2 延后项「guardrail 工具段
// 独立验证器」。可脱离管道单独实例化单测（纯函数、无 IO、不真启任何进程/模型）；
// 只产出 finding——MarkOnly 默认拦截语义不变（注册进管道 MarkOnly 仍恒不阻断）。
using System;
using System.Collections.Generic;
using System.Threading;
using AeroAgent.Moa.Guard;
using Xunit;

namespace AeroCode.Tests.MoaTests;

public sealed class ToolCallGuardrailValidatorTests
{
    // 命中 canonical 词表 sk- 形态的确定性假凭据（测试专用）。
    private const string FakeKey = "sk-ABCDEFGHIJKLMNOPQRSTUVWXYZ123456";

    private static GuardrailRequest ToolRequest(string toolName, Dictionary<string, object?>? args) => new()
    {
        Stage = GuardrailStage.ToolCall,
        ToolName = toolName,
        ToolArguments = args,
    };

    [Fact]
    public void CleanArgs_NoFindings()
    {
        var validator = new ToolCallGuardrailValidator();
        var verdict = validator.Validate(
            ToolRequest("write_file", new Dictionary<string, object?> { ["path"] = "src/a.cs", ["content"] = "hello" }),
            CancellationToken.None);

        Assert.True(verdict.HasNoFindings);
        Assert.False(verdict.IsBlocked);
    }

    [Fact]
    public void SensitiveArgs_WarningFinding_NotBlocking_MessageScrubbed()
    {
        var validator = new ToolCallGuardrailValidator();
        var verdict = validator.Validate(
            ToolRequest("store_secret", new Dictionary<string, object?> { ["api_key"] = FakeKey }),
            CancellationToken.None);

        var finding = Assert.Single(verdict.Findings);
        Assert.Equal("tool-call.sensitive-args", finding.Code);
        Assert.Equal(GuardrailSeverity.Warning, finding.Severity);
        Assert.False(finding.Blocking); // 只标记
        Assert.Contains("api_key", finding.Message, StringComparison.Ordinal);   // 参数名可见
        Assert.DoesNotContain(FakeKey, finding.Message, StringComparison.Ordinal); // 敏感原文绝不入 finding
    }

    [Fact]
    public void DestructiveCommand_CriticalBlockingCandidate()
    {
        var validator = new ToolCallGuardrailValidator();

        foreach (var command in new[]
                 {
                     "rm -rf /",
                     "rm -fr ~",
                     "rm -rf C:\\",
                     "rd /s /q C:\\old",
                     "Remove-Item -Recurse -Force C:\\old",
                     "git push origin main --force",
                 })
        {
            var verdict = validator.Validate(
                ToolRequest("run_shell", new Dictionary<string, object?> { ["command"] = command }),
                CancellationToken.None);

            var finding = Assert.Single(verdict.Findings);
            Assert.Equal("tool-call.destructive-command", finding.Code);
            Assert.Equal(GuardrailSeverity.Critical, finding.Severity);
            Assert.True(finding.Blocking); // 阻断候选（是否真拦截由 GuardrailMode 决定）
        }
    }

    [Fact]
    public void OrdinaryCommand_NoDestructiveFinding()
    {
        var validator = new ToolCallGuardrailValidator();
        var verdict = validator.Validate(
            ToolRequest("run_shell", new Dictionary<string, object?> { ["command"] = "git status && dotnet test" }),
            CancellationToken.None);

        Assert.True(verdict.HasNoFindings);
    }

    [Fact]
    public void NonStringCommandArg_Skipped()
    {
        var validator = new ToolCallGuardrailValidator();
        var verdict = validator.Validate(
            ToolRequest("run_shell", new Dictionary<string, object?> { ["command"] = 42 }),
            CancellationToken.None);

        Assert.True(verdict.HasNoFindings);
    }

    [Fact]
    public void NonToolCallStage_NoFindings_StageGuard()
    {
        var validator = new ToolCallGuardrailValidator();
        var verdict = validator.Validate(
            new GuardrailRequest
            {
                Stage = GuardrailStage.Input,
                Text = $"api_key = {FakeKey}",
            },
            CancellationToken.None);

        // 误投其他段：不越段裁决（工具段验证器只消费工具段负载）。
        Assert.True(verdict.HasNoFindings);
        Assert.Equal(GuardrailStage.Input, verdict.Stage);
    }

    [Fact]
    public void CancelledToken_OperationCanceled_Propagates()
    {
        var validator = new ToolCallGuardrailValidator();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            validator.Validate(ToolRequest("run_shell", null), cts.Token));
    }

    [Fact]
    public void ValidatorName_IsStable()
    {
        Assert.Equal("tool-call", new ToolCallGuardrailValidator().Name);
        Assert.Equal(ToolCallGuardrailValidator.ValidatorName, new ToolCallGuardrailValidator().Name);
    }

    // ---- 与 GuardrailPipeline 集成（注册语义：MarkOnly 默认不变）----

    [Fact]
    public void RegisteredInPipeline_MarkOnly_DestructiveFinding_DoesNotBlock()
    {
        // 默认 MarkOnly：阻断候选只标记、不拦截——验证器不改变管道默认语义。
        var pipeline = new GuardrailPipeline();
        pipeline.AddValidator(GuardrailStage.ToolCall, new ToolCallGuardrailValidator());

        var verdict = pipeline.ValidateToolCall(
            "run_shell", new Dictionary<string, object?> { ["command"] = "rm -rf /" });

        Assert.False(verdict.IsBlocked); // MarkOnly 恒不阻断
        Assert.Null(verdict.BlockReason);
        var finding = Assert.Single(verdict.Findings);
        Assert.True(finding.Blocking);   // 阻断候选如实标记，等 Enforce（组合根翻转）
    }

    [Fact]
    public void RegisteredInPipeline_Enforce_DestructiveFinding_Blocks()
    {
        // Enforce（仅组合根/设置翻转，此处显式构造验证升级路径存在）→ 阻断候选真拦截。
        var pipeline = new GuardrailPipeline(new GuardrailOptions { Mode = GuardrailMode.Enforce });
        pipeline.AddValidator(GuardrailStage.ToolCall, new ToolCallGuardrailValidator());

        var verdict = pipeline.ValidateToolCall(
            "run_shell", new Dictionary<string, object?> { ["command"] = "rm -rf /" });

        Assert.True(verdict.IsBlocked);
        Assert.NotNull(verdict.BlockReason);
    }

    [Fact]
    public void Standalone_InstantiableWithoutPipeline()
    {
        // 独立验证器契约：不依赖 GuardrailPipeline 即可单独构造、单独单测。
        IGuardrailValidator validator = new ToolCallGuardrailValidator();
        var verdict = validator.Validate(ToolRequest("run_shell", null), CancellationToken.None);
        Assert.True(verdict.HasNoFindings);
    }
}
