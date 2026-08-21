// Copyright (c) AeroCode V3.0
// Agent �?main loop (Hermes ReAct + DSH profile-aware + OpenCode toolsets).
using AeroCode.AI.Models;
using AeroCode.AI.Providers;
using AeroCode.Harness.Compaction;
using AeroCode.Harness.EventBus;
using AeroCode.Harness.Permission;
using AeroCode.Harness.PlanMode;
using AeroCode.Harness.Presets;

namespace AeroCode.Harness.Agent;

/// <summary>
/// Main agent loop. Combines:
///   - Hermes ReAct (think �?act �?observe)
///   - DSH profile-aware (preset + tools + safety)
///   - OpenCode toolsets (per-platform tool subsets)
///   - Reasonix append-only loop (messages are appended, never mutated)
///   - Hermes self-patch trigger (after 5+ tool calls)
/// </summary>
public sealed class Agent
{
    private readonly IAiProvider _provider;
    private readonly PermissionPolicy _permission;
    private readonly PlanModeManager _planMode;
    private readonly Compactor _compactor;
    private readonly EventBus.EventBus _eventBus;
    private readonly List<ChatMessage> _messages = new();
    private readonly object _lock = new();
    private Preset _activePreset;

    public string SessionId { get; }
    public IReadOnlyList<ChatMessage> Messages { get { lock (_lock) { return _messages.ToList(); } } }
    public int ToolCallCount { get; private set; }
    public Preset ActivePreset => _activePreset;
    public int MaxIterations { get; set; } = 30;
    public int MaxTokens { get; set; } = 32_000;

    public Agent(
        IAiProvider provider,
        PresetService presets,
        PermissionPolicy permission,
        PlanModeManager planMode,
        Compactor compactor,
        EventBus.EventBus eventBus,
        string? presetId = null,
        string? sessionId = null)
    {
        _provider = provider;
        _permission = permission;
        _planMode = planMode;
        _compactor = compactor;
        _eventBus = eventBus;
        _activePreset = presets.Get(presetId ?? "standard") ?? BuiltInPresets.Standard;
        SessionId = sessionId ?? Guid.NewGuid().ToString("N");
    }

    /// <summary>Switch to a different preset at runtime.</summary>
    public void SwitchPreset(string presetId, PresetService presets)
    {
        var p = presets.Get(presetId);
        if (p is null) throw new ArgumentException($"Unknown preset: {presetId}");
        _activePreset = p;
    }

    /// <summary>Inject a system prompt (Reasonix frozen-snapshot pattern).</summary>
    public void SetSystemPrompt(string systemPrompt)
    {
        lock (_lock)
        {
            // Replace or insert at index 0.
            _messages.RemoveAll(m => m.Role == "system");
            _messages.Insert(0, new ChatMessage { Role = "system", Content = systemPrompt });
        }
    }

