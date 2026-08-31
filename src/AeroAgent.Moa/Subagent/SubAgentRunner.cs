// Copyright (c) AeroCode
// 子代理契约（批次 B G1 契约钉死）+ SubAgentRunner 实现（builder-α）。
// 对标 opencode task.ts / claude-code [逆 08/21]：独立上下文、权限继承、完成回注、深度硬上限。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AeroAgent.Conversation.Models;
using AeroAgent.Conversation.Services;
using AeroAgent.Moa.Accounting;
using AeroAgent.Moa.Assignment;
using AeroAgent.Moa.Profiles;
using AeroAgent.Moa.Tools;
using AeroAgent.Moa.Strategies;
using AeroCode.AI.Models;
using AeroCode.AI.Providers;
using AeroCode.Harness.EventBus;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using AiChatMessage = AeroCode.AI.Models.ChatMessage;
using ConvChatMessage = AeroAgent.Conversation.Models.ChatMessage;

namespace AeroAgent.Moa.Subagent;

/// <summary>子代理规格（不可变）。深度 ≤ <see cref="MaxDepth"/>（硬上限，层数含自身）。</summary>
public sealed record SubAgentSpec(
    string Description,
    string Prompt,
    string ProviderId,
    string Model,
    int Depth,
    int MaxTurns,
    double MaxCostUsd,
    bool ParallelSafe)
{
    /// <summary>深度硬上限（对标 claude-code 子 agent 深度限制；超限的派发在入口拒绝）。</summary>
    public const int MaxDepth = 4;
}

/// <summary>运行中的子代理句柄（可等待/取消）。</summary>
public interface ISubAgentHandle : IAsyncDisposable
{
    string Id { get; }
    SubAgentSpec Spec { get; }
    /// <summary>等待完成；返回汇总文本（诚实失败时为错误说明）。</summary>
    Task<string> WaitAsync(CancellationToken ct);
    /// <summary>取消运行（幂等）。</summary>
    void Cancel();
}

/// <summary>子代理启动器。实现约束：
/// 1. 独立会话上下文（不复用父消息历史，仅带 spec.Prompt）；
/// 2. 权限显式继承（同一 PermissionPolicy 实例——策略与档位全局一致）；
/// 3. 完成时 Publish <see cref="SubAgentCompletedEvent"/>（成本真实核算，未知不估算）；
/// 4. Depth ≥ MaxDepth 的派发请求直接诚实失败；
/// 5. 并行实例数受设置上限（Settings.Subagent.MaxParallel）约束，超限排队。
/// </summary>
public interface ISubAgentLauncher
{
    /// <summary>当前活跃子代理数（诊断）。</summary>
    int ActiveCount { get; }

    /// <summary>派发一个子代理；深度超限/预算非法时抛 ArgumentException（诚实失败）。</summary>
    Task<ISubAgentHandle> LaunchAsync(SubAgentSpec spec, CancellationToken ct);
}

/// <summary>
/// 子代理启动器的真实实现。每个派发的子代理：
/// 独立真实会话（不复用父消息历史）→ 独立工具循环（模式源自 <see cref="WorkerRunner"/>，
/// 见下注）→ 真实 usage/成本逐轮核算 → 完成时向 <see cref="EventBus"/> 发布
/// <see cref="SubAgentCompletedEvent"/>（汇总文本 + 真实成本；未知价格如实为 0，不估算）。
/// 工具调用经由注入的 <see cref="ToolRouter"/> 执行——与父代理同一策略/守卫/授权代理
/// 实例，权限继承显式且无旁路。并行实例数受 <see cref="SubagentOptions.MaxParallel"/>
/// 信号量约束，超限派发排队等待空闲槽位；取消对排队/运行中均生效。
/// </summary>
/// <remarks>
/// 工具循环为本类自有实现（逐段对照 WorkerRunner.RunToolLoopAsync 的结构：占位消息、
/// 非流式多轮、tool_calls 配对落库、真实成本、诚实失败终态）。不直接复用 WorkerRunner
/// 的原因：其循环上限是常量（WorkerRunner.MaxToolTurns = 8），无法按
/// <see cref="SubAgentSpec.MaxTurns"/> 契约字段逐派发裁剪；且 WorkerRunner 不在
/// builder-α 所有权内，批次 B 不修改。事件面：子代理运行静默（无 sink），
/// 只在完成/失败/取消时发布一条 SubAgentCompletedEvent 回注父流程。
/// </remarks>
public sealed class SubAgentRunner : ISubAgentLauncher
{
    private readonly ISessionService _sessions;
    private readonly IProviderRegistry _providers;
    private readonly IModelProfileCatalog _catalog;
    private readonly EventBus _events;
    private readonly SubagentOptions _options;
    private readonly ToolRouter? _tools;
    private readonly ILogger<SubAgentRunner> _logger;
    private readonly SemaphoreSlim _slots;
    private int _active;

