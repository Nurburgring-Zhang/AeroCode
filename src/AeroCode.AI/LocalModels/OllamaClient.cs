// Copyright (c) AeroCode
// OllamaClient — Ollama 本地运行时客户端（LOCAL_LLM_SPEC P1）。
// 覆盖原生 /api（version/tags/show/pull/delete）+ 可达性探活。
// 诚实语义：不可达/超时/解析失败一律返回失败的 OllamaResult 或抛出可识别异常，
// 绝不伪造"已连接/已加载"。HttpMessageHandler 可注入以便单元测试。
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AeroCode.AI.LocalModels;

/// <summary>
/// Ollama 本地服务客户端。默认基地址 <c>http://127.0.0.1:11434</c>（Ollama 原生 /api，不带 /v1）。
/// 只读管理面（version/tags/show）+ 变更面（pull/delete）。聊天走 <c>OllamaProvider</c> 的 /v1。
/// </summary>
public sealed class OllamaClient : IDisposable
{
    /// <summary>Ollama 默认原生 API 基地址。</summary>
    public static readonly Uri DefaultBaseUrl = new("http://127.0.0.1:11434");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;

    public Uri BaseUrl { get; }

    /// <summary>探活/只读调用的短超时，避免 UI 卡住。</summary>
    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>pull 等长操作的超时（pull 本身经流式进度推进，这里给单次 HTTP 上限）。</summary>
    public TimeSpan LongTimeout { get; init; } = TimeSpan.FromMinutes(30);

