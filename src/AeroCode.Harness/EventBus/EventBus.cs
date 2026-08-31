// Copyright (c) AeroCode V3.0
// EventBus — pub/sub for cross-module decoupling (DSH-style events).
namespace AeroCode.Harness.EventBus;

/// <summary>
/// Simple in-process pub/sub for cross-module events (DSH ctx.on pattern).
/// Used to decouple tool calls, memory updates, plan-mode changes, etc.
/// </summary>
public sealed class EventBus
{
    private readonly Dictionary<Type, List<Delegate>> _subscribers = new();
    private readonly object _lock = new();

    /// <summary>Subscribe to events of type T. Returns an unsubscribe handle.</summary>
    public Action Subscribe<T>(Action<T> handler) where T : class
    {
        lock (_lock)
        {
            if (!_subscribers.TryGetValue(typeof(T), out var list))
            {
                list = new List<Delegate>();
                _subscribers[typeof(T)] = list;
            }
            list.Add(handler);
        }
        return () =>
        {
            lock (_lock)
            {
                if (_subscribers.TryGetValue(typeof(T), out var list))
                    list.Remove(handler);
            }
        };
    }

    /// <summary>Publish an event to all subscribers.</summary>
    public void Publish<T>(T evt) where T : class
    {
        List<Delegate> snapshot;
        lock (_lock)
        {
            if (!_subscribers.TryGetValue(typeof(T), out var list)) return;
            snapshot = list.ToList();
        }
        foreach (var d in snapshot)
        {
            try { ((Action<T>)d)(evt); }
            catch { /* swallow — events must not break the publisher */ }
        }
    }
}

// === Event types ===

public sealed record ToolCallEvent(string ToolName, IReadOnlyDictionary<string, object?> Args, DateTime Utc);
public sealed record ToolResultEvent(string ToolName, object? Result, bool Success, DateTime Utc, long ElapsedMs);
public sealed record SkillLoadedEvent(string SkillId, DateTime Utc);
public sealed record MemoryUpdatedEvent(string Layer, string Operation, DateTime Utc);
public sealed record PlanModeChangedEvent(bool Enabled, DateTime Utc);
public sealed record SessionStartEvent(string SessionId, DateTime Utc);
public sealed record SessionEndEvent(string SessionId, int TotalToolCalls, DateTime Utc);
public sealed record PermissionRequestedEvent(string ToolName, IReadOnlyDictionary<string, object?> Args);
public sealed record CompactionTriggeredEvent(int OriginalTokens, int CompactedTokens, DateTime Utc);
// ---- 批次 B 新事件（G0 契约钉死）----
public sealed record SubAgentCompletedEvent(string SubAgentId, string Summary, double CostUsd, bool Success, DateTime Utc);
public sealed record ApprovalCircuitBrokenEvent(string SessionId, string Reason, DateTime Utc);
public sealed record SteerRequestedEvent(string SessionId, string Text, DateTime Utc);
public sealed record HookExecutedEvent(string HookId, string EventName, bool Success, int ElapsedMs, DateTime Utc);
public sealed record EtopTrippedEvent(string Reason, DateTime Utc);
