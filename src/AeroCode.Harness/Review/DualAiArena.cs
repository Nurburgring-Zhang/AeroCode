// Copyright (c) AeroCode V3.0
// DualAiArena — multi-round adversarial Builder/Reviewer review with a Judge.
//
// Protocol per round:
//   1. Reviewer MUST steelman the builder's position first (SteelmanField is mandatory —
//      an empty steelman makes the round invalid), then deliver the critique.
//   2. Builder responds to the critique.
//   3. Judge rules Accept / Reject / Revise with a reason and evidence references.
// Convergence: two consecutive valid Accept verdicts, or the max round count (default 3).
// The full transcript is persisted to disk after every round.
//
// LLM roles are invoked through AeroCode.AI IAiProvider (directly, or via HarnessHost
// sub-agents). When no provider is available, a deterministic rule-based reviewer driven
// by real static signals in the reviewed artifact is used and marked [DEGRADED].
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AeroCode.AI.Models;
using AeroCode.AI.Providers;
using Microsoft.Extensions.Logging;

namespace AeroCode.Harness.Review;

/// <summary>Roles participating in the arena.</summary>
public enum ArenaRole
{
    /// <summary>Adversarial reviewer: steelman first, then critique.</summary>
    Reviewer,
    /// <summary>Builder: defends/repairs the artifact under review.</summary>
    Builder,
    /// <summary>Impartial judge: three-state ruling.</summary>
    Judge,
}

/// <summary>Three-state ruling issued by the judge.</summary>
public enum ArenaVerdict
{
    /// <summary>Artifact accepted as-is.</summary>
    Accept,
    /// <summary>Artifact rejected — fundamental problems.</summary>
    Reject,
    /// <summary>Artifact needs revision — addressable problems.</summary>
    Revise,
}

/// <summary>
/// Invokes one arena role with a system prompt and a user message, returning the raw text
/// output. Implementations: LLM provider, HarnessHost sub-agents, deterministic rules.
/// </summary>
public interface IArenaRoleInvoker
{
    /// <summary>Invoke the role and return its textual output.</summary>
    Task<string> InvokeAsync(ArenaRole role, string systemPrompt, string userMessage, CancellationToken ct);
}

/// <summary>Role invoker backed by a direct <see cref="IAiProvider"/> call per invocation.</summary>
public sealed class LlmArenaRoleInvoker : IArenaRoleInvoker
{
    private readonly IAiProvider _provider;
    private readonly string _model;

    /// <param name="provider">The AI provider to call.</param>
    /// <param name="model">Optional model id (empty = provider default).</param>
    public LlmArenaRoleInvoker(IAiProvider provider, string model = "")
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _model = model;
    }

    /// <inheritdoc />
    public async Task<string> InvokeAsync(ArenaRole role, string systemPrompt, string userMessage, CancellationToken ct)
    {
        var request = new ChatRequest
        {
            Model = _model,
            Temperature = 0.2,
            Messages = new List<ChatMessage>
            {
                new() { Role = "system", Content = systemPrompt },
                new() { Role = "user", Content = userMessage },
            },
        };
        var response = await _provider.ChatAsync(request, ct);
        return response.Content ?? string.Empty;
    }
}

/// <summary>
/// Role invoker that creates one HarnessHost sub-agent per role via
/// <see cref="HarnessHost.CreateAgent"/> — each role gets an independent context
/// (own session/message history) and accumulates the conversation across rounds.
/// </summary>
public sealed class AgentArenaRoleInvoker : IArenaRoleInvoker
{
    private readonly HarnessHost _host;
    private readonly IAiProvider _provider;
    private readonly Dictionary<ArenaRole, Agent.Agent> _agents = new();
    private readonly Dictionary<ArenaRole, string> _systemPrompts = new();
    private readonly object _lock = new();

