// Copyright (c) AeroCode V3.0
// AiResiliencePipeline — Polly v8 resilience pipeline for AI provider calls.
// Adds retry (transient HTTP), circuit breaker (5xx burst protection),
// and per-request timeout. Pipeline is shared per provider to preserve
// circuit-breaker state across calls.
using System;
using System.Net.Http;
using System.Threading;
using System.Threading.RateLimiting;
using System.Threading.Tasks;
using Polly;
using Polly.CircuitBreaker;
using Polly.RateLimiting;
using Polly.Retry;
using Polly.Timeout;

namespace AeroCode.AI.Resilience;

/// <summary>
/// Configurable knobs for the resilience pipeline. Read from AppSettings if available,
/// else defaults below.
/// </summary>
public sealed class ResilienceOptions
{
    public int MaxRetryAttempts { get; set; } = 3;
    public int RetryBaseDelayMs { get; set; } = 400;
    public int AttemptTimeoutSeconds { get; set; } = 60;
    public double CircuitBreakerFailureRatio { get; set; } = 0.5;
    public int CircuitBreakerSamplingDurationSeconds { get; set; } = 30;
    public int CircuitBreakerMinThroughput { get; set; } = 5;
    public int CircuitBreakerBreakDurationSeconds { get; set; } = 15;
    public int RateLimitPermitsPerSecond { get; set; } = 0; // 0 = disabled
    public int RateLimitBurst { get; set; } = 0;             // 0 = disabled
}

/// <summary>
/// Builds and owns a single Polly <see cref="ResiliencePipeline"/> that wraps
/// every AI provider HTTP call. One pipeline per provider id (stateful).
/// </summary>
public sealed class AiResiliencePipeline
{
    private readonly ResiliencePipeline _pipeline;
    public ResilienceOptions Options { get; }

    public AiResiliencePipeline(ResilienceOptions? options = null)
    {
        Options = options ?? new ResilienceOptions();
        _pipeline = BuildPipeline(Options);
    }

    /// <summary>Execute <paramref name="action"/> under the shared pipeline.</summary>
    public ValueTask<T> ExecuteAsync<T>(Func<CancellationToken, ValueTask<T>> action, CancellationToken ct = default)
        => _pipeline.ExecuteAsync(action, ct);

    public ValueTask ExecuteAsync(Func<CancellationToken, ValueTask> action, CancellationToken ct = default)
        => _pipeline.ExecuteAsync(action, ct);

    private static ResiliencePipeline BuildPipeline(ResilienceOptions o)
    {
        var builder = new ResiliencePipelineBuilder();

        // 1) Rate limit (outermost so we drop requests before spending time on retries)
        if (o.RateLimitPermitsPerSecond > 0 || o.RateLimitBurst > 0)
        {
            var permits = o.RateLimitPermitsPerSecond > 0 ? o.RateLimitPermitsPerSecond : 1;
            var burst = o.RateLimitBurst > 0 ? o.RateLimitBurst : permits;
            builder.AddRateLimiter(new SlidingWindowRateLimiter(new SlidingWindowRateLimiterOptions
            {
                PermitLimit = permits,
                Window = TimeSpan.FromSeconds(1),
                SegmentsPerWindow = 4,
                QueueLimit = burst,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                AutoReplenishment = true
            }));
        }

        // 2) Per-attempt timeout (cancels a single HTTP call that's hanging)
        if (o.AttemptTimeoutSeconds > 0)
        {
            builder.AddTimeout(new TimeoutStrategyOptions
            {
                Timeout = TimeSpan.FromSeconds(o.AttemptTimeoutSeconds),
                OnTimeout = args => default
            });
        }

        // 3) Retry transient HTTP failures + 5xx + 429 (rate-limited)
        if (o.MaxRetryAttempts > 0)
        {
            builder.AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = o.MaxRetryAttempts,
                BackoffType = DelayBackoffType.Exponential,
                Delay = TimeSpan.FromMilliseconds(o.RetryBaseDelayMs),
                UseJitter = true,
                ShouldHandle = new PredicateBuilder()
                    .Handle<HttpRequestException>()
                    .Handle<TaskCanceledException>(ex => !ex.CancellationToken.IsCancellationRequested)
                    .Handle<TimeoutRejectedException>()
                    .Handle<AiTransientHttpException>()
            });
        }

        // 4) Circuit breaker (innermost; opens when many recent calls failed)
        if (o.CircuitBreakerMinThroughput > 0)
        {
            builder.AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                FailureRatio = o.CircuitBreakerFailureRatio,
                SamplingDuration = TimeSpan.FromSeconds(o.CircuitBreakerSamplingDurationSeconds),
                MinimumThroughput = o.CircuitBreakerMinThroughput,
                BreakDuration = TimeSpan.FromSeconds(o.CircuitBreakerBreakDurationSeconds),
                ShouldHandle = new PredicateBuilder()
                    .Handle<HttpRequestException>()
                    .Handle<TimeoutRejectedException>()
                    .Handle<AiTransientHttpException>()
            });
        }

        return builder.Build();
    }
}

/// <summary>
/// Marker exception that providers throw on retryable HTTP responses (5xx / 429).
/// Converted to typed exception so retry/circuit-breaker strategies can pick it up
/// without coupling to HttpResponseMessage.
/// </summary>
public sealed class AiTransientHttpException : Exception
{
    public int StatusCode { get; }
    public string Body { get; }
    public AiTransientHttpException(int statusCode, string body)
        : base($"Transient HTTP failure: {statusCode}: {Truncate(body, 256)}")
    {
        StatusCode = statusCode;
        Body = body;
    }

    private static string Truncate(string s, int max) => string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "…";
}
