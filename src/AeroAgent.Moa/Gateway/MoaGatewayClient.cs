using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace AeroAgent.Moa.Gateway;

/// <summary>
/// moa-gateway-pro 客户端选项。
/// 环境变量约定（与官方 CLI 一致）：<c>MOA_GATEWAY_URL</c>（默认 http://127.0.0.1:8910）、
/// <c>MOA_GATEWAY_KEY</c>（API Key，写入 config.yaml 的 auth.gateway_api_keys 后生效）。
/// </summary>
public sealed record MoaGatewayClientOptions
{
    /// <summary>网关基地址（默认本机 8910，moa-gateway-pro config.yaml 默认端口）。</summary>
    public Uri BaseUrl { get; init; } = new("http://127.0.0.1:8910");

    /// <summary>API Key（Authorization: Bearer）。null/空 = 不带鉴权头（仅健康探活可用）。</summary>
    public string? ApiKey { get; init; }

    /// <summary>单次调用超时。execute 为多模型编排，默认给足 120 秒。</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>健康探活专用超时（短），避免探活拖慢降级判定。</summary>
    public TimeSpan HealthTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>从环境变量构造选项（MOA_GATEWAY_URL / MOA_GATEWAY_KEY），非法 URL 时回退默认值。</summary>
    public static MoaGatewayClientOptions FromEnvironment()
    {
        var url = Environment.GetEnvironmentVariable("MOA_GATEWAY_URL");
        var key = Environment.GetEnvironmentVariable("MOA_GATEWAY_KEY");
        var baseUrl = Uri.TryCreate(url, UriKind.Absolute, out var parsed) &&
                      (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps)
            ? parsed
            : new Uri("http://127.0.0.1:8910");
        return new MoaGatewayClientOptions
        {
            BaseUrl = baseUrl,
            ApiKey = string.IsNullOrWhiteSpace(key) ? null : key,
        };
    }
}

/// <summary>
/// moa-gateway-pro v3.1.1 真实 HTTP 客户端（FastAPI 网关，默认端口 8910）。
/// 能力映射（契约来源：发行包 <c>routes/moa.py</c> / <c>routes/health.py</c>）：
/// <list type="bullet">
/// <item><c>POST /v1/moa/execute</c> → <see cref="ExecuteAsync"/>：原生 MoA 编排信封
/// （references/critics/consensus/mock/final_content），响应头 <c>X-MOA-Mock</c> 透传 D6 标注。</item>
/// <item><c>GET /health</c> → <see cref="HealthAsync"/>：legacy 探活（无鉴权）。</item>
/// <item><c>GET /health/ready</c> → <see cref="IsReadyAsync"/>：就绪探针（未就绪 503）。</item>
/// <item><c>GET /v1/moa/presets</c> → <see cref="GetPresetsAsync"/>。</item>
/// </list>
/// v3.1.1 没有独立的 references/critics 端点——它们内嵌在 execute 响应信封中；
/// <see cref="GetReferencesAsync"/>/<see cref="GetCriticsAsync"/> 因此是"真实 execute + 信封投影"。
/// 诚实性：HTTP 错误/超时/网络中断/解析失败一律返回失败的 <see cref="GatewayResult{T}"/>，
/// 不抛裸异常、不伪造成功；调用方取消（token）如实向上抛 <see cref="OperationCanceledException"/>。
/// </summary>
public sealed class MoaGatewayClient : IDisposable
{
    /// <summary>D6 显式 mock 标注响应头（网关 <c>mock.header_name</c> 默认值）。</summary>
    public const string MockHeaderName = "X-MOA-Mock";

    private const string ExecutePath = "/v1/moa/execute";
    private const string HealthPath = "/health";
    private const string ReadyPath = "/health/ready";
    private const string PresetsPath = "/v1/moa/presets";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;

    /// <summary>使用自有 HttpClient（按选项创建并持有生命周期）。</summary>
    public MoaGatewayClient(MoaGatewayClientOptions options)
        : this(options, handler: null, httpClient: null)
    {
    }

    /// <summary>
    /// 测试/管道定制构造：传入 <see cref="HttpMessageHandler"/>（如 FakeHttpHandler）
    /// 时由本实例组装 HttpClient；两者都给时优先 <paramref name="httpClient"/>。
    /// </summary>
    public MoaGatewayClient(
        MoaGatewayClientOptions options,
        HttpMessageHandler? handler = null,
        HttpClient? httpClient = null)
    {
        Options = options ?? throw new ArgumentNullException(nameof(options));
        if (httpClient is not null)
        {
            _http = httpClient;
            _ownsHttpClient = false;
        }
        else
        {
            _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: true);
            _ownsHttpClient = true;
        }

