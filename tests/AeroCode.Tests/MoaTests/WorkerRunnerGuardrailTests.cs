// Copyright (c) AeroCode
// R2 修复 MED-4 验证：B5 guardrail 输入/输出段生产接点（WorkerRunner 可选注入 Guardrail）。
// 语义钉死：发现仅记录 + WARN（MarkOnly 语义）——验证有发现/Enforce 阻断性发现都不改变流程走向
//（不拦截、不改产出、不置失败）；null（默认）= 零成本跳过（现行为由既有测试覆盖）。
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AeroAgent.Conversation.Models;
using AeroAgent.Conversation.Orchestration;
using AeroAgent.Moa.Assignment;
using AeroAgent.Moa.Guard;
using AeroAgent.Moa.Profiles;
using AeroAgent.Moa.Strategies;
using AeroAgent.Moa.Tools;
using AeroCode.AI.Models;
using AeroCode.AI.Providers;
using AeroCode.Harness.EventBus;
using AeroCode.Harness.Permission;
using Microsoft.Extensions.Logging;
using Xunit;
using AiChatMessage = AeroCode.AI.Models.ChatMessage;
using ChatMessage = AeroAgent.Conversation.Models.ChatMessage;

namespace AeroCode.Tests.MoaTests;

public sealed class WorkerRunnerGuardrailTests : MoaTestBase
{
    private static readonly IReadOnlyList<AiChatMessage> Prompt = new List<AiChatMessage>
    {
        new() { Role = "user", Content = "帮我读笔记" },
    };

    /// <summary>捕获 WARN 级日志的最小 logger（断言 [GUARDRAIL]/[DEGRADED] 记录用；runner 与 pipeline 共享）。</summary>
    private sealed class RecordingLogger : ILogger
    {
        public List<string> Warnings { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        private readonly RecordingLogger _inner;

        public RecordingLogger(RecordingLogger inner) => _inner = inner;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => _inner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => _inner.IsEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            _inner.Log(logLevel, eventId, state, exception, formatter);
    }

    /// <summary>可编程验证器：按段返回固定发现（可带阻断候选），并记录收到的请求（佐证/文本观测用）。</summary>
    private sealed class StubGuardrailValidator : IGuardrailValidator
    {
        private readonly GuardrailFinding[] _findings = Array.Empty<GuardrailFinding>();
        private readonly Exception? _throwOnValidate;

        public StubGuardrailValidator(string name, params GuardrailFinding[] findings)
        {
            Name = name;
            _findings = findings;
        }

        public StubGuardrailValidator(string name, Exception throwOnValidate)
        {
            Name = name;
            _throwOnValidate = throwOnValidate;
        }

        public string Name { get; }

        public List<GuardrailRequest> Requests { get; } = new();

        public GuardrailVerdict Validate(GuardrailRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (_throwOnValidate is not null)
            {
                throw _throwOnValidate;
            }

            return new GuardrailVerdict { Stage = request.Stage, Findings = _findings };
        }
    }

    private static GuardrailFinding Finding(string validator, GuardrailStage stage, bool blocking = false) =>
        new(
            validator,
            stage,
            blocking ? GuardrailSeverity.Critical : GuardrailSeverity.Warning,
            $"{validator}.test-code",
            "测试发现：未佐证断言",
            blocking);

    private (WorkerRunner Runner, RecordingLogger Logger) MakeRunner(ToolRouter? router = null)
    {
        var logger = new RecordingLogger();
        var runner = new WorkerRunner(Sessions, Catalog, new RecordingLogger<WorkerRunner>(logger), tools: router);
        return (runner, logger);
    }

    private async Task<(OrchestrationContext Ctx, ModelAssignment Assignment)> SetupAsync(string providerId)
    {
        SetProfile(providerId, new[] { ModelStrength.General }, costPerMIn: 1.0, costPerMOut: 1.0);
        var session = await NewSessionAsync(OrchestrationStrategy.Single);
        var ctx = new OrchestrationContext
        {
            Session = session,
            History = Array.Empty<ChatMessage>(),
            UserMessageId = "msg-user",
            Providers = Registry,
        };
        return (ctx, new ModelAssignment(providerId, string.Empty, Catalog.List().First(p => p.ProviderId == providerId)));
    }

    // ---------- 直连路径（无工具） ----------