    /// <param name="host">The harness host used as the sub-agent factory.</param>
    /// <param name="provider">The AI provider each sub-agent calls.</param>
    public AgentArenaRoleInvoker(HarnessHost host, IAiProvider provider)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    /// <inheritdoc />
    public async Task<string> InvokeAsync(ArenaRole role, string systemPrompt, string userMessage, CancellationToken ct)
    {
        Agent.Agent agent;
        lock (_lock)
        {
            if (!_agents.TryGetValue(role, out agent!))
            {
                // Real sub-agent factory call: independent session/context per role.
                agent = _host.CreateAgent(
                    _provider,
                    presetId: null,
                    sessionId: $"arena-{role.ToString().ToLowerInvariant()}-{Guid.NewGuid():N}",
                    role: $"DualAiArena {role}");
                _agents[role] = agent;
                _systemPrompts[role] = systemPrompt;
                agent.SetSystemPrompt(systemPrompt);
            }
            else if (!string.Equals(_systemPrompts[role], systemPrompt, StringComparison.Ordinal))
            {
                agent.SetSystemPrompt(systemPrompt);
                _systemPrompts[role] = systemPrompt;
            }
        }

        var result = await agent.RunAsync(userMessage, toolDispatcher: null, ct);
        return result.Text;
    }
}

/// <summary>
/// Decorator that enforces an LLM-call budget around another invoker.
/// Throws <see cref="Loop.LoopBudgetExhaustedException"/> when the budget is spent.
/// </summary>
public sealed class BudgetedArenaRoleInvoker : IArenaRoleInvoker
{
    private readonly IArenaRoleInvoker _inner;
    private readonly Loop.LoopBudget _budget;

    /// <param name="inner">The invoker to wrap.</param>
    /// <param name="budget">The budget to charge one LLM call per invocation.</param>
    public BudgetedArenaRoleInvoker(IArenaRoleInvoker inner, Loop.LoopBudget budget)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _budget = budget ?? throw new ArgumentNullException(nameof(budget));
    }

    /// <inheritdoc />
    public async Task<string> InvokeAsync(ArenaRole role, string systemPrompt, string userMessage, CancellationToken ct)
    {
        if (!_budget.TryConsumeLlmCall())
            throw new Loop.LoopBudgetExhaustedException($"LLM call budget exhausted ({_budget.MaxLlmCalls} calls) before arena role {role}.");
        return await _inner.InvokeAsync(role, systemPrompt, userMessage, ct);
    }
}

/// <summary>
/// Deterministic rule-based invoker used when no LLM provider is available (marked
/// [DEGRADED] at construction by the arena). Decisions are derived from real static
/// signals in the text under review: failure markers (fail/error/exception/…), warning
/// markers (warn/degraded/…) and success markers (pass/succeed/…). No fabricated content:
/// every produced statement cites the lines actually found.
/// </summary>
public sealed class DeterministicArenaRoleInvoker : IArenaRoleInvoker
{
    private static readonly string[] FailureMarkers =
        { "fail", "error", "exception", "missing evidence", "inconclusive", "blocked", "rejected", "denied" };

    private static readonly string[] WarningMarkers =
        { "warn", "degraded", "retry", "revise", "unstable" };

    private static readonly string[] SuccessMarkers =
        { "pass", "succeed", "accepted", "verified", "all criteria" };

    /// <inheritdoc />
    public Task<string> InvokeAsync(ArenaRole role, string systemPrompt, string userMessage, CancellationToken ct)
    {
        var artifact = ExtractArtifactSection(userMessage);
        var lines = artifact.Split('\n');
        var failureLines = FindMarkerLines(lines, FailureMarkers);
        var warningLines = FindMarkerLines(lines, WarningMarkers);
        var successLines = FindMarkerLines(lines, SuccessMarkers);

        var output = role switch
        {
            ArenaRole.Reviewer => BuildReviewerOutput(failureLines, warningLines, successLines, lines),
            ArenaRole.Builder => BuildBuilderOutput(userMessage, failureLines, warningLines),
            ArenaRole.Judge => BuildJudgeOutput(failureLines, warningLines, successLines),
            _ => string.Empty,
        };
        return Task.FromResult(output);
    }

    private static string ExtractArtifactSection(string userMessage)
    {
        const string startMarker = "ARTIFACT UNDER REVIEW:";
        const string endMarker = "END OF ARTIFACT.";
        var start = userMessage.IndexOf(startMarker, StringComparison.Ordinal);
        if (start < 0) return userMessage;
        start += startMarker.Length;
        var end = userMessage.IndexOf(endMarker, start, StringComparison.Ordinal);
        return end < 0 ? userMessage[start..] : userMessage[start..end];
    }