    public OllamaClient(Uri? baseUrl = null, HttpMessageHandler? handler = null, HttpClient? httpClient = null)
    {
        BaseUrl = baseUrl ?? DefaultBaseUrl;
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
            _http.BaseAddress = BaseUrl;
        }
    }

    /// <summary>可达性探活：GET /api/version（短超时）。true = Ollama 服务在跑。</summary>
    public async Task<bool> IsReachableAsync(CancellationToken ct = default)
    {
        var result = await GetVersionAsync(ct).ConfigureAwait(false);
        return result.IsSuccess;
    }

    /// <summary>GET /api/version。</summary>
    public Task<OllamaResult<OllamaVersion>> GetVersionAsync(CancellationToken ct = default)
        => SendAsync<OllamaVersion>(HttpMethod.Get, "/api/version", payload: null, ProbeTimeout, ct);

    /// <summary>GET /api/tags：列出已安装模型。</summary>
    public async Task<OllamaResult<IReadOnlyList<OllamaModel>>> ListModelsAsync(CancellationToken ct = default)
    {
        var raw = await SendAsync<OllamaTagsResponse>(HttpMethod.Get, "/api/tags", payload: null, ProbeTimeout, ct)
            .ConfigureAwait(false);
        if (!raw.IsSuccess)
        {
            return OllamaResult<IReadOnlyList<OllamaModel>>.Fail(raw.Error ?? "list failed", raw.StatusCode, raw.IsTimeout);
        }

        IReadOnlyList<OllamaModel> models = raw.Value?.Models ?? new List<OllamaModel>();
        return OllamaResult<IReadOnlyList<OllamaModel>>.Ok(models, raw.StatusCode ?? 200);
    }

    /// <summary>POST /api/show：单个模型详情。</summary>
    public Task<OllamaResult<OllamaModelShow>> ShowModelAsync(string name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Task.FromResult(OllamaResult<OllamaModelShow>.Fail("model name must not be empty"));
        }

        return SendAsync<OllamaModelShow>(HttpMethod.Post, "/api/show", new { name }, ProbeTimeout, ct);
    }

    /// <summary>
    /// POST /api/delete：删除模型。成功返回 Ok(true)；404/其他错误如实返回失败。
    /// </summary>
    public async Task<OllamaResult<bool>> DeleteModelAsync(string name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return OllamaResult<bool>.Fail("model name must not be empty");
        }

        var result = await SendRawAsync(HttpMethod.Delete, "/api/delete", new { name }, ProbeTimeout, ct)
            .ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return OllamaResult<bool>.Fail(result.Error ?? "delete failed", result.StatusCode, result.IsTimeout);
        }

        return OllamaResult<bool>.Ok(true, result.StatusCode ?? 200);
    }

    /// <summary>
    /// POST /api/pull（stream=true）：拉取模型，逐条产出 <see cref="OllamaPullProgress"/>。
    /// 网络中断/服务端错误如实以失败项或异常向上暴露，绝不伪造 success。
    /// </summary>
    public async IAsyncEnumerable<OllamaPullProgress> PullModelAsync(
        string name,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("model name must not be empty", nameof(name));
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(LongTimeout);

        HttpResponseMessage? response = null;
        string? sendFailure = null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/pull")
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new { name, stream = true }, JsonOpts),
                    Encoding.UTF8,
                    "application/json"),
            };
            // 流式响应：HeadersOnly，逐行读 NDJSON。
            response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // 调用方取消：如实向上抛。
        }
        catch (OperationCanceledException)
        {
            sendFailure = $"pull timed out after {LongTimeout.TotalMinutes:0} min";
        }
        catch (HttpRequestException ex)
        {
            sendFailure = $"pull unreachable: {ex.Message}";
        }

        if (response is null)
        {
            // catch 体内不能 yield（CS1631），改在 catch 外如实产出失败项。
            yield return new OllamaPullProgress { Status = sendFailure ?? "pull failed" };
            yield break;
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var body = await ReadBodyAsync(response, timeoutCts.Token).ConfigureAwait(false);
                yield return new OllamaPullProgress
                {
                    Status = $"pull failed HTTP {(int)response.StatusCode}: {ExtractError(body)}",
                };
                yield break;
            }

            var stream = await response.Content.ReadAsStreamAsync(timeoutCts.Token).ConfigureAwait(false);
            using var reader = new StreamReader(stream);
            string? line;
            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                if (ct.IsCancellationRequested)
                {
                    yield break;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                OllamaPullProgress? progress;
                try
                {
                    progress = JsonSerializer.Deserialize<OllamaPullProgress>(line, JsonOpts);
                }
                catch (JsonException)
                {
                    continue; // 非 JSON 行：跳过（不伪造进度）。
                }

                if (progress is not null)
                {
                    yield return progress;
                }
            }
        }
    }

    // ---------------- 内部实现 ----------------

    private sealed record OllamaTagsResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("models")]
        public IReadOnlyList<OllamaModel> Models { get; init; } = new List<OllamaModel>();
    }

    private async Task<OllamaResult<T>> SendAsync<T>(
        HttpMethod method,
        string path,
        object? payload,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var raw = await SendRawAsync(method, path, payload, timeout, ct).ConfigureAwait(false);
        if (!raw.IsSuccess)
        {
            return OllamaResult<T>.Fail(raw.Error ?? "request failed", raw.StatusCode, raw.IsTimeout);
        }

        try
        {
            var value = JsonSerializer.Deserialize<T>(raw.Body ?? "null", JsonOpts);
            if (value is null)
            {
                return OllamaResult<T>.Fail("empty response body", raw.StatusCode);
            }

            return OllamaResult<T>.Ok(value, raw.StatusCode ?? 200);
        }
        catch (JsonException ex)
        {
            return OllamaResult<T>.Fail($"invalid JSON for {typeof(T).Name}: {ex.Message}", raw.StatusCode);
        }
    }

    private async Task<(bool IsSuccess, string? Body, string? Error, int? StatusCode, bool IsTimeout)> SendRawAsync(
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
                request.Content = new StringContent(
                    JsonSerializer.Serialize(payload, JsonOpts), Encoding.UTF8, "application/json");
            }

            response = await _http.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // 调用方取消：如实向上抛。
        }
        catch (OperationCanceledException)
        {
            return (false, null, $"ollama request to {path} timed out after {timeout.TotalSeconds:0.#}s", null, true);
        }
        catch (HttpRequestException ex)
        {
            return (false, null, $"ollama unreachable at {BaseUrl}{path}: {ex.Message}", null, false);
        }

        using (response)
        {
            var statusCode = (int)response.StatusCode;
            var body = await ReadBodyAsync(response, timeoutCts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return (false, body, $"ollama returned HTTP {statusCode}: {ExtractError(body)}", statusCode, false);
            }

            return (true, body, null, statusCode, false);
        }
    }

    private static async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>Ollama 错误体通常为 {"error":"..."}；解析失败原样截断。</summary>
    private static string ExtractError(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "(empty body)";
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("error", out var err))
            {
                return err.ToString();
            }
        }
        catch (JsonException)
        {
            // 非 JSON：原样截断。
        }

        return body.Length <= 300 ? body : body[..300] + "…";
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _http.Dispose();
        }
    }
}
