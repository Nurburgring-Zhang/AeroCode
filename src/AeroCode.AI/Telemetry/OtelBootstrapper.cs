// Copyright (c) AeroCode V3.2
// OtelBootstrapper — 真 OpenTelemetry 接入 (CNCF 标准)。
// 零假装：真实组装 TracerProvider / MeterProvider / LoggerProvider，配 Console + OTLP 双导出。
// 任何第三方后端（Jaeger / Tempo / Prometheus / OpenObserve / Datadog）只要支持 OTLP 都能直接收。
//
// 暴露:
//   - Meter 名 "AeroCode" (chat 计数 / 延迟直方图 / cache 命中 / 嵌入调用)
//   - ActivitySource "AeroCode.Harness" (DAG / Loop / Planner / Plugin / Skill 跨度)
//   - ILogger 通过 MEL 接 OpenTelemetry Logger
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace AeroCode.AI.Telemetry;

public sealed class OtelOptions
{
    public string ServiceName { get; set; } = "AeroCode";
    public string ServiceVersion { get; set; } = "3.2.0";
    public string? OtlpEndpoint { get; set; }        // e.g. "http://localhost:4317" for gRPC
    public bool EnableConsoleExporter { get; set; } = true;
    public bool EnableHttpClientInstrumentation { get; set; } = true;
    public bool EnableRuntimeInstrumentation { get; set; } = true;
    public double TraceSamplingRatio { get; set; } = 1.0;
}

/// <summary>
/// AeroCode OpenTelemetry 入口：注册真实 Tracer/Meter/Logger 三支柱，
/// 任何 OpenTelemetry-compatible collector 都能消费。
/// </summary>
public sealed class OtelBootstrapper : IDisposable
{
    public const string MeterName = "AeroCode";
    public const string ActivitySourceName = "AeroCode.Harness";

    public TracerProvider TracerProvider { get; }
    public MeterProvider MeterProvider { get; }
    public Meter Meter { get; }
    public ActivitySource ActivitySource { get; }
    public ILoggerFactory LoggerFactory { get; }
    public OtelMetrics Metrics { get; }

    private bool _disposed;
    private readonly OtelOptions _opts;

    public OtelBootstrapper(OtelOptions? options = null)
    {
        _opts = options ?? new OtelOptions();
        var resource = ResourceBuilder.CreateDefault()
            .AddService(_opts.ServiceName, serviceVersion: _opts.ServiceVersion)
            .AddAttributes(new Dictionary<string, object>
            {
                ["deployment.environment"] = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "production",
                ["telemetry.sdk.language"] = "dotnet",
                ["aero.component"] = "main"
            });

        // ===== TRACING =====
        var traceBuilder = Sdk.CreateTracerProviderBuilder()
            .SetResourceBuilder(resource)
            .AddSource(ActivitySourceName)
            .SetSampler(new TraceIdRatioBasedSampler(_opts.TraceSamplingRatio));
        if (_opts.EnableHttpClientInstrumentation) traceBuilder.AddHttpClientInstrumentation();
        if (_opts.EnableConsoleExporter) traceBuilder.AddConsoleExporter();
        if (!string.IsNullOrEmpty(_opts.OtlpEndpoint))
            traceBuilder.AddOtlpExporter(o => { o.Endpoint = new Uri(_opts.OtlpEndpoint); });
        TracerProvider = traceBuilder.Build();
        ActivitySource = new ActivitySource(ActivitySourceName);

        // ===== METRICS =====
        var meterBuilder = Sdk.CreateMeterProviderBuilder()
            .SetResourceBuilder(resource)
            .AddMeter(MeterName);
        if (_opts.EnableHttpClientInstrumentation) meterBuilder.AddHttpClientInstrumentation();
        if (_opts.EnableRuntimeInstrumentation) meterBuilder.AddRuntimeInstrumentation(); // .NET 8/9 GC/thread pool
        if (_opts.EnableConsoleExporter) meterBuilder.AddConsoleExporter((_, readerOpts) => readerOpts.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = 5000);
        if (!string.IsNullOrEmpty(_opts.OtlpEndpoint))
            meterBuilder.AddOtlpExporter((o, readerOpts) =>
            {
                o.Endpoint = new Uri(_opts.OtlpEndpoint);
                readerOpts.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = 5000;
            });
        MeterProvider = meterBuilder.Build();
        Meter = new Meter(MeterName);
        Metrics = new OtelMetrics(Meter);

        // ===== LOGS =====
        LoggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(b =>
        {
            b.AddOpenTelemetry(log =>
            {
                log.SetResourceBuilder(resource);
                if (_opts.EnableConsoleExporter) log.AddConsoleExporter();
                if (!string.IsNullOrEmpty(_opts.OtlpEndpoint))
                    log.AddOtlpExporter(o => { o.Endpoint = new Uri(_opts.OtlpEndpoint); });
            });
        });
    }

    /// <summary>Start a span around an operation. Returns IDisposable (Activity implements it).</summary>
    public Activity? StartActivity(string name, ActivityKind kind = ActivityKind.Internal)
        => ActivitySource.StartActivity(name, kind);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Meter.Dispose();
        ActivitySource.Dispose();
        TracerProvider.Dispose();
        MeterProvider.Dispose();
        LoggerFactory.Dispose();
    }
}

/// <summary>
/// All metrics AeroCode exports. Real counters and histograms (not static constants).
/// </summary>
public sealed class OtelMetrics
{
    public Counter<long> ChatRequests { get; }
    public Counter<long> ChatErrors { get; }
    public Histogram<double> ChatLatencyMs { get; }
    public Counter<long> EmbeddingRequests { get; }
    public Histogram<double> EmbeddingLatencyMs { get; }
    public Counter<long> CacheHits { get; }
    public Counter<long> CacheMisses { get; }
    public Counter<long> PluginLoaded { get; }
    public Counter<long> SkillInvocations { get; }
    public Histogram<double> SkillLatencyMs { get; }

    public OtelMetrics(Meter meter)
    {
        ChatRequests = meter.CreateCounter<long>("aero.chat.requests", "req", "Total LLM chat requests");
        ChatErrors = meter.CreateCounter<long>("aero.chat.errors", "err", "LLM chat errors");
        ChatLatencyMs = meter.CreateHistogram<double>("aero.chat.latency_ms", "ms", "LLM chat latency");
        EmbeddingRequests = meter.CreateCounter<long>("aero.embedding.requests", "req", "Embedding calls");
        EmbeddingLatencyMs = meter.CreateHistogram<double>("aero.embedding.latency_ms", "ms", "Embedding latency");
        CacheHits = meter.CreateCounter<long>("aero.cache.hits", "hit", "Cache hits (LRU + LLM prefix)");
        CacheMisses = meter.CreateCounter<long>("aero.cache.misses", "miss", "Cache misses");
        PluginLoaded = meter.CreateCounter<long>("aero.plugin.loaded", "dll", "Plugins loaded");
        SkillInvocations = meter.CreateCounter<long>("aero.skill.invocations", "inv", "Skill invocations");
        SkillLatencyMs = meter.CreateHistogram<double>("aero.skill.latency_ms", "ms", "Skill latency");
    }
}