    private static List<(int Line, string Text)> FindMarkerLines(string[] lines, string[] markers)
    {
        var found = new List<(int, string)>();
        for (var i = 0; i < lines.Length; i++)
        {
            var lower = lines[i].ToLowerInvariant();
            if (markers.Any(m => lower.Contains(m, StringComparison.Ordinal)))
                found.Add((i + 1, lines[i].Trim()));
        }
        return found;
    }

    private static string BuildReviewerOutput(
        List<(int Line, string Text)> failures,
        List<(int Line, string Text)> warnings,
        List<(int Line, string Text)> successes,
        string[] lines)
    {
        var sb = new StringBuilder();
        sb.Append("{\"steelman\": \"");
        if (successes.Count > 0)
        {
            sb.Append("The strongest case for this build rests on verified positive signals: ");
            sb.Append(string.Join("; ", successes.Take(5).Select(s => $"line {s.Line}: '{Truncate(s.Text)}'")));
        }
        else
        {
            var firstLine = lines.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim() ?? "(empty artifact)";
            sb.Append($"No positive signals detected; the strongest case rests on the stated objective: '{Truncate(firstLine)}'");
        }
        sb.Append("\", \"critique\": \"");
        if (failures.Count == 0 && warnings.Count == 0)
        {
            sb.Append("No failure or warning signals detected in the artifact.");
        }
        else
        {
            var parts = new List<string>();
            if (failures.Count > 0)
                parts.Add("failure signals: " + string.Join("; ", failures.Take(8).Select(f => $"line {f.Line}: '{Truncate(f.Text)}'")));
            if (warnings.Count > 0)
                parts.Add("warning signals: " + string.Join("; ", warnings.Take(8).Select(w => $"line {w.Line}: '{Truncate(w.Text)}'")));
            sb.Append(string.Join(" | ", parts));
        }
        sb.Append("\"}");
        return sb.ToString();
    }

    private static string BuildBuilderOutput(string userMessage, List<(int Line, string Text)> failures, List<(int Line, string Text)> warnings)
    {
        // Respond to the critique section actually present in the judge/builder prompt.
        var critiqueStart = userMessage.IndexOf("REVIEWER CRITIQUE:", StringComparison.Ordinal);
        var findingCount = critiqueStart >= 0
            ? FindMarkerLines(userMessage[critiqueStart..].Split('\n'), FailureMarkers.Concat(WarningMarkers).ToArray()).Count
            : failures.Count + warnings.Count;
        return findingCount == 0
            ? "No findings to address; the artifact stands as built."
            : $"Acknowledged {findingCount} finding(s). Remediation: apply a targeted fix for each cited signal, then re-run build and gate verification before the next review round.";
    }

    private static string BuildJudgeOutput(
        List<(int Line, string Text)> failures,
        List<(int Line, string Text)> warnings,
        List<(int Line, string Text)> successes)
    {
        var sb = new StringBuilder();
        if (failures.Count > 0)
        {
            sb.Append("{\"verdict\": \"reject\", \"reason\": \"")
              .Append($"{failures.Count} failure signal(s) present in the artifact")
              .Append("\", \"evidence\": [")
              .Append(string.Join(", ", failures.Take(5).Select(f => $"\"line {f.Line}: {Escape(Truncate(f.Text))}\"")))
              .Append("]}");
        }
        else if (warnings.Count > 0)
        {
            sb.Append("{\"verdict\": \"revise\", \"reason\": \"")
              .Append($"{warnings.Count} warning signal(s) present, no hard failures")
              .Append("\", \"evidence\": [")
              .Append(string.Join(", ", warnings.Take(5).Select(w => $"\"line {w.Line}: {Escape(Truncate(w.Text))}\"")))
              .Append("]}");
        }
        else
        {
            sb.Append("{\"verdict\": \"accept\", \"reason\": \"")
              .Append(successes.Count > 0
                  ? $"No failure or warning signals; {successes.Count} positive signal(s) verified"
                  : "No failure or warning signals detected")
              .Append("\", \"evidence\": [")
              .Append(string.Join(", ", successes.Take(5).Select(s => $"\"line {s.Line}: {Escape(Truncate(s.Text))}\"")))
              .Append("]}");
        }
        return sb.ToString();
    }

    private static string Truncate(string s) => s.Length <= 160 ? s : s[..160];

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "'");
}

/// <summary>Reviewer statement: mandatory steelman plus the critique.</summary>
public sealed record ReviewerStatement(string Steelman, string Critique);