    [Fact]
    public async Task DirectPath_InputFinding_RecordedWarning_FlowUnchanged()
    {
        var logger = new RecordingLogger();
        var pipeline = new GuardrailPipeline(options: null, new RecordingLogger<GuardrailPipeline>(logger));
        var inputValidator = new StubGuardrailValidator("stub-input", Finding("stub-input", GuardrailStage.Input));
        pipeline.AddValidator(GuardrailStage.Input, inputValidator);

        var (runner, runnerLogger) = MakeRunner();
        runner.Guardrail = pipeline;

        var scripted = AddProvider("gr-input");
        scripted.ResponseQueue.Enqueue(new ChatResponse
        {
            Id = "r1", Model = string.Empty, Content = "答案", FinishReason = "stop",
            Usage = new UsageInfo { PromptTokens = 10, CompletionTokens = 5 },
        });

        var (ctx, assignment) = await SetupAsync("gr-input");
        var outcome = await runner.RunAsync(
            ctx, assignment, StrategyRole.Worker, null, null,
            Prompt, stream: false, isFinal: true, null, null, CancellationToken.None);

        // MarkOnly：只标记不拦截——流程走向与产出完全不变
        Assert.True(outcome.Succeeded);
        Assert.Equal("答案", outcome.Content);
        Assert.Equal("帮我读笔记", inputValidator.Requests[0].Text); // 输入段真实收到送入模型的用户输入

        var warning = Assert.Single(runnerLogger.Warnings, w => w.Contains("[GUARDRAIL]"));
        Assert.Contains("input findings (1)", warning);
        Assert.Contains("stub-input.test-code", warning);
        Assert.Contains("测试发现：未佐证断言", warning);
    }

    [Fact]
    public async Task DirectPath_OutputFinding_RecordedWarning_MarkOnlyNoBlocking()
    {
        var pipeline = new GuardrailPipeline();
        pipeline.AddValidator(GuardrailStage.Output, new StubGuardrailValidator(
            "stub-output", Finding("stub-output", GuardrailStage.Output)));

        var (runner, logger) = MakeRunner();
        runner.Guardrail = pipeline;

        var scripted = AddProvider("gr-output");
        scripted.ResponseQueue.Enqueue(new ChatResponse
        {
            Id = "r2", Model = string.Empty, Content = "模型产出", FinishReason = "stop",
            Usage = new UsageInfo { PromptTokens = 10, CompletionTokens = 5 },
        });

        var (ctx, assignment) = await SetupAsync("gr-output");
        var outcome = await runner.RunAsync(
            ctx, assignment, StrategyRole.Worker, null, null,
            Prompt, stream: false, isFinal: true, null, null, CancellationToken.None);

        Assert.True(outcome.Succeeded);
        Assert.Equal("模型产出", outcome.Content);
        var warning = Assert.Single(logger.Warnings, w => w.Contains("[GUARDRAIL]"));
        Assert.Contains("output findings (1)", warning);
    }

    [Fact]
    public async Task DirectPath_EnforceModeBlockingFinding_StillRecordOnly()
    {
        // 关键语义：即使 pipeline 处于 Enforce 且发现为阻断候选，输入/输出段接点也只记录——
        // 拦截升级只允许发生在设置/组合根定义的既有挂点，本接点绝不新增阻断。
        var pipeline = new GuardrailPipeline(new GuardrailOptions { Mode = GuardrailMode.Enforce });
        pipeline.AddValidator(GuardrailStage.Output, new StubGuardrailValidator(
            "stub-enforce", Finding("stub-enforce", GuardrailStage.Output, blocking: true)));

        var (runner, logger) = MakeRunner();
        runner.Guardrail = pipeline;

        var scripted = AddProvider("gr-enforce");
        scripted.ResponseQueue.Enqueue(new ChatResponse
        {
            Id = "r3", Model = string.Empty, Content = "产出", FinishReason = "stop",
            Usage = new UsageInfo { PromptTokens = 10, CompletionTokens = 5 },
        });

        var (ctx, assignment) = await SetupAsync("gr-enforce");
        var outcome = await runner.RunAsync(
            ctx, assignment, StrategyRole.Worker, null, null,
            Prompt, stream: false, isFinal: true, null, null, CancellationToken.None);

        Assert.True(outcome.Succeeded);
        Assert.Equal("产出", outcome.Content);
        Assert.Contains(logger.Warnings, w => w.Contains("[GUARDRAIL]") && w.Contains("output findings (1)"));
    }

