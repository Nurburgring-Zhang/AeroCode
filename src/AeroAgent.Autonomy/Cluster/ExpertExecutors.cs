// Copyright (c) AeroCode V3.0
// Production IExpertExecutor implementations.
//  - AgentExpertExecutor: drives each attempt through a HarnessHost sub-agent
//    (real provider loop, role + skill catalog injection).
//  - FacadeExpertExecutor: drives each attempt through the Conversation
//    orchestration facade (real session + MOA strategy event stream).
// Both capture their own exceptions into failed outcomes (contract requirement).
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AeroAgent.Conversation.Models;
using AeroAgent.Conversation.Orchestration;
using AeroAgent.Conversation.Services;
using AeroCode.AI.Providers;
using AeroCode.Harness;
using AeroCode.Skills;

namespace AeroAgent.Autonomy.Cluster;

/// <summary>
/// Expert executor backed by HarnessHost sub-agents: every attempt runs a real
/// agent loop (provider + presets + permission policy), with the expert's role and
/// the skill catalog injected into the system prompt.
/// </summary>
public sealed class AgentExpertExecutor : IExpertExecutor
{
    private readonly HarnessHost _host;
    private readonly IAiProvider _provider;
    private readonly SkillHub? _skills;

    public AgentExpertExecutor(HarnessHost host, IAiProvider provider, SkillHub? skills = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _skills = skills;
    }

    /// <inheritdoc/>
    public async Task<ExpertExecutionOutcome> ExecuteAsync(ExpertExecutionContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var sw = Stopwatch.StartNew();
        try
        {
            var agent = _host.CreateAgent(
                _provider,
                sessionId: string.IsNullOrWhiteSpace(context.ExpertSessionId) ? null : context.ExpertSessionId,
                role: context.Role,
                skills: _skills);

            var result = await agent.RunAsync(ComposePrompt(context), toolDispatcher: null, ct);
            sw.Stop();

            if (result.Cancelled)
            {
                return new ExpertExecutionOutcome(false, true, false, string.Empty, null, sw.Elapsed.TotalMilliseconds);
            }

            if (string.IsNullOrWhiteSpace(result.Text))
            {
                return new ExpertExecutionOutcome(false, false, false, string.Empty,
                    "agent produced no output", sw.Elapsed.TotalMilliseconds);
            }

            return new ExpertExecutionOutcome(true, false, false, result.Text, null, sw.Elapsed.TotalMilliseconds);
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            return new ExpertExecutionOutcome(false, true, false, string.Empty, null, sw.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new ExpertExecutionOutcome(false, false, false, string.Empty,
                $"{ex.GetType().Name}: {ex.Message}", sw.Elapsed.TotalMilliseconds);
        }
    }

    internal static string ComposePrompt(ExpertExecutionContext context)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[{context.AttemptKind} attempt #{context.FanOutIndex}] 节点 {context.NodeId}（{context.NodeName}）");
        if (!string.IsNullOrWhiteSpace(context.MemorySnapshot))
        {
            sb.AppendLine();
            sb.AppendLine("## 你的持久记忆（以往任务沉淀）");
            sb.AppendLine(context.MemorySnapshot);
        }

        sb.AppendLine();
        sb.AppendLine("## 本次任务");
        sb.AppendLine(context.TaskText);
        sb.AppendLine();
        sb.AppendLine("要求：产出与任务直接对应的可检验成果，做不到如实说明，禁止编造。");
        return sb.ToString();
    }
}

/// <summary>
/// Expert executor backed by the Conversation orchestration facade: every attempt
/// runs through a real session with the expert's MOA strategy and the full event
/// stream (assistant messages, deltas, costs) aggregated into the deliverable.
/// Expert sessions are created lazily once per expert and reused afterwards.
/// </summary>
public sealed class FacadeExpertExecutor : IExpertExecutor
{
    private readonly ISessionService _sessions;
    private readonly IChatOrchestrationFacade _facade;
    private readonly OrchestrationStrategy _strategy;
    private readonly ConcurrentDictionary<string, string> _expertSessions = new();