/// <summary>Judge decision: three-state verdict, reason, evidence references.</summary>
public sealed record JudgeDecision(ArenaVerdict Verdict, string Reason, IReadOnlyList<string> EvidenceRefs);

/// <summary>One arena round. Invalid rounds (e.g. missing steelman) carry Valid=false.</summary>
public sealed class ArenaRound
{
    /// <summary>1-based round index.</summary>
    public int Index { get; init; }

    /// <summary>Whether the round satisfied the protocol (steelman present, judge parseable).</summary>
    public bool Valid { get; init; }

    /// <summary>Why the round is invalid (null when valid).</summary>
    public string? InvalidReason { get; init; }

    /// <summary>Reviewer steelman of the builder's position (mandatory).</summary>
    public string Steelman { get; init; } = string.Empty;

    /// <summary>Reviewer critique.</summary>
    public string Critique { get; init; } = string.Empty;

    /// <summary>Builder response to the critique.</summary>
    public string BuilderResponse { get; init; } = string.Empty;

    /// <summary>Judge verdict (null for invalid rounds).</summary>
    public ArenaVerdict? Verdict { get; init; }

    /// <summary>Judge reason.</summary>
    public string? VerdictReason { get; init; }

    /// <summary>Evidence references cited by the judge.</summary>
    public IReadOnlyList<string> EvidenceRefs { get; init; } = Array.Empty<string>();

    /// <summary>Round wall-clock duration.</summary>
    public TimeSpan Duration { get; init; }
}

/// <summary>Options for an arena run.</summary>
public sealed class DualAiArenaOptions
{
    /// <summary>Maximum number of rounds (default 3).</summary>
    public int MaxRounds { get; init; } = 3;

    /// <summary>Consecutive valid Accept verdicts required for convergence (default 2).</summary>
    public int ConsecutiveAcceptsRequired { get; init; } = 2;

    /// <summary>Directory where transcripts are persisted (default: temp/aerocode-arena).</summary>
    public string? TranscriptDirectory { get; init; }
}

/// <summary>Result of an arena run.</summary>
public sealed class ArenaResult
{
    /// <summary>Final verdict: Accept only when converged; otherwise last valid verdict or Revise.</summary>
    public ArenaVerdict FinalVerdict { get; init; }

    /// <summary>True when convergence (consecutive accepts) was reached.</summary>
    public bool Converged { get; init; }

    /// <summary>Why the run ended (convergence / max rounds / budget / cancellation).</summary>
    public string TerminationReason { get; init; } = string.Empty;

    /// <summary>All rounds executed, including invalid ones.</summary>
    public IReadOnlyList<ArenaRound> Rounds { get; init; } = Array.Empty<ArenaRound>();

    /// <summary>Path of the persisted transcript JSON.</summary>
    public string? TranscriptPath { get; init; }
}

