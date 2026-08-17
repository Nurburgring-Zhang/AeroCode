// Copyright (c) AeroCode V3.0
// PlanMode — DSH-style write protection. All write operations become pending.
using AeroCode.Harness.EventBus;

namespace AeroCode.Harness.PlanMode;

/// <summary>Whether a write operation is allowed or pending approval.</summary>
public enum WriteState
{
    /// <summary>Write was applied immediately.</summary>
    Applied,
    /// <summary>Write is pending user approval (PlanMode is on).</summary>
    PendingApproval,
    /// <summary>Write was rejected by the user or by the policy.</summary>
    Rejected,
}

/// <summary>A pending write operation that the user must approve or reject.</summary>
public sealed class PendingEdit
{
    public required string Id { get; init; }
    public required string ToolName { get; init; }
    public required string FilePath { get; init; }
    public required string Content { get; init; }
    public string? OriginalContent { get; init; }
    public string? Description { get; init; }
    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Plan mode manager (DSH + Reasonix /plan pattern).
/// When enabled, all write operations return PendingEdit instead of writing immediately.
/// </summary>
public sealed class PlanModeManager
{
    private readonly EventBus.EventBus _eventBus;
    private readonly List<PendingEdit> _pending = new();
    private readonly object _lock = new();

    public PlanModeManager(EventBus.EventBus eventBus) { _eventBus = eventBus; }

    /// <summary>Whether plan mode is currently active.</summary>
    public bool IsEnabled { get; private set; }

    /// <summary>Number of pending edits awaiting approval.</summary>
    public int PendingCount { get { lock (_lock) { return _pending.Count; } } }

    /// <summary>Enable plan mode.</summary>
    public void Enable()
    {
        if (IsEnabled) return;
        IsEnabled = true;
        _eventBus.Publish(new PlanModeChangedEvent(true, DateTime.UtcNow));
    }

    /// <summary>Disable plan mode.</summary>
    public void Disable()
    {
        if (!IsEnabled) return;
        IsEnabled = false;
        _eventBus.Publish(new PlanModeChangedEvent(false, DateTime.UtcNow));
    }

    /// <summary>Get all pending edits (snapshot).</summary>
    public IReadOnlyList<PendingEdit> GetPending()
    {
        lock (_lock) { return _pending.ToList(); }
    }

    /// <summary>Submit a write that should be held until approved (only when plan mode is on).</summary>
    public PendingEdit SubmitIfPlanMode(string toolName, string filePath, string content, string? originalContent = null, string? description = null)
    {
        if (!IsEnabled)
        {
            // Plan mode off: caller should write directly. We still return a synthetic record for traceability.
            return new PendingEdit
            {
                Id = Guid.NewGuid().ToString("N"),
                ToolName = toolName,
                FilePath = filePath,
                Content = content,
                OriginalContent = originalContent,
                Description = description,
            };
        }

        var edit = new PendingEdit
        {
            Id = Guid.NewGuid().ToString("N"),
            ToolName = toolName,
            FilePath = filePath,
            Content = content,
            OriginalContent = originalContent,
            Description = description,
        };
        lock (_lock) { _pending.Add(edit); }
        return edit;
    }

    /// <summary>Approve and apply a pending edit. Returns the write state.</summary>
    public WriteState Approve(string pendingId, Action<string, string> writeAction)
    {
        PendingEdit? edit;
        lock (_lock)
        {
            edit = _pending.FirstOrDefault(p => p.Id == pendingId);
            if (edit is null) return WriteState.Rejected;
            _pending.Remove(edit);
        }
        try
        {
            writeAction(edit.FilePath, edit.Content);
            return WriteState.Applied;
        }
        catch
        {
            return WriteState.Rejected;
        }
    }

    /// <summary>Reject a pending edit.</summary>
    public bool Reject(string pendingId)
    {
        lock (_lock)
        {
            var edit = _pending.FirstOrDefault(p => p.Id == pendingId);
            if (edit is null) return false;
            _pending.Remove(edit);
            return true;
        }
    }

    /// <summary>Approve and apply ALL pending edits.</summary>
    public int ApproveAll(Action<string, string> writeAction)
    {
        var applied = 0;
        while (true)
        {
            string? nextId;
            lock (_lock)
            {
                nextId = _pending.FirstOrDefault()?.Id;
                if (nextId is null) break;
            }
            if (Approve(nextId, writeAction) == WriteState.Applied) applied++;
            else break;
        }
        return applied;
    }
}