    public FacadeExpertExecutor(
        ISessionService sessions,
        IChatOrchestrationFacade facade,
        OrchestrationStrategy strategy = OrchestrationStrategy.Single)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _facade = facade ?? throw new ArgumentNullException(nameof(facade));
        _strategy = strategy;
    }

    /// <inheritdoc/>
    public async Task<ExpertExecutionOutcome> ExecuteAsync(ExpertExecutionContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var sw = Stopwatch.StartNew();
        try
        {
            var sessionId = await ResolveSessionAsync(context, ct);

            var contents = new Dictionary<string, StringBuilder>();
            var order = new List<string>();
            string? failure = null;
            var cancelled = false;

            await foreach (var ev in _facade.SendAsync(sessionId, AgentExpertExecutor.ComposePrompt(context), ct))
            {
                switch (ev)
                {
                    case AssistantMessageStarted started:
                        if (!contents.ContainsKey(started.MessageId))
                        {
                            contents[started.MessageId] = new StringBuilder();
                            order.Add(started.MessageId);
                        }
                        break;

                    case TextDeltaEvent delta:
                        if (contents.TryGetValue(delta.MessageId, out var sb))
                        {
                            sb.Append(delta.Delta);
                        }
                        break;

                    case MessageFailedEvent failed:
                        failure ??= failed.Error;
                        break;

                    case MessageCancelledEvent:
                        cancelled = true;
                        break;
                }
            }

            sw.Stop();
            var finalContent = CollectLastNonEmpty(contents, order);

            if (cancelled)
            {
                return new ExpertExecutionOutcome(false, true, false, finalContent, null, sw.Elapsed.TotalMilliseconds);
            }

            if (failure is not null)
            {
                return new ExpertExecutionOutcome(false, false, false, finalContent, failure, sw.Elapsed.TotalMilliseconds);
            }

            if (finalContent.Length == 0)
            {
                return new ExpertExecutionOutcome(false, false, false, string.Empty,
                    "orchestration produced no assistant content", sw.Elapsed.TotalMilliseconds);
            }

            return new ExpertExecutionOutcome(true, false, false, finalContent, null, sw.Elapsed.TotalMilliseconds);
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            return new ExpertExecutionOutcome(false, true, false, string.Empty, null, sw.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new ExpertExecutionOutcome(false, false, false, string.Empty,
                $"{ex.GetType().Name}: {ex.Message}", sw.Elapsed.TotalMilliseconds);
        }
    }

    private async Task<string> ResolveSessionAsync(ExpertExecutionContext context, CancellationToken ct)
    {
        if (_expertSessions.TryGetValue(context.ExpertId, out var cached))
        {
            return cached;
        }

        if (!string.IsNullOrWhiteSpace(context.ExpertSessionId))
        {
            var existing = await _sessions.GetSessionAsync(context.ExpertSessionId);
            if (existing.IsSuccess && existing.Value is not null)
            {
                _expertSessions[context.ExpertId] = context.ExpertSessionId;
                return context.ExpertSessionId;
            }
        }

        var created = await _sessions.CreateSessionAsync(
            strategy: _strategy,
            title: $"Expert {context.ExpertId} ({context.Role})");
        if (!created.IsSuccess || created.Value is null)
        {
            throw new InvalidOperationException($"failed to create expert session: {created.Error ?? "unknown"}");
        }

        _expertSessions[context.ExpertId] = created.Value.Id;
        return created.Value.Id;
    }

    private static string CollectLastNonEmpty(Dictionary<string, StringBuilder> contents, List<string> order)
    {
        for (var i = order.Count - 1; i >= 0; i--)
        {
            var text = contents[order[i]].ToString().Trim();
            if (text.Length > 0)
            {
                return text;
            }
        }

        return string.Empty;
    }
}