    public SubAgentRunner(
        ISessionService sessions,
        IProviderRegistry providers,
        IModelProfileCatalog catalog,
        EventBus events,
        SubagentOptions? options = null,
        ToolRouter? tools = null,
        ILogger<SubAgentRunner>? logger = null)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _providers = providers ?? throw new ArgumentNullException(nameof(providers));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _options = options ?? new SubagentOptions();
        _tools = tools;
        _logger = logger ?? NullLogger<SubAgentRunner>.Instance;

        if (_options.MaxParallel < 1)
        {
            throw new ArgumentException("MaxParallel must be >= 1", nameof(options));
        }

        _slots = new SemaphoreSlim(_options.MaxParallel, _options.MaxParallel);
    }

    /// <inheritdoc/>
    public int ActiveCount => Volatile.Read(ref _active);

    /// <inheritdoc/>
    public Task<ISubAgentHandle> LaunchAsync(SubAgentSpec spec, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(spec);

        // ---- 入口校验：全部诚实失败，不做静默修正 ----
        if (!_options.Enabled)
        {
            throw new InvalidOperationException(
                "subagent dispatch is disabled by settings (Subagent.Enabled = false)");
        }

        if (string.IsNullOrWhiteSpace(spec.Description))
        {
            throw new ArgumentException("subagent spec requires a non-empty Description", nameof(spec));
        }

        if (string.IsNullOrWhiteSpace(spec.Prompt))
        {
            throw new ArgumentException("subagent spec requires a non-empty Prompt", nameof(spec));
        }

        if (string.IsNullOrWhiteSpace(spec.ProviderId))
        {
            throw new ArgumentException("subagent spec requires a non-empty ProviderId", nameof(spec));
        }

        if (spec.Depth < 1)
        {
            throw new ArgumentException(
                $"subagent depth must be >= 1, got {spec.Depth}", nameof(spec));
        }

        var maxDepth = _options.EffectiveMaxDepth;
        if (spec.Depth > maxDepth)
        {
            throw new ArgumentException(
                $"subagent depth {spec.Depth} exceeds the hard limit {maxDepth} " +
                "(depth counts the subagent itself)", nameof(spec));
        }

        if (spec.MaxTurns < 1)
        {
            throw new ArgumentException(
                $"subagent MaxTurns must be >= 1, got {spec.MaxTurns}", nameof(spec));
        }

        if (double.IsNaN(spec.MaxCostUsd) || double.IsPositiveInfinity(spec.MaxCostUsd) || spec.MaxCostUsd < 0)
        {
            throw new ArgumentException(
                $"subagent MaxCostUsd must be a finite non-negative value, got {spec.MaxCostUsd}",
                nameof(spec));
        }

        // TurnBudget 对 <=0 抛 ArgumentOutOfRangeException（ArgumentException 子类）——契约口径一致。
        // MaxCostUsd == 0 语义：无计价上限（未知价格不估算，0 不构成可用预算约束）。
        var budget = new TurnBudget(spec.MaxCostUsd == 0 ? null : spec.MaxCostUsd);

        var run = new SubAgentRun(spec, budget);
        run.Start(ExecuteAsync(run, parentCt: ct));
        return Task.FromResult<ISubAgentHandle>(run);
    }

    /// <summary>执行主体：排队取槽 → 建独立会话 → 工具循环 → 发布完成事件。永不抛出。</summary>
    private async Task<string> ExecuteAsync(SubAgentRun run, CancellationToken parentCt)
    {
        var spec = run.Spec;
        try
        {
            // ---- 并行上限排队：无空闲槽位时在此等待；排队中取消直接诚实收场 ----
            try
            {
                await _slots.WaitAsync(run.Cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                PublishCompleted(run, summary: "cancelled before start（排队等待槽位时被取消）",
                    costUsd: 0, success: false);
                return "cancelled before start（排队等待槽位时被取消）";
            }

            Interlocked.Increment(ref _active);
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(parentCt, run.Cts.Token);
                var ct = linked.Token;

                // ---- 独立会话上下文：新真实会话，只带 spec.Prompt，不带父历史 ----
                var created = await _sessions.CreateSessionAsync(
                    OrchestrationStrategy.Single,
                    title: $"[subagent] {spec.Description}").ConfigureAwait(false);
                if (!created.IsSuccess)
                {
                    var error = created.Error ?? "create subagent session failed";
                    return FailRun(run, error, costUsd: 0);
                }

                run.SessionId = created.Value!.Id;

                // ---- provider 与画像：解析失败诚实收场（不冒充其他模型）----
                IAiProvider provider;
                ModelAssignment assignment;
                try
                {
                    provider = _providers.Get(spec.ProviderId);
                    assignment = new ModelAssignment(
                        spec.ProviderId, spec.Model, _catalog.GetOrAddDefault(spec.ProviderId, spec.Model));
                }
                catch (Exception ex)
                {
                    return FailRun(run, $"provider '{spec.ProviderId}' unavailable: {ex.Message}", costUsd: 0);
                }

                var (summary, success, costUsd) = await RunLoopAsync(
                    run, provider, assignment, ct).ConfigureAwait(false);
                PublishCompleted(run, summary, costUsd, success);
                return summary;
            }
            finally
            {
                Interlocked.Decrement(ref _active);
                _slots.Release();
            }
        }
        catch (Exception ex)
        {
            // 兜底：任何未预期异常都不允许逃出执行任务（WaitAsync 契约：返回错误说明而非抛出）。
            _logger.LogWarning("subagent {Id} crashed: {Error}", run.Id, ex.Message);
            var summary = $"subagent crashed: {ex.Message}";
            PublishCompleted(run, summary, costUsd: 0, success: false);
            return summary;
        }
    }

    /// <summary>
    /// 工具循环（结构对照 WorkerRunner.RunToolLoopAsync）：占位消息 → 非流式多轮 →
    /// tool_calls 配对落库并回灌 → 逐轮真实成本核算（逐轮计入预算）→
    /// spec.MaxTurns 上限诚实中止。返回（汇总文本, 成功, 累计成本）。
    /// </summary>
    private async Task<(string Summary, bool Success, double CostUsd)> RunLoopAsync(
        SubAgentRun run,
        IAiProvider provider,
        ModelAssignment assignment,
        CancellationToken ct)
    {
        var spec = run.Spec;
        var sessionId = run.SessionId!;
        var finalMessage = new ConvChatMessage
        {
            SessionId = sessionId,
            Role = ChatRole.Assistant,
            ProviderId = assignment.ProviderId,
            ModelId = assignment.ModelId,
            OrchestrationRole = StrategyRole.Worker,
            Label = $"subagent:{spec.Description}",
            IsFinal = false, // 编排中间产物语义：不回灌父上下文，仅供审计与回溯
            Status = MessageStatus.Streaming,
        };
        var appended = await _sessions.AppendMessageAsync(finalMessage).ConfigureAwait(false);
        if (!appended.IsSuccess)
        {
            return (appended.Error ?? "persist failed", false, 0);
        }

        var definitions = _tools is { HasTools: true } ? _tools.Definitions : null;
        var conversation = new List<AiChatMessage>
        {
            new() { Role = "user", Content = spec.Prompt },
        };
        var runSw = Stopwatch.StartNew();
        var totalCost = 0.0;

        try
        {
            for (var turn = 0; ; turn++)
            {
                if (turn >= spec.MaxTurns)
                {
                    var error = $"tool-call loop exceeded the limit ({spec.MaxTurns} turns), aborted";
                    _logger.LogWarning(
                        "subagent {Id} aborted: hit {MaxTurns} turns", run.Id, spec.MaxTurns);
                    return await FailRunPersistedAsync(
                        run, finalMessage, assignment, error, totalCost, (int)runSw.ElapsedMilliseconds,
                        countInStats: true).ConfigureAwait(false);
                }

                // ---- 预算闸门：逐轮检查（此前各轮的真实花费已计入）----
                if (!run.Budget.HasBudget)
                {
                    var error = $"budget exceeded: spent ${run.Budget.SpentUsd:F6}, limit ${run.Budget.MaxUsd:F6}";
                    return await FailRunPersistedAsync(
                        run, finalMessage, assignment, error, totalCost, (int)runSw.ElapsedMilliseconds,
                        countInStats: false).ConfigureAwait(false);
                }

                var turnSw = Stopwatch.StartNew();
                var request = new ChatRequest
                {
                    Model = assignment.ModelId,
                    Messages = conversation,
                    Tools = definitions,
                    Stream = false, // 工具轮必须非流式：拿到完整 tool_calls 才能配对执行
                };
                var response = await provider.ChatAsync(request, ct).ConfigureAwait(false);
                var turnLatency = (int)turnSw.ElapsedMilliseconds;

                var turnTokensIn = response.Usage?.PromptTokens ?? 0;
                var turnTokensOut = response.Usage?.CompletionTokens ?? 0;
                var turnCost = CostTracker.Estimate(assignment.Profile, turnTokensIn, turnTokensOut) ?? 0.0;
                totalCost += turnCost;
                run.Budget.AddActual(turnCost);

                if (response.ToolCalls.Count == 0)
                {
                    // ---- 最终答复：写回占位消息，真实用量/成本落库 ----
                    runSw.Stop();
                    finalMessage.Content = response.Content;
                    finalMessage.Status = MessageStatus.Completed;
                    finalMessage.TokensIn = turnTokensIn;
                    finalMessage.TokensOut = turnTokensOut;
                    finalMessage.CostUsd = turnCost;
                    finalMessage.LatencyMs = turnLatency;
                    await _sessions.UpdateMessageAsync(finalMessage).ConfigureAwait(false);

                    _catalog.RecordUsage(assignment.ProviderId, assignment.ModelId, turnLatency, failed: false);
                    await SaveCatalogQuietlyAsync().ConfigureAwait(false);

                    var summary = string.IsNullOrWhiteSpace(response.Content)
                        ? "（子代理返回空答复）"
                        : response.Content;
                    return (summary, true, totalCost);
                }

                // ---- 工具轮：助手 tool_calls 消息落库（IsFinal=false，配对回灌用）----
                var turnMessage = new ConvChatMessage
                {
                    SessionId = sessionId,
                    Role = ChatRole.Assistant,
                    ProviderId = assignment.ProviderId,
                    ModelId = assignment.ModelId,
                    OrchestrationRole = StrategyRole.Worker,
                    ParentMessageId = finalMessage.Id,
                    Label = finalMessage.Label,
                    Content = response.Content,
                    ToolCallsJson = JsonSerializer.Serialize(response.ToolCalls),
                    IsFinal = false,
                    Status = MessageStatus.Completed,
                    TokensIn = turnTokensIn,
                    TokensOut = turnTokensOut,
                    CostUsd = turnCost,
                    LatencyMs = turnLatency,
                };
                var appendedTurn = await _sessions.AppendMessageAsync(turnMessage).ConfigureAwait(false);
                if (!appendedTurn.IsSuccess)
                {
                    return await FailRunPersistedAsync(
                        run, finalMessage, assignment, appendedTurn.Error ?? "persist failed",
                        totalCost, (int)runSw.ElapsedMilliseconds, countInStats: true).ConfigureAwait(false);
                }

                conversation.Add(new AiChatMessage
                {
                    Role = "assistant",
                    Content = response.Content,
                    ToolCalls = response.ToolCalls,
                });

                // ---- 逐个执行工具调用：先裁决后执行（继承的 ToolRouter 内含策略/守卫/授权），结果如实回传 ----
                foreach (var call in response.ToolCalls)
                {
                    var toolMessage = new ConvChatMessage
                    {
                        SessionId = sessionId,
                        Role = ChatRole.Tool,
                        ProviderId = assignment.ProviderId,
                        ModelId = assignment.ModelId,
                        OrchestrationRole = StrategyRole.Worker,
                        ParentMessageId = turnMessage.Id,
                        Name = call.FunctionName,
                        ToolCallId = call.Id,
                        IsFinal = false,
                        Status = MessageStatus.Pending,
                    };
                    var appendedTool = await _sessions.AppendMessageAsync(toolMessage).ConfigureAwait(false);
                    if (!appendedTool.IsSuccess)
                    {
                        return await FailRunPersistedAsync(
                            run, finalMessage, assignment, appendedTool.Error ?? "persist failed",
                            totalCost, (int)runSw.ElapsedMilliseconds, countInStats: true).ConfigureAwait(false);
                    }

                    var toolResult = _tools is null
                        ? ToolInvokeResult.Fail($"Tool '{call.FunctionName}' not found: no tool router attached")
                        : await _tools.InvokeAsync(call.FunctionName, call.ArgumentsJson, ct).ConfigureAwait(false);

                    toolMessage.Content = toolResult.Output;
                    toolMessage.Status = toolResult.Success ? MessageStatus.Completed : MessageStatus.Degraded;
                    toolMessage.Error = toolResult.Success ? null : toolResult.Error;
                    await _sessions.UpdateMessageAsync(toolMessage).ConfigureAwait(false);

                    conversation.Add(new AiChatMessage
                    {
                        Role = "tool",
                        Content = toolResult.Output,
                        Name = call.FunctionName,
                        ToolCallId = call.Id,
                    });
                }
            }
        }
        catch (OperationCanceledException)
        {
            runSw.Stop();
            finalMessage.Status = MessageStatus.Cancelled;
            finalMessage.LatencyMs = (int)runSw.ElapsedMilliseconds;
            await _sessions.UpdateMessageAsync(finalMessage).ConfigureAwait(false);

            // 与 WorkerRunner 取消语义一致：不计画像统计、不产生成本（未计价不猜）。
            return ("cancelled by user", false, 0);
        }
        catch (Exception ex)
        {
            runSw.Stop();
            var error = ErrorText.Truncate(ex.Message) ?? ex.Message;
            _logger.LogWarning(
                "subagent {Id} tool loop failed after {LatencyMs}ms: {Error}",
                run.Id, (int)runSw.ElapsedMilliseconds, error);
            return await FailRunPersistedAsync(
                run, finalMessage, assignment, error, totalCost, (int)runSw.ElapsedMilliseconds,
                countInStats: true).ConfigureAwait(false);
        }
    }

    /// <summary>失败收尾：占位消息落 Failed 终态 + 画像统计（可选）。</summary>
    private async Task<(string Summary, bool Success, double CostUsd)> FailRunPersistedAsync(
        SubAgentRun run,
        ConvChatMessage finalMessage,
        ModelAssignment assignment,
        string error,
        double costUsd,
        int latencyMs,
        bool countInStats)
    {
        finalMessage.Status = MessageStatus.Failed;
        finalMessage.Error = error;
        finalMessage.LatencyMs = latencyMs;
        await _sessions.UpdateMessageAsync(finalMessage).ConfigureAwait(false);

        if (countInStats)
        {
            _catalog.RecordUsage(assignment.ProviderId, assignment.ModelId, latencyMs, failed: true);
            await SaveCatalogQuietlyAsync().ConfigureAwait(false);
        }

        return (error, false, costUsd);
    }

    private string FailRun(SubAgentRun run, string error, double costUsd)
    {
        PublishCompleted(run, error, costUsd, success: false);
        return error;
    }

    private void PublishCompleted(SubAgentRun run, string summary, double costUsd, bool success)
    {
        _events.Publish(new SubAgentCompletedEvent(
            run.Id,
            ErrorText.Truncate(summary) ?? summary,
            costUsd,
            success,
            DateTime.UtcNow));
    }

    private async Task SaveCatalogQuietlyAsync()
    {
        try
        {
            await _catalog.SaveAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 统计持久化失败不影响主流程（下次保存会覆盖），但降级必须可见。
            _logger.LogWarning("[DEGRADED] failed to persist model profile stats: {Error}", ex.Message);
        }
    }

    /// <summary>运行中的子代理句柄：Wait 等待完成，Cancel 幂等取消（排队/运行中均生效）。</summary>
    private sealed class SubAgentRun : ISubAgentHandle
    {
        private readonly TaskCompletionSource<string> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public SubAgentRun(SubAgentSpec spec, TurnBudget budget)
        {
            Spec = spec ?? throw new ArgumentNullException(nameof(spec));
            Budget = budget ?? throw new ArgumentNullException(nameof(budget));
            Id = $"sub-{Guid.NewGuid():N}";
            Cts = new CancellationTokenSource();
        }

        public string Id { get; }

        public SubAgentSpec Spec { get; }

        public TurnBudget Budget { get; }

        public CancellationTokenSource Cts { get; }

        /// <summary>独立会话 Id（会话创建后填充；诊断用）。</summary>
        public string? SessionId { get; set; }

        /// <summary>启动执行任务，把其结果回填给 WaitAsync。</summary>
        public void Start(Task<string> execution)
        {
            // ExecuteAsync 已收容一切异常；此续接只是把结果搬进 TaskCompletionSource。
            _ = execution.ContinueWith(
                static (t, state) =>
                {
                    var completion = (TaskCompletionSource<string>)state!;
                    if (t.IsCompletedSuccessfully)
                    {
                        completion.TrySetResult(t.Result);
                    }
                    else if (t.IsFaulted)
                    {
                        var error = t.Exception?.GetBaseException().Message ?? "unknown failure";
                        completion.TrySetResult($"subagent crashed: {error}");
                    }
                    else
                    {
                        completion.TrySetResult("cancelled by user");
                    }
                },
                _completion,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        /// <inheritdoc/>
        public Task<string> WaitAsync(CancellationToken ct) => _completion.Task.WaitAsync(ct);

        /// <inheritdoc/>
        public void Cancel() => Cts.Cancel();

        /// <inheritdoc/>
        public async ValueTask DisposeAsync()
        {
            Cts.Dispose();
            await Task.CompletedTask.ConfigureAwait(false);
        }
    }
}