/// <summary>
/// DualAiArena — Builder/Reviewer adversarial review with judge rulings.
/// See the file header for the protocol. The arena is invoker-agnostic: LLM-backed
/// invokers call the real provider; the deterministic invoker provides a [DEGRADED]
/// rule-based fallback when no provider exists.
/// </summary>
public sealed class DualAiArena
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IArenaRoleInvoker _invoker;
    private readonly DualAiArenaOptions _options;
    private readonly ILogger? _logger;
    private readonly bool _degraded;

    /// <param name="invoker">Role invoker (LLM, sub-agent, or deterministic).</param>
    /// <param name="options">Arena options.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="degraded">
    /// True when the invoker is a deterministic fallback; causes a [DEGRADED] log line.
    /// </param>
    public DualAiArena(IArenaRoleInvoker invoker, DualAiArenaOptions? options = null, ILogger? logger = null, bool degraded = false)
    {
        _invoker = invoker ?? throw new ArgumentNullException(nameof(invoker));
        _options = options ?? new DualAiArenaOptions();
        if (_options.MaxRounds < 1) throw new ArgumentOutOfRangeException(nameof(options), "MaxRounds must be >= 1.");
        if (_options.ConsecutiveAcceptsRequired < 1) throw new ArgumentOutOfRangeException(nameof(options), "ConsecutiveAcceptsRequired must be >= 1.");
        _logger = logger;
        _degraded = degraded;
        if (_degraded)
        {
            _logger?.LogWarning("[DEGRADED] DualAiArena running with deterministic rule-based reviewers — no LLM provider available. Verdicts derive from static signal analysis only.");
        }
    }

    /// <summary>Creates an arena backed by direct provider calls.</summary>
    public static DualAiArena CreateLlm(IAiProvider provider, DualAiArenaOptions? options = null, ILogger? logger = null, string model = "")
        => new(new LlmArenaRoleInvoker(provider, model), options, logger);

    /// <summary>Creates an arena whose roles run as HarnessHost sub-agents (independent contexts).</summary>
    public static DualAiArena CreateWithSubAgents(HarnessHost host, IAiProvider provider, DualAiArenaOptions? options = null, ILogger? logger = null)
        => new(new AgentArenaRoleInvoker(host, provider), options, logger);

    /// <summary>Creates the deterministic [DEGRADED] fallback arena (no provider).</summary>
    public static DualAiArena CreateDeterministic(DualAiArenaOptions? options = null, ILogger? logger = null)
        => new(new DeterministicArenaRoleInvoker(), options, logger, degraded: true);

    /// <summary>
    /// Run the adversarial review over the given subject (the artifact under review).
    /// </summary>
    /// <param name="subject">The artifact text to review (e.g. goal + gate report).</param>
    /// <param name="transcriptDirectory">Per-run override of the transcript directory.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<ArenaResult> RunAsync(string subject, string? transcriptDirectory = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(subject);
        var runId = Guid.NewGuid().ToString("N");
        var rounds = new List<ArenaRound>();
        var consecutiveAccepts = 0;
        var converged = false;
        string termination;

        var dir = transcriptDirectory ?? _options.TranscriptDirectory
            ?? Path.Combine(Path.GetTempPath(), "aerocode-arena");
        Directory.CreateDirectory(dir);
        var transcriptPath = Path.Combine(dir, $"arena-transcript-{runId}.json");

        var prior = new StringBuilder();
        ArenaVerdict? lastValidVerdict = null;

        for (var i = 1; i <= _options.MaxRounds; i++)
        {
            ct.ThrowIfCancellationRequested();
            var t0 = DateTime.UtcNow;

            // --- 1. Reviewer: steelman (mandatory) + critique ---
            var reviewerRaw = await _invoker.InvokeAsync(
                ArenaRole.Reviewer,
                ReviewerSystemPrompt,
                BuildReviewerUserMessage(subject, prior.ToString(), i),
                ct);

            if (!TryParseReviewerStatement(reviewerRaw, out var statement, out var parseError))
            {
                rounds.Add(new ArenaRound
                {
                    Index = i,
                    Valid = false,
                    InvalidReason = parseError,
                    Steelman = string.Empty,
                    Critique = reviewerRaw,
                    Duration = DateTime.UtcNow - t0,
                });
                consecutiveAccepts = 0;
                AppendPrior(prior, rounds[^1]);
                PersistTranscript(transcriptPath, runId, subject, rounds, converged, "in-progress");
                continue;
            }

            if (string.IsNullOrWhiteSpace(statement!.Steelman))
            {
                rounds.Add(new ArenaRound
                {
                    Index = i,
                    Valid = false,
                    InvalidReason = "Reviewer produced no steelman — the steelman field is mandatory; round invalid.",
                    Steelman = string.Empty,
                    Critique = statement.Critique,
                    Duration = DateTime.UtcNow - t0,
                });
                consecutiveAccepts = 0;
                AppendPrior(prior, rounds[^1]);
                PersistTranscript(transcriptPath, runId, subject, rounds, converged, "in-progress");
                continue;
            }

            // --- 2. Builder responds to the critique ---
            var builderResponse = await _invoker.InvokeAsync(
                ArenaRole.Builder,
                BuilderSystemPrompt,
                BuildBuilderUserMessage(subject, statement, i),
                ct);

            // --- 3. Judge rules ---
            var judgeRaw = await _invoker.InvokeAsync(
                ArenaRole.Judge,
                JudgeSystemPrompt,
                BuildJudgeUserMessage(subject, statement, builderResponse, i),
                ct);

            if (!TryParseJudgeDecision(judgeRaw, out var decision, out var judgeError))
            {
                rounds.Add(new ArenaRound
                {
                    Index = i,
                    Valid = false,
                    InvalidReason = judgeError,
                    Steelman = statement.Steelman,
                    Critique = statement.Critique,
                    BuilderResponse = builderResponse,
                    Duration = DateTime.UtcNow - t0,
                });
                consecutiveAccepts = 0;
                AppendPrior(prior, rounds[^1]);
                PersistTranscript(transcriptPath, runId, subject, rounds, converged, "in-progress");
                continue;
            }

            var round = new ArenaRound
            {
                Index = i,
                Valid = true,
                Steelman = statement.Steelman,
                Critique = statement.Critique,
                BuilderResponse = builderResponse,
                Verdict = decision!.Verdict,
                VerdictReason = decision.Reason,
                EvidenceRefs = decision.EvidenceRefs,
                Duration = DateTime.UtcNow - t0,
            };
            rounds.Add(round);
            lastValidVerdict = decision.Verdict;
            consecutiveAccepts = decision.Verdict == ArenaVerdict.Accept ? consecutiveAccepts + 1 : 0;
            AppendPrior(prior, round);
            PersistTranscript(transcriptPath, runId, subject, rounds, converged, "in-progress");

            if (consecutiveAccepts >= _options.ConsecutiveAcceptsRequired)
            {
                converged = true;
                termination = $"Converged after {consecutiveAccepts} consecutive Accept verdicts at round {i}.";
                PersistTranscript(transcriptPath, runId, subject, rounds, converged, termination);
                return new ArenaResult
                {
                    FinalVerdict = ArenaVerdict.Accept,
                    Converged = true,
                    TerminationReason = termination,
                    Rounds = rounds,
                    TranscriptPath = transcriptPath,
                };
            }
        }

        termination = $"Reached max rounds ({_options.MaxRounds}) without convergence.";
        var finalVerdict = lastValidVerdict ?? ArenaVerdict.Revise;
        if (lastValidVerdict is null)
            termination += " No valid judge verdict was produced; final verdict defaults to Revise.";
        PersistTranscript(transcriptPath, runId, subject, rounds, converged, termination);

        return new ArenaResult
        {
            FinalVerdict = finalVerdict,
            Converged = false,
            TerminationReason = termination,
            Rounds = rounds,
            TranscriptPath = transcriptPath,
        };
    }

    // ============== prompts ==============

    internal const string ReviewerSystemPrompt =
        "You are an adversarial code-review reviewer in a dual-AI arena. Protocol: you MUST first steelman the " +
        "builder's position (state the strongest honest case FOR the artifact as it stands), and only then deliver " +
        "your critique. An empty steelman invalidates the round. Output STRICT JSON only, no prose: " +
        "{\"steelman\": \"<strongest case for the build>\", \"critique\": \"<concrete problems, each citing evidence>\"}";

    internal const string BuilderSystemPrompt =
        "You are the builder in a dual-AI arena review. Respond to the reviewer's critique concretely: accept valid " +
        "findings with a remediation, or rebut with evidence. Output plain text.";

    internal const string JudgeSystemPrompt =
        "You are the impartial judge in a dual-AI arena review. Weigh the reviewer statement and the builder response, " +
        "then rule. Output STRICT JSON only, no prose: " +
        "{\"verdict\": \"accept|reject|revise\", \"reason\": \"<why>\", \"evidence\": [\"<cited evidence>\", ...]}";

    private static string BuildReviewerUserMessage(string subject, string priorRounds, int round) =>
        $"Round {round}.\n\nARTIFACT UNDER REVIEW:\n{subject}\nEND OF ARTIFACT.\n\nPRIOR ROUNDS:\n{(priorRounds.Length == 0 ? "(none)" : priorRounds)}\n\nProduce the reviewer statement as strict JSON.";

    private static string BuildBuilderUserMessage(string subject, ReviewerStatement statement, int round) =>
        $"Round {round}.\n\nARTIFACT UNDER REVIEW:\n{subject}\nEND OF ARTIFACT.\n\nREVIEWER STEELMAN:\n{statement.Steelman}\n\nREVIEWER CRITIQUE:\n{statement.Critique}\n\nRespond to the critique.";

    private static string BuildJudgeUserMessage(string subject, ReviewerStatement statement, string builderResponse, int round) =>
        $"Round {round}.\n\nARTIFACT UNDER REVIEW:\n{subject}\nEND OF ARTIFACT.\n\nREVIEWER STEELMAN:\n{statement.Steelman}\n\nREVIEWER CRITIQUE:\n{statement.Critique}\n\nBUILDER RESPONSE:\n{builderResponse}\n\nRule as strict JSON.";

    private static void AppendPrior(StringBuilder prior, ArenaRound round)
    {
        prior.AppendLine($"--- Round {round.Index} (valid={round.Valid}{(round.InvalidReason is null ? "" : $", invalid: {round.InvalidReason}")}) ---");
        if (round.Valid)
        {
            prior.AppendLine($"Steelman: {round.Steelman}");
            prior.AppendLine($"Critique: {round.Critique}");
            prior.AppendLine($"Builder: {round.BuilderResponse}");
            prior.AppendLine($"Judge: {round.Verdict} — {round.VerdictReason}");
        }
        else if (round.Steelman.Length > 0 || round.Critique.Length > 0)
        {
            prior.AppendLine($"Raw reviewer output: {round.Critique}");
        }
    }

    // ============== parsing ==============

    internal static bool TryParseReviewerStatement(string raw, out ReviewerStatement? statement, out string error)
    {
        statement = null;
        if (!TryParseJsonObject(raw, out var root, out error)) return false;
        var steelman = GetStringProp(root, "steelman");
        var critique = GetStringProp(root, "critique") ?? string.Empty;
        statement = new ReviewerStatement(steelman ?? string.Empty, critique);
        return true;
    }

    internal static bool TryParseJudgeDecision(string raw, out JudgeDecision? decision, out string error)
    {
        decision = null;
        if (!TryParseJsonObject(raw, out var root, out error)) return false;
        var verdictStr = GetStringProp(root, "verdict");
        if (verdictStr is null)
        {
            error = "Judge output missing 'verdict' field.";
            return false;
        }
        ArenaVerdict verdict;
        if (verdictStr.Equals("accept", StringComparison.OrdinalIgnoreCase)) verdict = ArenaVerdict.Accept;
        else if (verdictStr.Equals("reject", StringComparison.OrdinalIgnoreCase)) verdict = ArenaVerdict.Reject;
        else if (verdictStr.Equals("revise", StringComparison.OrdinalIgnoreCase)) verdict = ArenaVerdict.Revise;
        else
        {
            error = $"Judge verdict '{verdictStr}' is not one of accept/reject/revise.";
            return false;
        }
        var reason = GetStringProp(root, "reason") ?? string.Empty;
        var evidence = new List<string>();
        if (root.TryGetProperty("evidence", out var evEl) && evEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in evEl.EnumerateArray())
                if (e.ValueKind == JsonValueKind.String) evidence.Add(e.GetString() ?? string.Empty);
        }
        decision = new JudgeDecision(verdict, reason, evidence);
        return true;
    }

    private static bool TryParseJsonObject(string raw, out JsonElement root, out string error)
    {
        root = default;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "Empty role output.";
            return false;
        }
        var text = raw.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var s = text.IndexOf('{');
            var e = text.LastIndexOf('}');
            if (s >= 0 && e > s) text = text[s..(e + 1)];
        }
        else
        {
            var s = text.IndexOf('{');
            var e = text.LastIndexOf('}');
            if (s >= 0 && e > s) text = text[s..(e + 1)];
        }
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "Role output is not a JSON object.";
                return false;
            }
            root = doc.RootElement.Clone();
            return true;
        }
        catch (JsonException ex)
        {
            error = $"Role output is not valid JSON: {ex.Message}";
            return false;
        }
    }

    private static string? GetStringProp(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    // ============== transcript ==============

    private void PersistTranscript(string path, string runId, string subject, List<ArenaRound> rounds, bool converged, string status)
    {
        try
        {
            var transcript = new
            {
                RunId = runId,
                Status = status,
                Converged = converged,
                Degraded = _degraded,
                MaxRounds = _options.MaxRounds,
                ConsecutiveAcceptsRequired = _options.ConsecutiveAcceptsRequired,
                PersistedAtUtc = DateTime.UtcNow,
                Subject = subject,
                Rounds = rounds,
            };
            File.WriteAllText(path, JsonSerializer.Serialize(transcript, JsonOpts));
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("[DEGRADED] DualAiArena could not persist transcript to '{Path}': {Error}", path, ex.Message);
        }
    }
}