    /// <summary>Run a single user turn (potentially multi-step with tool calls).</summary>
    public async Task<AgentTurnResult> RunAsync(string userMessage, IToolDispatcher? toolDispatcher = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userMessage))
            return new AgentTurnResult { Text = "(empty user message)", Iterations = 0 };

        // Append user message (Reasonix append-only).
        lock (_lock)
        {
            _messages.Add(new ChatMessage { Role = "user", Content = userMessage });
        }
        _eventBus.Publish(new SessionStartEvent(SessionId, DateTime.UtcNow));

        var iterations = 0;
        try
        {
            for (var i = 0; i < MaxIterations; i++)
            {
                iterations++;
                ct.ThrowIfCancellationRequested();

                // Compact if needed.
                var currentTokens = TokenCounter.ApproxTokens(Messages);
                if (_compactor.ShouldCompact(currentTokens, MaxTokens))
                {
                    var compactResult = _compactor.Compact(Messages, MaxTokens);
                    if (compactResult.DidCompact)
                    {
                        lock (_lock)
                        {
                            _messages.Clear();
                            _messages.AddRange(compactResult.Messages);
                        }
                    }
                }

                // Build request.
                var snapshot = Messages.ToList();
                var request = new ChatRequest
                {
                    Model = _activePreset.ModelRoutingStrategy,
                    Messages = snapshot,
                };

                // Call LLM.
                var startedAt = DateTime.UtcNow;
                var response = await _provider.ChatAsync(request, ct);
                var elapsedMs = (long)(DateTime.UtcNow - startedAt).TotalMilliseconds;

                if (string.IsNullOrEmpty(response.Content) && (response.ToolCalls is null || response.ToolCalls.Count == 0))
                {
                    return new AgentTurnResult { Text = "(empty response from LLM)", Iterations = iterations };
                }

                var assistantMsg = new ChatMessage
                {
                    Role = "assistant",
                    Content = response.Content ?? string.Empty,
                    ToolCalls = response.ToolCalls,
                };

                // Append assistant message.
                lock (_lock)
                {
                    _messages.Add(assistantMsg);
                }

                // Check for tool calls.
                if (response.ToolCalls is { Count: > 0 } toolCalls && toolDispatcher is not null)
                {
                    foreach (var tc in toolCalls)
                    {
                        ct.ThrowIfCancellationRequested();
                        ToolCallCount++;

                        var argsDict = ParseArgs(tc.ArgumentsJson) ?? new Dictionary<string, object?>();
                        var perm = _permission.Check(tc.FunctionName ?? "unknown", argsDict);
                        _eventBus.Publish(new ToolCallEvent(tc.FunctionName ?? "unknown", argsDict, DateTime.UtcNow));

                        if (perm.Decision == PermissionDecision.Deny)
                        {
                            // Append a denial tool result.
                            var denial = new ChatMessage
                            {
                                Role = "tool",
                                Name = tc.FunctionName,
                                Content = $"Permission denied: {perm.Reason ?? "policy"}",
                            };
                            lock (_lock) { _messages.Add(denial); }
                            continue;
                        }

                        if (perm.Decision == PermissionDecision.Ask)
                        {
                            _eventBus.Publish(new PermissionRequestedEvent(tc.FunctionName ?? "unknown", argsDict));
                            // For headless runs, default to allow. UI overrides via subscription.
                        }

                        // Dispatch.
                        object? result;
                        try
                        {
                            result = await toolDispatcher.DispatchAsync(tc.FunctionName ?? "unknown", argsDict, ct);
                        }
                        catch (Exception ex)
                        {
                            result = $"Error: {ex.Message}";
                        }

                        var toolMsg = new ChatMessage
                        {
                            Role = "tool",
                            Name = tc.FunctionName,
                            Content = result?.ToString() ?? string.Empty,
                        };
                        lock (_lock) { _messages.Add(toolMsg); }

                        _eventBus.Publish(new ToolResultEvent(
                            tc.FunctionName ?? "unknown", result, true, DateTime.UtcNow, elapsedMs));
                    }
                    // Continue the loop �?call LLM again with the tool results.
                    continue;
                }

                // No tool calls: we're done with this turn.
                _eventBus.Publish(new SessionEndEvent(SessionId, ToolCallCount, DateTime.UtcNow));
                return new AgentTurnResult
                {
                    Text = assistantMsg.Content ?? string.Empty,
                    Iterations = iterations,
                };
            }

            return new AgentTurnResult
            {
                Text = "(max iterations reached)",
                Iterations = iterations,
            };
        }
        catch (OperationCanceledException)
        {
            // 取消必须如实标记：否则调用方（如 AgentExpertExecutor）会把
            // "(cancelled)" 文本当作成功产出，取消语义被吞掉。
            return new AgentTurnResult { Text = "(cancelled)", Iterations = iterations, Cancelled = true };
        }
    }

    private static IReadOnlyDictionary<string, object?>? ParseArgs(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(json);
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>Result of a single agent turn.</summary>
public sealed class AgentTurnResult
{
    public string Text { get; init; } = string.Empty;
    public int Iterations { get; init; }
    public bool Cancelled { get; init; }
}

/// <summary>Interface for dispatching tool calls. Implemented by the App or Test layer.</summary>
public interface IToolDispatcher
{
    Task<object?> DispatchAsync(string toolName, IReadOnlyDictionary<string, object?>? args, CancellationToken ct = default);
}
