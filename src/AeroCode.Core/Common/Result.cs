using System;

namespace AeroCode.Core.Common;

/// <summary>
/// 轻量级 Result 类型，避免把异常作为业务控制流。
/// 服务层用 Result 显式表达成功/失败，调用方强制处理错误路径。
/// </summary>
public readonly struct Result<T>
{
    public bool IsSuccess { get; }
    public T? Value { get; }
    public string? Error { get; }
    public Exception? Exception { get; }

    private Result(bool ok, T? value, string? err, Exception? ex)
    {
        IsSuccess = ok;
        Value = value;
        Error = err;
        Exception = ex;
    }

    public static Result<T> Ok(T value) => new(true, value, null, null);
    public static Result<T> Fail(string error, Exception? ex = null) => new(false, default, error, ex);

    public override string ToString() =>
        IsSuccess ? $"Ok({Value})" : $"Fail({Error})";
}
