using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AeroAgent.Moa.Gateway;

/// <summary>
/// 一次 moa-gateway-pro <c>POST /v1/moa/execute</c> 调用的结果包装。
/// 契约来源：moa-gateway-pro v3.1.1 <c>routes/moa.py</c> + <c>MoAResult.to_dict()</c>。
/// 网关失败语义（v3.1.1 审计修复，不静默降级）：
/// 参考模型全失败 → 502 + 逐模型证据；mock 禁用 → 503；上游超时 → 504。
/// </summary>
public sealed record MoaGatewayExecuteRequest
{
    /// <summary>本轮任务文本（映射为 messages 数组的最后一条 user 消息）。</summary>
    public required string Query { get; init; }

    /// <summary>
    /// 对话上下文（可选）：按时间升序的历史消息，映射为 messages 数组中
    /// 最后一条 user 消息之前的部分。null/空 = 单轮。
    /// </summary>
    public IReadOnlyList<MoaGatewayChatMessage>? Context { get; init; }

    /// <summary>网关预设（fast/balanced/quality/chinese_battalion/…）；null = 网关默认 preset。</summary>
    public string? Preset { get; init; }

    /// <summary>策略族覆盖（single/compose/judge/chain/pipeline/layered/…）；null = preset 自带。</summary>
    public string? Strategy { get; init; }

    /// <summary>参考模型数量（1..8）；null = preset 默认。</summary>
    public int? ReferenceCount { get; init; }

    /// <summary>critic 审查轮数（0..5）；null = preset 默认。</summary>
    public int? CriticRounds { get; init; }

    /// <summary>采样温度（0..2）；null = 网关默认 0.6。</summary>
    public double? Temperature { get; init; }

    /// <summary>最大生成 token（1..32000）；null = 网关默认 4096。</summary>
    public int? MaxTokens { get; init; }
}

/// <summary>OpenAI 式消息（role + content），用于构造 execute 请求的 messages 数组。</summary>
public sealed record MoaGatewayChatMessage(string Role, string Content);

/// <summary>execute 响应信封中的单条参考模型记录（<c>references[]</c>）。</summary>
public sealed record MoaReferenceResult
{
    [JsonPropertyName("model_id")] public string ModelId { get; init; } = string.Empty;
    [JsonPropertyName("role")] public string? Role { get; init; }
    [JsonPropertyName("success")] public bool Success { get; init; }
    [JsonPropertyName("latency_ms")] public double LatencyMs { get; init; }
    [JsonPropertyName("cost")] public double Cost { get; init; }
    [JsonPropertyName("tokens")] public long Tokens { get; init; }

    /// <summary>参考回答预览（网关截断至 300 字符）。</summary>
    [JsonPropertyName("preview")] public string Preview { get; init; } = string.Empty;
}

/// <summary>execute 响应信封中的单条 critic 审查记录（<c>critics[]</c>）。</summary>
public sealed record MoaCriticResult
{
    [JsonPropertyName("model_id")] public string ModelId { get; init; } = string.Empty;
    [JsonPropertyName("success")] public bool Success { get; init; }
    [JsonPropertyName("issues_count")] public int IssuesCount { get; init; }
    [JsonPropertyName("suggestions_count")] public int SuggestionsCount { get; init; }
    [JsonPropertyName("latency_ms")] public double LatencyMs { get; init; }
    [JsonPropertyName("cost")] public double Cost { get; init; }
}

/// <summary>execute 响应信封中的 chain 策略单步记录（<c>chain_steps[]</c>）。</summary>
public sealed record MoaChainStepResult
{
    [JsonPropertyName("step")] public int Step { get; init; }
    [JsonPropertyName("strategy")] public string? Strategy { get; init; }
    [JsonPropertyName("preset")] public string? Preset { get; init; }
    [JsonPropertyName("latency_ms")] public double LatencyMs { get; init; }
    [JsonPropertyName("cost")] public double Cost { get; init; }
    [JsonPropertyName("preview")] public string Preview { get; init; } = string.Empty;
}

/// <summary>
/// <c>POST /v1/moa/execute</c> 的完整响应信封（<c>MoAResult.to_dict()</c> 的强类型映射）。
/// 松散类型字段（layer_outputs/ranker_output/pipeline_stages）保留 <see cref="JsonElement"/>
/// 原样，避免网关版本演进中的结构变化导致整体解析失败。
/// </summary>
public sealed record MoaExecuteResult
{
    [JsonPropertyName("request_id")] public string RequestId { get; init; } = string.Empty;
    [JsonPropertyName("query")] public string Query { get; init; } = string.Empty;
    [JsonPropertyName("preset")] public string? Preset { get; init; }
    [JsonPropertyName("strategy")] public string? Strategy { get; init; }
    [JsonPropertyName("references")] public IReadOnlyList<MoaReferenceResult> References { get; init; } = [];
    [JsonPropertyName("critics")] public IReadOnlyList<MoaCriticResult> Critics { get; init; } = [];
    [JsonPropertyName("chain_steps")] public IReadOnlyList<MoaChainStepResult> ChainSteps { get; init; } = [];
    [JsonPropertyName("aggregator_model")] public string? AggregatorModel { get; init; }
    [JsonPropertyName("winner_model")] public string? WinnerModel { get; init; }
    [JsonPropertyName("ranker_output")] public JsonElement? RankerOutput { get; init; }
    [JsonPropertyName("layers_count")] public int LayersCount { get; init; }
    [JsonPropertyName("layer_outputs")] public JsonElement? LayerOutputs { get; init; }
    [JsonPropertyName("consensus_score")] public double ConsensusScore { get; init; }
    [JsonPropertyName("iterations")] public int Iterations { get; init; }
    [JsonPropertyName("total_latency_ms")] public double TotalLatencyMs { get; init; }
    [JsonPropertyName("total_cost")] public double TotalCost { get; init; }