        if (_http.BaseAddress is null)
        {
            _http.BaseAddress = options.BaseUrl;
        }

        // execute 超时按选项；探活用 per-request CTS 单独收紧（见 HealthAsync）。
        if (_ownsHttpClient)
        {
            _http.Timeout = options.Timeout;
        }
    }

    public MoaGatewayClientOptions Options { get; }

    /// <summary>
    /// 调用 <c>POST /v1/moa/execute</c> 执行原生 MoA 编排。
    /// 请求体 = OpenAI 式 messages 数组 + MoA 扩展字段（preset/strategy/reference_count/critic_rounds/…）；
    /// 响应解析为强类型信封，<c>X-MOA-Mock</c> 头透传到 <see cref="GatewayResult{T}.IsMock"/>。
    /// </summary>
    public Task<GatewayResult<MoaExecuteResult>> ExecuteAsync(
        MoaGatewayExecuteRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Query))
        {
            return Task.FromResult(
                GatewayResult<MoaExecuteResult>.Fail("request.Query must not be empty"));
        }

        var payload = BuildExecutePayload(request);
        return SendAsync<MoaExecuteResult>(
            HttpMethod.Post, ExecutePath, payload, Options.Timeout, ct);
    }

    /// <summary>
    /// 探活：<c>GET /health</c>（无鉴权）。进程存活且 HTTP 就绪即成功；
    /// 值对象含 D6 显式 mock 可见性字段（mock_endpoints_count / mock_mode）。
    /// </summary>
    public Task<GatewayResult<MoaGatewayHealth>> HealthAsync(CancellationToken ct = default) =>
        SendAsync<MoaGatewayHealth>(HttpMethod.Get, HealthPath, payload: null, Options.HealthTimeout, ct);

    /// <summary>
    /// 就绪探针：<c>GET /health/ready</c>。true = 2xx；false = 503（未就绪）或任何失败。
    /// </summary>
    public async Task<bool> IsReadyAsync(CancellationToken ct = default)
    {
        var result = await SendAsync<JsonElement>(HttpMethod.Get, ReadyPath, payload: null, Options.HealthTimeout, ct);
        return result.IsSuccess;
    }

    /// <summary>拉取预设清单：<c>GET /v1/moa/presets</c>（需 API Key）。</summary>
    public Task<GatewayResult<MoaPresetsResponse>> GetPresetsAsync(CancellationToken ct = default) =>
        SendAsync<MoaPresetsResponse>(HttpMethod.Get, PresetsPath, payload: null, Options.Timeout, ct);

    /// <summary>
    /// 参考模型结果投影：v3.1.1 无独立 references 端点，本方法真实调用
    /// <c>/v1/moa/execute</c> 后返回信封中的 <c>references[]</c>（逐模型成败/延迟/成本/预览）。
    /// </summary>
    public async Task<GatewayResult<IReadOnlyList<MoaReferenceResult>>> GetReferencesAsync(
        MoaGatewayExecuteRequest request, CancellationToken ct = default)
    {
        var executed = await ExecuteAsync(request, ct);
        return executed.IsSuccess
            ? GatewayResult<IReadOnlyList<MoaReferenceResult>>.Ok(
                executed.Value!.References, executed.IsMock, executed.StatusCode ?? 200)
            : GatewayResult<IReadOnlyList<MoaReferenceResult>>.Fail(
                executed.Error ?? "execute failed", executed.StatusCode, executed.IsTimeout, executed.IsMock);
    }

    /// <summary>
    /// critic 审查结果投影：v3.1.1 无独立 critics 端点，本方法真实调用
    /// <c>/v1/moa/execute</c> 后返回信封中的 <c>critics[]</c>。
    /// </summary>
    public async Task<GatewayResult<IReadOnlyList<MoaCriticResult>>> GetCriticsAsync(
        MoaGatewayExecuteRequest request, CancellationToken ct = default)
    {
        var executed = await ExecuteAsync(request, ct);
        return executed.IsSuccess
            ? GatewayResult<IReadOnlyList<MoaCriticResult>>.Ok(
                executed.Value!.Critics, executed.IsMock, executed.StatusCode ?? 200)
            : GatewayResult<IReadOnlyList<MoaCriticResult>>.Fail(
                executed.Error ?? "execute failed", executed.StatusCode, executed.IsTimeout, executed.IsMock);
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _http.Dispose();
        }
    }

    // ---------------- 内部实现 ----------------

    private static object BuildExecutePayload(MoaGatewayExecuteRequest request)
    {
        var messages = new List<MoaWireMessage>(capacity: (request.Context?.Count ?? 0) + 1);
        if (request.Context is { Count: > 0 })
        {
            messages.AddRange(request.Context.Select(m => new MoaWireMessage(m.Role, m.Content)));
        }

        messages.Add(new MoaWireMessage("user", request.Query));

        return new ExecuteWireRequest
        {
            Model = "auto",
            Messages = messages,
            Preset = request.Preset,
            Strategy = request.Strategy,
            ReferenceCount = request.ReferenceCount,
            CriticRounds = request.CriticRounds,
            Temperature = request.Temperature,
            MaxTokens = request.MaxTokens,
        };
    }

    private async Task<GatewayResult<T>> SendAsync<T>(
        HttpMethod method,
        string path,
        object? payload,
        TimeSpan timeout,
        CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(method, path);
            if (payload is not null)
            {
                var json = JsonSerializer.Serialize(payload, JsonOptions);
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            }

            if (!string.IsNullOrWhiteSpace(Options.ApiKey))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Options.ApiKey);
            }

            response = await _http.SendAsync(request, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // 调用方取消：如实向上抛，不算网关失败。
        }
        catch (OperationCanceledException)
        {
            return GatewayResult<T>.Fail(
                $"gateway request to {path} timed out after {timeout.TotalSeconds:0.#}s",
                isTimeout: true);
        }
        catch (HttpRequestException ex)
        {
            return GatewayResult<T>.Fail(
                $"gateway unreachable at {Options.BaseUrl}{path}: {ex.Message}",
                statusCode: ex.StatusCode is null ? null : (int)ex.StatusCode);
        }

        using (response)
        {
            var isMock = ReadMockHeader(response);
            var statusCode = (int)response.StatusCode;

            string body;
            try
            {
                // 用联动超时令牌读响应体：仅用调用方 ct 时，慢速滴送服务器可让
                // ReadAsStringAsync 超出请求超时上限仍不返回。
                body = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // 调用方取消：如实向上抛。
            }
            catch (OperationCanceledException)
            {
                return GatewayResult<T>.Fail(
                    $"gateway response body read timed out after {timeout.TotalSeconds:0.#}s",
                    statusCode, isTimeout: true);
            }
            catch (Exception ex)
            {
                return GatewayResult<T>.Fail($"failed to read gateway response body: {ex.Message}", statusCode);
            }

            if (!response.IsSuccessStatusCode)
            {
                return GatewayResult<T>.Fail(
                    $"gateway returned HTTP {statusCode}: {ExtractErrorDetail(body)}",
                    statusCode, isMock: isMock);
            }

            try
            {
                var value = JsonSerializer.Deserialize<T>(body, JsonOptions);
                if (value is null)
                {
                    return GatewayResult<T>.Fail("gateway returned an empty response body", statusCode);
                }

                // D6 双通道：响应头与信封 mock 字段同源，任一为真即显式 mock。
                var bodyMock = value is MoaExecuteResult { Mock: true };
                return GatewayResult<T>.Ok(value, isMock || bodyMock, statusCode);
            }
            catch (JsonException ex)
            {
                return GatewayResult<T>.Fail(
                    $"gateway response was not valid JSON for {typeof(T).Name}: {ex.Message}",
                    statusCode);
            }
        }
    }

    private static bool ReadMockHeader(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues(MockHeaderName, out var values))
        {
            return values.Any(v => string.Equals(v, "true", StringComparison.OrdinalIgnoreCase));
        }

        return false;
    }

    /// <summary>FastAPI 错误体为 {"detail": "..."}；解析失败则原样截断返回。</summary>
    private static string ExtractErrorDetail(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "(empty body)";
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("detail", out var detail))
            {
                return detail.ToString();
            }
        }
        catch (JsonException)
        {
            // 非 JSON 错误体（代理/网关层的 HTML 等）：原样截断。
        }

        return body.Length <= 300 ? body : body[..300] + "…";
    }

    /// <summary>execute 请求体（OpenAI 式 + MoA 扩展字段；null 字段不序列化）。</summary>
    private sealed record ExecuteWireRequest
    {
        [JsonPropertyName("model")] public string Model { get; init; } = "auto";
        [JsonPropertyName("messages")] public required IReadOnlyList<MoaWireMessage> Messages { get; init; }
        [JsonPropertyName("preset")] public string? Preset { get; init; }
        [JsonPropertyName("strategy")] public string? Strategy { get; init; }
        [JsonPropertyName("reference_count")] public int? ReferenceCount { get; init; }
        [JsonPropertyName("critic_rounds")] public int? CriticRounds { get; init; }
        [JsonPropertyName("temperature")] public double? Temperature { get; init; }
        [JsonPropertyName("max_tokens")] public int? MaxTokens { get; init; }
    }

    private sealed record MoaWireMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);
}
