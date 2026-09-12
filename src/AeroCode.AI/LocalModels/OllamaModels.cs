// Copyright (c) AeroCode
// LocalModels — 本地大模型运行时 DTO（LOCAL_LLM_SPEC P1）。
// Ollama 原生 /api 的响应模型。诚实语义：字段缺省为空/0，绝不臆造。
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AeroCode.AI.LocalModels;

/// <summary>
/// Ollama 调用统一结果包装：成功带值，失败带原因。HTTP 错误、超时、网络中断、
/// JSON 解析失败一律收敛为失败结果，不抛裸异常、不伪造成功（对齐 MoaGatewayClient 口径）。
/// </summary>
public sealed record OllamaResult<T>
{
    public bool IsSuccess { get; init; }
    public T? Value { get; init; }
    public string? Error { get; init; }
    public int? StatusCode { get; init; }
    public bool IsTimeout { get; init; }

    public static OllamaResult<T> Ok(T value, int statusCode = 200)
        => new() { IsSuccess = true, Value = value, StatusCode = statusCode };

    public static OllamaResult<T> Fail(string error, int? statusCode = null, bool isTimeout = false)
        => new() { IsSuccess = false, Error = error, StatusCode = statusCode, IsTimeout = isTimeout };
}

/// <summary>Ollama 模型条目（/api/tags 的 models[]）。</summary>
public sealed record OllamaModel
{
    /// <summary>模型名（如 "qwen2.5:1.5b"）。</summary>
    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;

    /// <summary>模型标识（通常同 Name）。</summary>
    [JsonPropertyName("model")] public string Model { get; init; } = string.Empty;

    /// <summary>磁盘占用（字节）。</summary>
    [JsonPropertyName("size")] public long Size { get; init; }

    /// <summary>权重摘要（sha256）。</summary>
    [JsonPropertyName("digest")] public string Digest { get; init; } = string.Empty;

    /// <summary>最近修改时间（RFC3339 字符串，原样保留）。</summary>
    [JsonPropertyName("modified_at")] public string ModifiedAt { get; init; } = string.Empty;

    /// <summary>模型细节（格式/家族/参数量/量化）。</summary>
    [JsonPropertyName("details")] public OllamaModelDetails? Details { get; init; }

    /// <summary>人类可读大小。</summary>
    [JsonIgnore]
    public string DisplaySize => Size switch
    {
        < 1024 => $"{Size}B",
        < 1024 * 1024 => $"{Size / 1024.0:F1}KB",
        < 1024L * 1024 * 1024 => $"{Size / (1024.0 * 1024.0):F1}MB",
        _ => $"{Size / (1024.0 * 1024.0 * 1024.0):F2}GB",
    };
}

/// <summary>模型细节（GGUF 格式/家族/参数量/量化等级）。</summary>
public sealed record OllamaModelDetails
{
    [JsonPropertyName("parent_model")] public string ParentModel { get; init; } = string.Empty;

    /// <summary>权重格式（如 "gguf"）。</summary>
    [JsonPropertyName("format")] public string Format { get; init; } = string.Empty;

    /// <summary>模型家族（如 "qwen2"）。</summary>
    [JsonPropertyName("family")] public string Family { get; init; } = string.Empty;

    [JsonPropertyName("families")] public IReadOnlyList<string> Families { get; init; } = new List<string>();

    /// <summary>参数量（如 "1.5B"）。</summary>
    [JsonPropertyName("parameter_size")] public string ParameterSize { get; init; } = string.Empty;

    /// <summary>量化等级（如 "Q4_K_M"）。</summary>
    [JsonPropertyName("quantization_level")] public string QuantizationLevel { get; init; } = string.Empty;
}

/// <summary>/api/show 响应（模型详情，含 modelfile/parameters/template 等，此处取常用字段）。</summary>
public sealed record OllamaModelShow
{
    [JsonPropertyName("modelfile")] public string ModelFile { get; init; } = string.Empty;
    [JsonPropertyName("parameters")] public string Parameters { get; init; } = string.Empty;
    [JsonPropertyName("template")] public string Template { get; init; } = string.Empty;
    [JsonPropertyName("details")] public OllamaModelDetails? Details { get; init; }
}

/// <summary>/api/pull 流式进度（NDJSON 逐行）。</summary>
public sealed record OllamaPullProgress
{
    /// <summary>阶段描述（"pulling manifest"/"downloading ..."/"success" 等）。</summary>
    [JsonPropertyName("status")] public string Status { get; init; } = string.Empty;

    [JsonPropertyName("digest")] public string? Digest { get; init; }

    /// <summary>总字节（下载层）。</summary>
    [JsonPropertyName("total")] public long? Total { get; init; }

    /// <summary>已完成字节。</summary>
    [JsonPropertyName("completed")] public long? Completed { get; init; }

    /// <summary>错误信息（Ollama 失败时流式输出 {"error":"..."}；M-1：不再静默丢弃）。</summary>
    [JsonPropertyName("error")] public string? Error { get; init; }

    /// <summary>下载进度 0..1（无 total 时为 null）。</summary>
    [JsonIgnore]
    public double? Fraction => Total is > 0 && Completed is not null
        ? (double)Completed.Value / Total.Value
        : null;

    /// <summary>是否终态成功。</summary>
    [JsonIgnore]
    public bool IsDone => string.Equals(Status, "success", System.StringComparison.OrdinalIgnoreCase);

    /// <summary>是否失败项（携带 error）。</summary>
    [JsonIgnore]
    public bool IsError => !string.IsNullOrWhiteSpace(Error);
}

/// <summary>/api/version 响应。</summary>
public sealed record OllamaVersion
{
    [JsonPropertyName("version")] public string Version { get; init; } = string.Empty;
}
