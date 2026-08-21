// Cluster test doubles — hand-written implementations of the real contracts
// (IExpertExecutor / FanOutJudge / ISessionService / IChatOrchestrationFacade).
// No mocking library, following the FakeMissionExecutor / FakeSessionService /
// FakeOrchestrationFacade pattern established in MissionControllerTests.cs.
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using AeroAgent.Autonomy.Cluster;
using AeroAgent.Conversation.Models;
using AeroAgent.Conversation.Orchestration;
using AeroAgent.Conversation.Services;
using AeroCode.Core.Common;

namespace AeroCode.Tests.Autonomy.Cluster;

/// <summary>Outcome factories shared by the scripted cluster test doubles.</summary>
internal static class ClusterOutcomes
{
    public static ExpertExecutionOutcome Ok(string output) =>
        new(true, false, false, output, null, 0);

    public static ExpertExecutionOutcome Fail(string error) =>
        new(false, false, false, string.Empty, error, 0);

    /// <summary>
    /// Hangs until the given token fires (Task.Delay throws OperationCanceledException),
    /// simulating a stuck attempt that the scheduler's per-attempt timeout must abandon.
    /// The final return is unreachable — it exists only to satisfy the compiler.
    /// </summary>
    public static async Task<ExpertExecutionOutcome> HangUntilCancelled(CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct);
        return new ExpertExecutionOutcome(false, true, false, string.Empty, null, 0);
    }
}

/// <summary>
/// Test double 1: scripted <see cref="IExpertExecutor"/>. Each branch node gets a
/// behavior delegate; real async delays are used so concurrency is observable.
/// Tracks every received context and the maximum number of attempts observed
/// executing simultaneously (interlocked, same pattern as RunState.EnterExecution).
/// </summary>
internal sealed class ScriptedExpertExecutor : IExpertExecutor
{
    public delegate Task<ExpertExecutionOutcome> AttemptScript(
        ExpertExecutionContext context, CancellationToken ct);

    private readonly Dictionary<string, AttemptScript> _byNode = new(StringComparer.Ordinal);
    private AttemptScript? _fallback;
    private int _active;
    private int _maxActive;

    /// <summary>Every execution context received, in arrival order.</summary>
    public ConcurrentQueue<ExpertExecutionContext> ReceivedContexts { get; } = new();

    /// <summary>(NodeId, ExpertId, started ticks) trace entries for overlap inspection.</summary>
    public ConcurrentBag<(string NodeId, string ExpertId, long StartedTicks)> ExecutionTrace { get; } = new();

    /// <summary>Maximum number of attempts observed executing at the same time.</summary>
    public int MaxActiveObserved => Volatile.Read(ref _maxActive);

    public ScriptedExpertExecutor When(string nodeId, AttemptScript script)
    {
        _byNode[nodeId] = script;
        return this;
    }

    public ScriptedExpertExecutor Fallback(AttemptScript script)
    {
        _fallback = script;
        return this;
    }

    public Task<ExpertExecutionOutcome> ExecuteAsync(ExpertExecutionContext context, CancellationToken ct)
    {
        ReceivedContexts.Enqueue(context);
        ExecutionTrace.Add((context.NodeId, context.ExpertId, DateTime.UtcNow.Ticks));
        EnterConcurrency();
        var script = _byNode.TryGetValue(context.NodeId, out var s)
            ? s
            : _fallback ?? ((_, _) => Task.FromResult(ClusterOutcomes.Fail($"no script for node '{context.NodeId}'")));
        return RunCoreAsync(script, context, ct);
    }

    private async Task<ExpertExecutionOutcome> RunCoreAsync(
        AttemptScript script, ExpertExecutionContext context, CancellationToken ct)
    {
        try
        {
            return await script(context, ct).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _active);
        }
    }

    private void EnterConcurrency()
    {
        var current = Interlocked.Increment(ref _active);
        int max;
        do
        {
            max = Volatile.Read(ref _maxActive);
            if (current <= max)
            {
                break;
            }
        }
        while (Interlocked.CompareExchange(ref _maxActive, current, max) != max);
    }

    // ---------- reusable script building blocks ----------

    /// <summary>Succeed after a real delay (simulated expert work).</summary>
    public static AttemptScript SucceedAfter(string output, int delayMs) =>
        async (_, ct) =>
        {
            await Task.Delay(delayMs, ct);
            return ClusterOutcomes.Ok(output);
        };

    /// <summary>Succeed after a real delay with an output derived from the context.</summary>
    public static AttemptScript SucceedAfter(Func<ExpertExecutionContext, string> output, int delayMs) =>
        async (ctx, ct) =>
        {
            await Task.Delay(delayMs, ct);
            return ClusterOutcomes.Ok(output(ctx));
        };

    /// <summary>Fail after a real delay (simulated crash at the stuck point).</summary>
    public static AttemptScript FailAfter(string error, int delayMs) =>
        async (_, ct) =>
        {
            await Task.Delay(delayMs, ct);
            return ClusterOutcomes.Fail(error);
        };
}