    [Fact]
    public async Task DirectPath_ValidatorThrows_DegradedLogged_FlowContinues()
    {
        // 单验证器故障：Pipeline 内按 [DEGRADED] 跳过（不打断验证链），流程照常完成、无发现记录。
        var logger = new RecordingLogger();
        var pipeline = new GuardrailPipeline(options: null, new RecordingLogger<GuardrailPipeline>(logger));
        pipeline.AddValidator(GuardrailStage.Input, new StubGuardrailValidator(
            "stub-boom", new InvalidOperationException("validator exploded")));

        var (runner, _) = MakeRunner();
        runner.Guardrail = pipeline;

        var scripted = AddProvider("gr-boom");
        scripted.ResponseQueue.Enqueue(new ChatResponse
        {
            Id = "r4", Model = string.Empty, Content = "ok", FinishReason = "stop",
            Usage = new UsageInfo { PromptTokens = 10, CompletionTokens = 5 },
        });

        var (ctx, assignment) = await SetupAsync("gr-boom");
        var outcome = await runner.RunAsync(
            ctx, assignment, StrategyRole.Worker, null, null,
            Prompt, stream: false, isFinal: true, null, null, CancellationToken.None);

        Assert.True(outcome.Succeeded);
        Assert.Equal("ok", outcome.Content);
        Assert.DoesNotContain(logger.Warnings, w => w.Contains("[GUARDRAIL]")); // 无发现 = 无记录
        Assert.Contains(logger.Warnings, w => w.Contains("[DEGRADED]") && w.Contains("stub-boom"));
    }

    // ---------- 工具循环路径 ----------

    [Fact]
    public async Task ToolLoop_OutputValidatedWithToolEvidence_FindingRecorded()
    {
        var pipeline = new GuardrailPipeline();
        var outputValidator = new StubGuardrailValidator("stub-tool", Finding("stub-tool", GuardrailStage.Output));
        pipeline.AddValidator(GuardrailStage.Output, outputValidator);

        var box = new ScriptedToolbox("notes", new ToolDefinition { Name = "get_note", Description = "读取笔记" });
        var registry = new ToolboxRegistry();
        registry.Register(box);
        var router = new ToolRouter(registry, PermissionPolicy.CreateDefault(new EventBus()),
            new ScriptedBroker(PermissionDecision.Allow));

        var (runner, logger) = MakeRunner(router);
        runner.Guardrail = pipeline;

        var scripted = AddProvider("gr-tool");
        scripted.ResponseQueue.Enqueue(new ChatResponse
        {
            Id = "r5", Model = string.Empty, Content = string.Empty, FinishReason = "tool_calls",
            ToolCalls = new List<ToolCall>
            {
                new() { Id = "c1", Type = "function", FunctionName = "get_note", ArgumentsJson = "{}" },
            },
            Usage = new UsageInfo { PromptTokens = 10, CompletionTokens = 5 },
        });
        scripted.ResponseQueue.Enqueue(new ChatResponse
        {
            Id = "r6", Model = string.Empty, Content = "最终答复", FinishReason = "stop",
            Usage = new UsageInfo { PromptTokens = 20, CompletionTokens = 10 },
        });
        box.SetResult("get_note", ToolInvokeResult.Ok("NOTE_BODY"));

        var (ctx, assignment) = await SetupAsync("gr-tool");
        var outcome = await runner.RunAsync(
            ctx, assignment, StrategyRole.Worker, null, null,
            Prompt, stream: false, isFinal: true, null, null, CancellationToken.None);

        // 佐证 = 工具输出（C2 evidence 同源），输出段真实收到最终答复与佐证
        Assert.True(outcome.Succeeded);
        Assert.Equal("最终答复", outcome.Content);
        var outputRequest = Assert.Single(outputValidator.Requests);
        Assert.Equal("最终答复", outputRequest.Text);
        Assert.Contains("NOTE_BODY", Assert.Single(outputRequest.GroundedEvidence));

        var warning = Assert.Single(logger.Warnings, w => w.Contains("[GUARDRAIL]"));
        Assert.Contains("output findings (1)", warning);
        Assert.Contains("stub-tool.test-code", warning);
    }

    [Fact]
    public async Task GuardrailNotInjected_CurrentBehaviorUnchanged_NoGuardrailCalls()
    {
        // null（默认）= 现行为：不构建 pipeline、零验证调用（行为由既有测试覆盖，此处钉住无 guardrail 日志）。
        var (runner, logger) = MakeRunner();

        var scripted = AddProvider("gr-null");
        scripted.ResponseQueue.Enqueue(new ChatResponse
        {
            Id = "r7", Model = string.Empty, Content = "done", FinishReason = "stop",
            Usage = new UsageInfo { PromptTokens = 10, CompletionTokens = 5 },
        });

        var (ctx, assignment) = await SetupAsync("gr-null");
        var outcome = await runner.RunAsync(
            ctx, assignment, StrategyRole.Worker, null, null,
            Prompt, stream: false, isFinal: true, null, null, CancellationToken.None);

        Assert.True(outcome.Succeeded);
        Assert.Equal("done", outcome.Content);
        Assert.DoesNotContain(logger.Warnings, w => w.Contains("[GUARDRAIL]"));
        Assert.DoesNotContain(logger.Warnings, w => w.Contains("[DEGRADED]"));
    }
}
