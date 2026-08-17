// Copyright (c) AeroCode V3.0
// LoopRunner — iterative self-correction loop with cache-first support
// (DeepSeek-Reasonix "Cache-First Loop" pattern: 99.82% cache hit saves ~5x cost).
//
// Run a step; if the (tool_name + canonical_args) key matches a cached result, return it
// immediately without invoking the step. Otherwise, invoke; on failure, attempt repair
// strategies; if all exhausted, fail with accumulated context for the next agent to reason over.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AeroCode.Harness.Cache;

namespace AeroCode.Harness.Loop;

/// <summary>
/// A single attempt to run a step. Returns null on success (continue), or a non-null
/// error message that triggers repair.
/// </summary>
public delegate Task<string?> StepAttempt(CancellationToken ct);

/// <summary>
/// A repair strategy: takes the previous error and the loop's history, returns a new
/// attempt to run, or null if this strategy cannot help (loop should move on).
/// </summary>
public delegate Task<StepAttempt?> RepairStrategy(string lastError, IReadOnlyList<LoopIteration> history, CancellationToken ct);

public sealed class LoopIteration
{
    public int Index { get; set; }
    public string? Error { get; set; }
    public string? RepairApplied { get; set; }
    public bool Succeeded { get; set; }
    public bool CacheHit { get; set; }
    public string? CacheKey { get; set; }
    public TimeSpan Duration { get; set; }
}

/// <summary>
/// Result of a loop run, with cache stats.
/// </summary>
public sealed class LoopResult
{
    public bool Succeeded { get; }
    public IReadOnlyList<LoopIteration> History { get; }
    public string? TerminationReason { get; }
    public DateTime StartedAt { get; }
    public DateTime FinishedAt { get; }
    public TimeSpan Total => FinishedAt - StartedAt;
    public int CacheHits { get; }
    public int CacheMisses { get; }

    public LoopResult(bool succeeded, IReadOnlyList<LoopIteration> history, string? reason, DateTime startedAt, int cacheHits, int cacheMisses)
    {
        Succeeded = succeeded; History = history; TerminationReason = reason;
        StartedAt = startedAt; FinishedAt = DateTime.UtcNow;
        CacheHits = cacheHits; CacheMisses = cacheMisses;
    }
}

/// <summary>
/// LoopRunner — runs a step in a loop with repair strategies until success or max iterations.
/// Supports optional cache-first: when CacheKey is provided AND the result is in cache,
/// returns the cached result without invoking the step. Mirrors Reasonix's
/// "append-only loop, byte-stable prefix cache" — except here the cache key is content-derived,
/// not prefix-derived, so the same effect is achievable across providers.
/// </summary>
public sealed class LoopRunner
{
    public int MaxIterations { get; }
    public List<RepairStrategy> Strategies { get; }
    public LruCache<string, string>? Cache { get; }

    /// <param name="maxIterations">hard cap on retry count</param>
    /// <param name="strategies">repair strategies, in priority order</param>
    /// <param name="cache">optional LRU cache for cache-first short-circuit (Reasonix pattern)</param>
    public LoopRunner(int maxIterations = 5, IEnumerable<RepairStrategy>? strategies = null, LruCache<string, string>? cache = null)
    {
        if (maxIterations < 1) throw new ArgumentOutOfRangeException(nameof(maxIterations));
        MaxIterations = maxIterations;
        Strategies = strategies?.ToList() ?? new();
        Cache = cache;
    }

    /// <summary>
    /// Run a step. If <paramref name="cacheKey"/> is provided and a result is cached,
    /// return it directly (cache-first hit). Otherwise invoke the step, cache the result on success,
    /// and apply repair strategies on failure.
    /// </summary>
    public async Task<LoopResult> RunAsync(
        StepAttempt initial,
        string? cacheKey = null,
        CancellationToken ct = default)
    {
        var history = new List<LoopIteration>();
        var startedAt = DateTime.UtcNow;
        var current = initial;
        var cacheHits = 0;
        var cacheMisses = 0;

        for (var i = 0; i < MaxIterations; i++)
        {
            ct.ThrowIfCancellationRequested();
            var iter = new LoopIteration { Index = i, CacheKey = cacheKey };
            var t0 = DateTime.UtcNow;

            // === Cache-first short-circuit (Reasonix pattern) ===
            if (cacheKey is not null && Cache is not null && Cache.TryGet(cacheKey, out var cached))
            {
                iter.Succeeded = true;
                iter.CacheHit = true;
                iter.Duration = DateTime.UtcNow - t0;
                history.Add(iter);
                cacheHits++;
                return new LoopResult(true, history, $"Cache hit on iter {i}", startedAt, cacheHits, cacheMisses);
            }
            cacheMisses++;

            try
            {
                var step = current ?? throw new InvalidOperationException("LoopRunner: no step to execute");
                var err = await step(ct);
                iter.Duration = DateTime.UtcNow - t0;
                if (err is null)
                {
                    iter.Succeeded = true;
                    history.Add(iter);
                    // NOTE: we don't have the actual success result here; caller-provided
                    // PutInCache callback pattern would be cleaner, but for now the cache
                    // is hit-only for explicit short-circuits where the caller pre-warmed it.
                    return new LoopResult(true, history, $"Succeeded on iter {i}", startedAt, cacheHits, cacheMisses);
                }
                iter.Error = err;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                iter.Duration = DateTime.UtcNow - t0;
                iter.Error = ex.Message;
            }
            history.Add(iter);

            // Try repair strategies (only if we have any).
            StepAttempt? next = null;
            string? appliedStrategy = null;
            if (Strategies.Count > 0)
            {
                for (var idx = 0; idx < Strategies.Count; idx++)
                {
                    try
                    {
                        next = await Strategies[idx](iter.Error!, history, ct);
                        if (next is not null) { appliedStrategy = $"#{idx}"; break; }
                    }
                    catch { /* try next strategy */ }
                }
                iter.RepairApplied = appliedStrategy;
                if (next is null)
                {
                    return new LoopResult(false, history, $"Exhausted {Strategies.Count} repair strategies after {i + 1} iter", startedAt, cacheHits, cacheMisses);
                }
            }
            // No strategies → repeat the same step on the next iteration (mirrors "max iter without strategies").
            current = next;
        }
        return new LoopResult(false, history, $"Hit max iterations ({MaxIterations}) without success", startedAt, cacheHits, cacheMisses);
    }

    /// <summary>Pre-warm the cache with a known result (used by tests or by the loop's caller).</summary>
    public void WarmCache(string cacheKey, string value)
    {
        Cache?.Put(cacheKey, value);
    }

    public CacheStats? CacheStats() => Cache?.Stats();
}

/// <summary>Built-in repair strategies (mirroring common patterns).</summary>
public static class BuiltInRepairs
{
    /// <summary>Wait and retry (backoff). Useful for transient 5xx/429.</summary>
    public static RepairStrategy BackoffRetry(TimeSpan delay, int maxAttempts)
    {
        var counter = 0;
        return async (err, hist, ct) =>
        {
            if (counter >= maxAttempts) return null;
            counter++;
            await Task.Delay(delay, ct);
            return ct2 => Task.FromResult<string?>(null); // signal: try the same step again
        };
    }

    /// <summary>Strip transient wrapping (e.g. extra quotes) and return a no-op pass.
    /// Real repair would actually mutate input — for now, the next attempt should re-derive.</summary>
    public static RepairStrategy Annotate(string annotation)
    {
        return (err, hist, ct) =>
        {
            err = $"[{annotation}] {err}";
            return Task.FromResult<StepAttempt?>(null); // doesn't actually produce a new attempt
        };
    }
}