/// <summary>
/// Test double 2: deterministic <see cref="FanOutJudge"/> that records the ballot it
/// received and applies a scripted ruling (or a scripted throw for degraded paths).
/// </summary>
internal sealed class RecordingFanOutJudge
{
    private readonly Func<FanOutBallot, FanOutDecision> _rule;

    public FanOutBallot? ReceivedBallot { get; private set; }
    public int CallCount { get; private set; }

    public RecordingFanOutJudge(Func<FanOutBallot, FanOutDecision> rule) => _rule = rule;

    public Task<FanOutDecision> JudgeAsync(FanOutBallot ballot, CancellationToken ct)
    {
        CallCount++;
        ReceivedBallot = ballot;
        return Task.FromResult(_rule(ballot));
    }

    /// <summary>Method-group conversion to the production delegate type.</summary>
    public FanOutJudge AsDelegate() => JudgeAsync;
}

/// <summary>
/// Test double: session service for <see cref="FacadeExpertExecutor"/> tests.
/// GetSessionAsync succeeds only for ids placed in <see cref="ExistingSessionIds"/>;
/// creation can be failed on demand. Every call is counted for reuse assertions.
/// </summary>
internal sealed class ClusterFakeSessionService : ISessionService
{
    public HashSet<string> ExistingSessionIds { get; } = new(StringComparer.Ordinal);
    public bool FailCreation { get; set; }

    public int GetCallCount { get; private set; }
    public int CreateCallCount { get; private set; }
    public OrchestrationStrategy LastCreateStrategy { get; private set; }
    public string? LastCreateTitle { get; private set; }
    public List<ChatSession> CreatedSessions { get; } = new();

    public Task<Result<ChatSession>> CreateSessionAsync(
        OrchestrationStrategy strategy = OrchestrationStrategy.Single,
        string? preferredProviderId = null, string? preferredModel = null, string? title = null)
    {
        CreateCallCount++;
        LastCreateStrategy = strategy;
        LastCreateTitle = title;
        if (FailCreation)
        {
            return Task.FromResult(Result<ChatSession>.Fail("session creation disabled"));
        }

        var session = new ChatSession { Strategy = strategy, Title = title ?? string.Empty };
        CreatedSessions.Add(session);
        return Task.FromResult(Result<ChatSession>.Ok(session));
    }

    public Task<Result<ChatSession>> GetSessionAsync(string id)
    {
        GetCallCount++;
        return Task.FromResult(ExistingSessionIds.Contains(id)
            ? Result<ChatSession>.Ok(new ChatSession { Id = id })
            : Result<ChatSession>.Fail($"session '{id}' not found"));
    }

    public Task<Result<IReadOnlyList<ChatSessionSummary>>> ListSessionsAsync(bool includeDeleted = false)
        => throw new NotSupportedException();
    public Task<Result<ChatSession>> RenameSessionAsync(string id, string title)
        => throw new NotSupportedException();
    public Task<Result<ChatSession>> SetStrategyAsync(string id, OrchestrationStrategy strategy, string? preferredProviderId, string? preferredModel)
        => throw new NotSupportedException();
    public Task<Result<ChatSession>> TogglePinAsync(string id) => throw new NotSupportedException();
    public Task<Result<bool>> DeleteSessionAsync(string id) => throw new NotSupportedException();
    public Task<Result<bool>> RestoreSessionAsync(string id) => throw new NotSupportedException();
    public Task<Result<IReadOnlyList<ChatMessage>>> GetMessagesAsync(string sessionId) => throw new NotSupportedException();
    public Task<Result<ChatMessage>> AppendMessageAsync(ChatMessage message) => throw new NotSupportedException();
    public Task<Result<ChatMessage>> UpdateMessageAsync(ChatMessage message) => throw new NotSupportedException();
}

/// <summary>
/// Test double: scripted orchestration event stream for <see cref="FacadeExpertExecutor"/>
/// tests (same shape as FakeOrchestrationFacade in MissionControllerTests). The script
/// may throw to simulate a facade crash.
/// </summary>
internal sealed class ClusterFakeFacade : IChatOrchestrationFacade
{
    private readonly Func<string, string, IReadOnlyList<ChatEvent>> _script;

    public string? LastSessionId { get; private set; }
    public string? LastPayload { get; private set; }
    public int SendCallCount { get; private set; }

    public ClusterFakeFacade(Func<string, string, IReadOnlyList<ChatEvent>> script) => _script = script;

    public async IAsyncEnumerable<ChatEvent> SendAsync(
        string sessionId, string userText,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        SendCallCount++;
        LastSessionId = sessionId;
        LastPayload = userText;
        foreach (var ev in _script(sessionId, userText))
        {
            await Task.Yield();
            yield return ev;
        }
    }

    public static AssistantMessageStarted Started(string sessionId, string messageId) => new()
    {
        SessionId = sessionId,
        MessageId = messageId,
        ProviderId = "test-provider",
        ModelId = "test-model",
        OrchestrationRole = StrategyRole.None,
    };
}
