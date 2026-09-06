// Copyright (c) AeroCode
// 测试共享：日志捕获器（真实实现 ILogger 契约，只记录不输出）。
// 用于「可观测性」断言（如 MarkOnly findings WARN、DeprecationMonitor 命中 WARN）。
using Microsoft.Extensions.Logging;

namespace AeroCode.Tests.MoaTests;

/// <summary>捕获日志条目供断言（线程安全；Dispose 无副作用）。</summary>
public sealed class CapturingLogger<T> : ILogger<T>, IDisposable
{
    private readonly object _sync = new();
    private readonly List<CapturedLogEntry> _entries = new();

    public sealed record CapturedLogEntry(LogLevel Level, string Message, Exception? Exception);

    public IReadOnlyList<CapturedLogEntry> Entries
    {
        get { lock (_sync) return _entries.ToList(); }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => this;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        lock (_sync)
        {
            _entries.Add(new CapturedLogEntry(logLevel, formatter(state, exception), exception));
        }
    }

    public void Dispose()
    {
        // BeginScope 返回 this：Dispose 只清记录，不影响断言后续读取由调用方时序保证。
    }
}