    /// <summary>网关内部是否动用了兜底路径（如聚合器失败后的降级合成）。</summary>
    [JsonPropertyName("fallback_used")] public bool FallbackUsed { get; init; }

    /// <summary>
    /// D6 诚实性标注：任一 reference/aggregator/critic 来自 MockProvider 即为 true。
    /// 与响应头 <c>X-MOA-Mock</c> 同源；两者任一为真，调用方必须把结果视为显式 mock。
    /// </summary>
    [JsonPropertyName("mock")] public bool Mock { get; init; }

    [JsonPropertyName("pipeline_stages")] public JsonElement? PipelineStages { get; init; }

    /// <summary>聚合后的最终答复（本端呈现给用户的内容）。</summary>
    [JsonPropertyName("final_content")] public string FinalContent { get; init; } = string.Empty;
}

/// <summary><c>GET /health</c>（legacy，无鉴权）响应。D6：mock 端点数量显式可见。</summary>
public sealed record MoaGatewayHealth
{
    [JsonPropertyName("status")] public string Status { get; init; } = string.Empty;
    [JsonPropertyName("version")] public string Version { get; init; } = string.Empty;
    [JsonPropertyName("endpoints_total")] public int EndpointsTotal { get; init; }
    [JsonPropertyName("endpoints_enabled")] public int EndpointsEnabled { get; init; }
    [JsonPropertyName("endpoints_healthy")] public int EndpointsHealthy { get; init; }
    [JsonPropertyName("mock_endpoints_count")] public int MockEndpointsCount { get; init; }
    [JsonPropertyName("real_endpoints_count")] public int RealEndpointsCount { get; init; }
    [JsonPropertyName("mock_mode")] public string MockMode { get; init; } = string.Empty;
}

/// <summary><c>GET /v1/moa/presets</c> 响应中的单个预设配置。</summary>
public sealed record MoaPresetInfo
{
    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;

    /// <summary>预设的完整配置（strategy/reference_count/critic_rounds/…），结构随网关版本演进，保留原样。</summary>
    [JsonPropertyName("config")] public JsonElement? Config { get; init; }
}

/// <summary><c>GET /v1/moa/presets</c> 响应。</summary>
public sealed record MoaPresetsResponse
{
    [JsonPropertyName("presets")] public IReadOnlyList<MoaPresetInfo> Presets { get; init; } = [];
    [JsonPropertyName("default")] public string Default { get; init; } = string.Empty;
}

/// <summary>
/// 网关调用的统一结果包装：成功带值，失败带原因——HTTP 错误、超时、网络中断、
/// JSON 解析失败全部如实收敛为失败结果，绝不向调用方抛裸异常、绝不伪造成功。
/// </summary>
/// <typeparam name="T">成功时的响应类型。</typeparam>
public sealed record GatewayResult<T>
{
    /// <summary>true = HTTP 2xx 且响应体成功解析为 <typeparamref name="T"/>。</summary>
    public bool IsSuccess { get; init; }

    /// <summary>成功时的强类型响应；失败时为 default。</summary>
    public T? Value { get; init; }

    /// <summary>失败原因（人类可读，含 HTTP 状态/异常消息/解析错误）。</summary>
    public string? Error { get; init; }

    /// <summary>HTTP 状态码；网络层失败（未收到响应）为 null。</summary>
    public int? StatusCode { get; init; }

    /// <summary>
    /// D6 mock 标注透传：响应头 <c>X-MOA-Mock: true</c> 命中即为 true。
    /// 对 execute 调用，调用方应同时检查 <see cref="MoaExecuteResult.Mock"/> 字段（两者同源）。
    /// </summary>
    public bool IsMock { get; init; }

    /// <summary>true = 本端超时（请求未在限定时间内收到响应）。</summary>
    public bool IsTimeout { get; init; }

    public static GatewayResult<T> Ok(T value, bool isMock = false, int statusCode = 200) =>
        new() { IsSuccess = true, Value = value, IsMock = isMock, StatusCode = statusCode };

    public static GatewayResult<T> Fail(string error, int? statusCode = null, bool isTimeout = false, bool isMock = false) =>
        new() { IsSuccess = false, Error = error, StatusCode = statusCode, IsTimeout = isTimeout, IsMock = isMock };
}
