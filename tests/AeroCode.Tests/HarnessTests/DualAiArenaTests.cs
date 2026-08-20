// DualAiArena tests — scripted role invoker (legitimate test double implementing the
// real IArenaRoleInvoker contract) plus the deterministic [DEGRADED] invoker.
using AeroCode.Harness.Review;
using Xunit;

namespace AeroCode.Tests.HarnessTests;

/// <summary>Test double: scripted outputs per arena role.</summary>
internal sealed class ScriptedArenaInvoker : IArenaRoleInvoker
{
    public Queue<string> ReviewerOutputs { get; } = new();
    public Queue<string> BuilderOutputs { get; } = new();
    public Queue<string> JudgeOutputs { get; } = new();
    public int TotalCalls { get; private set; }

    public Task<string> InvokeAsync(ArenaRole role, string systemPrompt, string userMessage, CancellationToken ct)
    {
        TotalCalls++;
        var queue = role switch
        {
            ArenaRole.Reviewer => ReviewerOutputs,
            ArenaRole.Builder => BuilderOutputs,
            ArenaRole.Judge => JudgeOutputs,
            _ => JudgeOutputs,
        };
        return Task.FromResult(queue.Count > 0 ? queue.Dequeue() : string.Empty);
    }
}

public sealed class DualAiArenaTests : IDisposable
{
    private readonly string _transcriptDir;

    public DualAiArenaTests()
    {
        _transcriptDir = Path.Combine(Path.GetTempPath(), "aerocode-arena-tests-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_transcriptDir)) Directory.Delete(_transcriptDir, true); } catch { }
    }

    private static string Reviewer(string steelman, string critique) =>
        $"{{\"steelman\": \"{steelman}\", \"critique\": \"{critique}\"}}";

    private static string Judge(string verdict, string reason) =>
        $"{{\"verdict\": \"{verdict}\", \"reason\": \"{reason}\", \"evidence\": [\"e1\"]}}";

    [Fact]
    public async Task TwoConsecutiveAccepts_Converge_WithAcceptVerdict()
    {
        var invoker = new ScriptedArenaInvoker();
        for (var i = 0; i < 2; i++)
        {
            invoker.ReviewerOutputs.Enqueue(Reviewer("strongest case stands", "minor nit"));
            invoker.BuilderOutputs.Enqueue("addressed the nit");
            invoker.JudgeOutputs.Enqueue(Judge("accept", "no blocking findings"));
        }
        var arena = new DualAiArena(invoker, new DualAiArenaOptions
        {
            MaxRounds = 3,
            ConsecutiveAcceptsRequired = 2,
            TranscriptDirectory = _transcriptDir,
        });

        var result = await arena.RunAsync("artifact under test");

        Assert.True(result.Converged);
        Assert.Equal(ArenaVerdict.Accept, result.FinalVerdict);
        Assert.Equal(2, result.Rounds.Count);
        Assert.All(result.Rounds, r => Assert.True(r.Valid));
        Assert.NotNull(result.TranscriptPath);
        Assert.True(File.Exists(result.TranscriptPath)); // real transcript on disk
    }

    [Fact]
    public async Task MissingSteelman_RoundInvalid_AndResetsAcceptStreak()
    {
        var invoker = new ScriptedArenaInvoker();
        // Round 1: valid accept. Round 2: empty steelman → invalid. Round 3: accept again.
        invoker.ReviewerOutputs.Enqueue(Reviewer("valid steelman", "critique 1"));
        invoker.ReviewerOutputs.Enqueue(Reviewer("", "critique without steelman"));
        invoker.ReviewerOutputs.Enqueue(Reviewer("valid again", "critique 3"));
        invoker.BuilderOutputs.Enqueue("response 1");
        invoker.BuilderOutputs.Enqueue("response 3");
        invoker.JudgeOutputs.Enqueue(Judge("accept", "ok"));
        invoker.JudgeOutputs.Enqueue(Judge("accept", "ok again"));

        var arena = new DualAiArena(invoker, new DualAiArenaOptions
        {
            MaxRounds = 3,
            ConsecutiveAcceptsRequired = 2,
            TranscriptDirectory = _transcriptDir,
        });

        var result = await arena.RunAsync("subject");

        // The invalid round broke the streak: accepts at rounds 1 and 3 are NOT consecutive.
        Assert.False(result.Converged);
        Assert.Equal(3, result.Rounds.Count);
        Assert.False(result.Rounds[1].Valid);
        Assert.Contains("steelman", result.Rounds[1].InvalidReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RejectVerdict_NeverConverges_FinalVerdictIsLastValid()
    {
        var invoker = new ScriptedArenaInvoker();
        for (var i = 0; i < 3; i++)
        {
            invoker.ReviewerOutputs.Enqueue(Reviewer("steelman", "hard failure found"));
            invoker.BuilderOutputs.Enqueue("will fix");
            invoker.JudgeOutputs.Enqueue(Judge("reject", "blocking issue"));
        }
        var arena = new DualAiArena(invoker, new DualAiArenaOptions
        {
            MaxRounds = 3,
            ConsecutiveAcceptsRequired = 2,
            TranscriptDirectory = _transcriptDir,
        });

        var result = await arena.RunAsync("subject");

        Assert.False(result.Converged);
        Assert.Equal(ArenaVerdict.Reject, result.FinalVerdict);
        Assert.Contains("max rounds", result.TerminationReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnparseableReviewerOutput_RoundInvalid_NotCrash()
    {
        var invoker = new ScriptedArenaInvoker();
        invoker.ReviewerOutputs.Enqueue("this is not json at all");
        invoker.ReviewerOutputs.Enqueue(Reviewer("steelman", "critique"));
        invoker.BuilderOutputs.Enqueue("response");
        invoker.JudgeOutputs.Enqueue(Judge("revise", "needs work"));

        var arena = new DualAiArena(invoker, new DualAiArenaOptions
        {
            MaxRounds = 2,
            TranscriptDirectory = _transcriptDir,
        });

        var result = await arena.RunAsync("subject");

        Assert.Equal(2, result.Rounds.Count);
        Assert.False(result.Rounds[0].Valid);
        Assert.Equal(ArenaVerdict.Revise, result.FinalVerdict);
    }

    [Fact]
    public async Task NoValidVerdictAtAll_DefaultsToRevise()
    {
        var invoker = new ScriptedArenaInvoker(); // all queues empty → unparseable outputs
        var arena = new DualAiArena(invoker, new DualAiArenaOptions
        {
            MaxRounds = 2,
            TranscriptDirectory = _transcriptDir,
        });

        var result = await arena.RunAsync("subject");

        Assert.False(result.Converged);
        Assert.Equal(ArenaVerdict.Revise, result.FinalVerdict);
        Assert.Contains("Revise", result.TerminationReason);
    }

    [Fact]
    public async Task DeterministicInvoker_AcceptsCleanArtifact_Converges()
    {
        var arena = DualAiArena.CreateDeterministic(new DualAiArenaOptions
        {
            MaxRounds = 3,
            ConsecutiveAcceptsRequired = 2,
            TranscriptDirectory = _transcriptDir,
        });

        var result = await arena.RunAsync(
            "ARTIFACT UNDER REVIEW:\nbuild: all criteria verified, tests pass\nEND OF ARTIFACT.");

        Assert.True(result.Converged);
        Assert.Equal(ArenaVerdict.Accept, result.FinalVerdict);
    }

    [Fact]
    public async Task DeterministicInvoker_RejectsArtifactWithFailureMarkers()
    {
        var arena = DualAiArena.CreateDeterministic(new DualAiArenaOptions
        {
            MaxRounds = 2,
            TranscriptDirectory = _transcriptDir,
        });

        var result = await arena.RunAsync(
            "ARTIFACT UNDER REVIEW:\nbuild failed with error CS1001\nEND OF ARTIFACT.");

        Assert.False(result.Converged);
        Assert.Equal(ArenaVerdict.Reject, result.FinalVerdict);
    }

    [Fact]
    public async Task DeterministicInvoker_ReviseOnWarningsOnly()
    {
        var arena = DualAiArena.CreateDeterministic(new DualAiArenaOptions
        {
            MaxRounds = 2,
            TranscriptDirectory = _transcriptDir,
        });

        var result = await arena.RunAsync(
            "ARTIFACT UNDER REVIEW:\nwarning: degraded path used, retry suggested\nEND OF ARTIFACT.");

        Assert.Equal(ArenaVerdict.Revise, result.FinalVerdict);
    }

    [Fact]
    public async Task EmptySubject_Throws()
    {
        var arena = DualAiArena.CreateDeterministic(new DualAiArenaOptions { TranscriptDirectory = _transcriptDir });
        await Assert.ThrowsAsync<ArgumentException>(() => arena.RunAsync(""));
    }

    [Fact]
    public async Task BudgetedInvoker_ThrowsWhenBudgetExhausted()
    {
        var budget = new AeroCode.Harness.Loop.LoopBudget(maxRounds: 5, maxLlmCalls: 1, maxDuration: TimeSpan.FromMinutes(5));
        var inner = new ScriptedArenaInvoker();
        for (var i = 0; i < 3; i++)
        {
            inner.ReviewerOutputs.Enqueue(Reviewer("s", "c"));
            inner.BuilderOutputs.Enqueue("b");
            inner.JudgeOutputs.Enqueue(Judge("revise", "r"));
        }
        var budgeted = new BudgetedArenaRoleInvoker(inner, budget);
        var arena = new DualAiArena(budgeted, new DualAiArenaOptions
        {
            MaxRounds = 3,
            TranscriptDirectory = _transcriptDir,
        });

        // Budget allows exactly 1 LLM call; the arena needs many → honest budget exception path.
        await Assert.ThrowsAsync<AeroCode.Harness.Loop.LoopBudgetExhaustedException>(
            () => arena.RunAsync("subject"));
    }
}
