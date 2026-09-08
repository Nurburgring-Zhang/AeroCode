using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AeroCode.AI.Configuration;
using Microsoft.Extensions.Logging;

namespace AeroCode.AI.Capabilities;

/// <summary>
/// B3 厂商能力运行时探测。判定依据 = provider 配置 / 模型 id / 探测响应，<b>禁止静态宣称支持</b>。
/// 硬规则：
/// 1) 探测请求体全部为固定常量（固定 probe 文本/schema），绝不携带用户对话数据；
/// 2) fail-closed：探测失败（4xx/5xx/无网络/超时/解析失败/配置缺失/认证缺失）一律返回 <see cref="VendorCapabilityState.Missing"/>，禁 fail-open；
/// 3) 单次探测短超时，不重试、不阻塞。
/// </summary>
public sealed class VendorCapabilityProbe : IVendorCapabilityProbe
{
    private const int ProbeTimeoutSeconds = 8;

    // 固定探测载荷（无任何用户数据）。
    private const string ProbeText = "ping";
    private const string ProbeToolName = "probe_report";
    private const string ProbeToolSchema = "{\"type\":\"object\",\"properties\":{\"ok\":{\"type\":\"boolean\"}},\"required\":[\"ok\"]}";
    private const string ProbeJsonSchemaFormat =
        "{\"type\":\"json_schema\",\"json_schema\":{\"name\":\"probe_report\",\"strict\":true," +
        "\"schema\":{\"type\":\"object\",\"properties\":{\"ok\":{\"type\":\"boolean\"}},\"required\":[\"ok\"],\"additionalProperties\":false}}}";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly Func<string, ProviderConfig?> _configLookup;
    private readonly HttpClient _http;
    private readonly ILogger _logger;

    /// <param name="configLookup">providerId → ProviderConfig（运行时配置查找；返回 null = 未知 provider）。</param>
    /// <param name="http">可选注入 HttpClient（测试用 FakeHandler）；null = 自建。</param>
    /// <param name="logger">可选注入日志器；null = NullLogger。</param>
    public VendorCapabilityProbe(
        Func<string, ProviderConfig?> configLookup,
        HttpClient? http = null,
        ILogger? logger = null)
    {
        _configLookup = configLookup ?? throw new ArgumentNullException(nameof(configLookup));
        if (http is not null)
        {
            _http = http;
        }
        else
        {
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(ProbeTimeoutSeconds + 2) };
        }
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }

    /// <inheritdoc />
    public async Task<VendorCapabilityState> ProbeAsync(
        string providerId, VendorCapability capability, CancellationToken ct = default)
    {
        try
        {
            var cfg = ResolveConfig(providerId);
            if (cfg is null)
            {
                _logger.LogDebug("VendorCapabilityProbe: no config for {ProviderId} → Missing", providerId);
                return VendorCapabilityState.Missing;
            }
            return capability switch
            {
                VendorCapability.StructuredOutputs => await ProbeStructuredOutputsAsync(cfg, ct).ConfigureAwait(false),
                VendorCapability.EffortTiers => ProbeEffortTiers(cfg),
                VendorCapability.PromptCache => await ProbePromptCacheAsync(cfg, ct).ConfigureAwait(false),
                VendorCapability.Batch => await ProbeBatchAsync(cfg, ct).ConfigureAwait(false),
                _ => VendorCapabilityState.Missing
            };
        }
        catch (Exception ex)
        {
            // fail-closed 硬门：任何异常路径（含取消）一律 Missing，绝不向上抛、绝不放行为 Supported。
            _logger.LogDebug(ex, "VendorCapabilityProbe: {ProviderId}/{Capability} failed → Missing", providerId, capability);
            return VendorCapabilityState.Missing;
        }
    }

    /// <inheritdoc />
    public string? DescribeDowngrade(string providerId, VendorCapability capability)
    {
        try
        {
            var cfg = ResolveConfig(providerId);
            if (cfg is null) return null;
            // G：structured outputs 无 strict json_schema 端点支持——文档化降级 = prompt-JSON + 本端校验
            //（与 ProbeStructuredOutputsAsync 的 G 分支判定同源；其余路径无文档化降级 → null）。
            return capability == VendorCapability.StructuredOutputs && IsGeminiConfig(cfg)
                ? "documented: gemini lacks strict json_schema response_format; degradation path = prompt-JSON + local validation"
                : null;
        }
        catch
        {
            return null; // reason 描述绝不抛出、绝不伪造。
        }
    }

    // ---------- 配置解析 ----------

    private ProviderConfig? ResolveConfig(string providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId)) return null;
        // 精确查找；别名归一化（A/G/O ↔ 厂商全名）由调用方的 lookup 与政策层（VendorCachePolicies）负责。
        return _configLookup(providerId);
    }

    private static bool IsAnthropicConfig(ProviderConfig cfg) =>
        cfg.Kind.Equals("AnthropicMessages", StringComparison.OrdinalIgnoreCase) ||
        cfg.BaseUrl.Contains("api.anthropic.com", StringComparison.OrdinalIgnoreCase) ||
        cfg.DefaultModel.Contains("claude", StringComparison.OrdinalIgnoreCase);

    private static bool IsGeminiConfig(ProviderConfig cfg) =>
        cfg.BaseUrl.Contains("generativelanguage.googleapis.com", StringComparison.OrdinalIgnoreCase) ||
        cfg.Id.Contains("gemini", StringComparison.OrdinalIgnoreCase) ||
        cfg.DefaultModel.Contains("gemini", StringComparison.OrdinalIgnoreCase);

    private static string? ResolveApiKey(ProviderConfig cfg)
    {
        if (cfg.RequiresApiKey && string.IsNullOrWhiteSpace(cfg.ApiKeyEnvVar)) return null;
        if (string.IsNullOrWhiteSpace(cfg.ApiKeyEnvVar)) return null;
        var key = Environment.GetEnvironmentVariable(cfg.ApiKeyEnvVar);
        return string.IsNullOrEmpty(key) ? null : key;
    }

    // ---------- HTTP ----------

    private readonly record struct ProbeHttpResult(int? StatusCode, string? Body, string? Error)
    {
        public bool Ok => StatusCode is >= 200 and < 300;
    }

    private async Task<ProbeHttpResult> SendAsync(
        ProviderConfig cfg, HttpMethod method, string url, string? json, CancellationToken ct = default)
    {
        try
        {
            // 探测自身 8s 短超时 + 调用方取消令牌联动：任一触发即中止（异常路径 fail-closed 收敛 Missing）。
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(ProbeTimeoutSeconds));
            using var req = new HttpRequestMessage(method, url);
            var key = ResolveApiKey(cfg);
            if (key is null)
            {
                if (cfg.RequiresApiKey)
                {
                    // 无可用凭据：fail-closed，不发探测请求。
                    return new ProbeHttpResult(null, null, "no-credentials");
                }
            }
            else if (IsAnthropicConfig(cfg))
            {
                req.Headers.TryAddWithoutValidation("x-api-key", key);
            }
            else
            {
                req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {key}");
            }
            if (cfg.ExtraHeaders is { Count: > 0 })
            {
                foreach (var kv in cfg.ExtraHeaders) req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
            }
            if (json is not null)
            {
                req.Content = new StringContent(json, Encoding.UTF8, "application/json");
            }
            using var resp = await _http.SendAsync(req, cts.Token).ConfigureAwait(false);
            var text = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            return new ProbeHttpResult((int)resp.StatusCode, text, null);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "VendorCapabilityProbe: probe request failed for {Url}", RedactUrl(url));
            return new ProbeHttpResult(null, null, ex.GetType().Name);
        }
    }

    private static string RedactUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var u)) return u.GetLeftPart(UriPartial.Path);
        return "<unparsed-url>";
    }

    // ---------- StructuredOutputs ----------

    private async Task<VendorCapabilityState> ProbeStructuredOutputsAsync(ProviderConfig cfg, CancellationToken ct)
    {
        if (IsAnthropicConfig(cfg))
        {
            // A：强制 tool_choice 的 schema 化 tool-use 探测；响应含合法 tool_use 入参 → Supported。
            var body = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["model"] = cfg.DefaultModel,
                ["max_tokens"] = 64,
                ["messages"] = new object[] { new { role = "user", content = ProbeText } },
                ["tools"] = new object[] { new { name = ProbeToolName, description = "capability probe", input_schema = JsonNode.Parse(ProbeToolSchema) } },
                ["tool_choice"] = new { type = "tool", name = ProbeToolName }
            }, JsonOpts);
            var url = cfg.BaseUrl.TrimEnd('/') + "/v1/messages";
            var resp = await SendAsync(cfg, HttpMethod.Post, url, body, ct).ConfigureAwait(false);
            return resp.Ok && AnthropicForcedToolUseOk(resp.Body)
                ? VendorCapabilityState.Supported
                : VendorCapabilityState.Missing;
        }

        if (IsGeminiConfig(cfg))
        {
            // G：降级路径 = prompt-JSON + 本端校验。response_mime_type 探测 200 → Downgraded（文档化降级）。
            var url = BuildGeminiGenerateUrl(cfg);
            var body = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["contents"] = new object[] { new { parts = new object[] { new { text = ProbeText } } } },
                ["generationConfig"] = new { responseMimeType = "application/json", maxOutputTokens = 64 }
            }, JsonOpts);
            var resp = await SendAsync(cfg, HttpMethod.Post, url, body, ct).ConfigureAwait(false);
            return resp.Ok ? VendorCapabilityState.Downgraded : VendorCapabilityState.Missing;
        }

        // O 兼容协议：response_format=json_schema(strict) 探测；响应 content 为合法 JSON → Supported。
        var oaiUrl = cfg.BaseUrl.TrimEnd('/') + "/chat/completions";
        var oaiBody = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["model"] = cfg.DefaultModel,
            ["max_tokens"] = 64,
            ["messages"] = new object[] { new { role = "user", content = ProbeText } },
            ["response_format"] = JsonNode.Parse(ProbeJsonSchemaFormat),
            ["stream"] = false
        }, JsonOpts);
        var oaiResp = await SendAsync(cfg, HttpMethod.Post, oaiUrl, oaiBody, ct).ConfigureAwait(false);
        return oaiResp.Ok && OpenAiJsonContentOk(oaiResp.Body)
            ? VendorCapabilityState.Supported
            : VendorCapabilityState.Missing;
    }

    private static string BuildGeminiGenerateUrl(ProviderConfig cfg)
    {
        var basePart = cfg.BaseUrl.TrimEnd('/');
        var model = string.IsNullOrWhiteSpace(cfg.DefaultModel) ? "gemini-2.5-flash" : cfg.DefaultModel;
        return basePart.Contains("/v1beta", StringComparison.OrdinalIgnoreCase)
            ? $"{basePart}/models/{model}:generateContent"
            : $"{basePart}/v1beta/models/{model}:generateContent";
    }

    private static bool AnthropicForcedToolUseOk(string? body)
    {
        try
        {
            if (string.IsNullOrEmpty(body)) return false;
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("content", out var content) ||
                content.ValueKind != JsonValueKind.Array) return false;
            foreach (var block in content.EnumerateArray())
            {
                if (block.TryGetProperty("type", out var t) && t.GetString() == "tool_use" &&
                    block.TryGetProperty("input", out var input) &&
                    input.ValueKind == JsonValueKind.Object &&
                    input.TryGetProperty("ok", out _))
                {
                    return true;
                }
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static bool OpenAiJsonContentOk(string? body)
    {
        try
        {
            if (string.IsNullOrEmpty(body)) return false;
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("choices", out var choices) ||
                choices.GetArrayLength() == 0) return false;
            var msg = choices[0];
            if (!msg.TryGetProperty("message", out var m) ||
                !m.TryGetProperty("content", out var c) || c.ValueKind != JsonValueKind.String) return false;
            using var content = JsonDocument.Parse(c.GetString() ?? string.Empty);
            return content.RootElement.ValueKind == JsonValueKind.Object &&
                   content.RootElement.TryGetProperty("ok", out _);
        }
        catch
        {
            return false;
        }
    }

    // ---------- EffortTiers（配置 + 模型 id 依据，无外呼） ----------

    private VendorCapabilityState ProbeEffortTiers(ProviderConfig cfg)
    {
        // 运行时配置证据：操作者实际配置的档位列表（如 "low,medium,high,max"）。
        if (!string.IsNullOrWhiteSpace(cfg.ThinkingEfforts))
        {
            return VendorCapabilityState.Supported;
        }
        var model = cfg.DefaultModel ?? string.Empty;
        // 模型 id 依据（dispatch 允许"基于 provider 配置/模型 id"判定）：
        // OpenAI reasoning-effort 族 / Claude extended thinking / Gemini thinking。
        if (model.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase) ||
            model.StartsWith("o1", StringComparison.OrdinalIgnoreCase) ||
            model.StartsWith("o3", StringComparison.OrdinalIgnoreCase) ||
            model.StartsWith("o4", StringComparison.OrdinalIgnoreCase))
        {
            return VendorCapabilityState.Supported;
        }
        if ((model.Contains("claude", StringComparison.OrdinalIgnoreCase) ||
             model.Contains("gemini-2.5", StringComparison.OrdinalIgnoreCase) ||
             model.Contains("gemini-3", StringComparison.OrdinalIgnoreCase)) && cfg.SupportsThinking)
        {
            return VendorCapabilityState.Supported;
        }
        return VendorCapabilityState.Missing;
    }

    // ---------- PromptCache ----------

    private async Task<VendorCapabilityState> ProbePromptCacheAsync(ProviderConfig cfg, CancellationToken ct)
    {
        if (IsAnthropicConfig(cfg))
        {
            // A：携带 cache_control 的探测请求被端点接受（200）= caching API 可用（端点级证据）。
            var body = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["model"] = cfg.DefaultModel,
                ["max_tokens"] = 16,
                ["system"] = new object[] { new { type = "text", text = ProbeText, cache_control = new { type = "ephemeral" } } },
                ["messages"] = new object[] { new { role = "user", content = ProbeText } }
            }, JsonOpts);
            var url = cfg.BaseUrl.TrimEnd('/') + "/v1/messages";
            var resp = await SendAsync(cfg, HttpMethod.Post, url, body, ct).ConfigureAwait(false);
            return resp.Ok ? VendorCapabilityState.Supported : VendorCapabilityState.Missing;
        }

        if (IsGeminiConfig(cfg))
        {
            // G：implicit 共享前缀缓存（自动生效）。模型代际 + generateContent 探测 200 → Supported。
            var model = cfg.DefaultModel ?? string.Empty;
            if (!(model.Contains("gemini-2", StringComparison.OrdinalIgnoreCase) ||
                  model.Contains("gemini-3", StringComparison.OrdinalIgnoreCase)))
            {
                return VendorCapabilityState.Missing;
            }
            var url = BuildGeminiGenerateUrl(cfg);
            var body = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["contents"] = new object[] { new { parts = new object[] { new { text = ProbeText } } } },
                ["generationConfig"] = new { maxOutputTokens = 16 }
            }, JsonOpts);
            var resp = await SendAsync(cfg, HttpMethod.Post, url, body, ct).ConfigureAwait(false);
            return resp.Ok ? VendorCapabilityState.Supported : VendorCapabilityState.Missing;
        }

        // O：implicit prompt caching（无请求级开关）。模型代际符合条件 + 最小 chat 探测 200 → Supported。
        var m = cfg.DefaultModel ?? string.Empty;
        if (!(m.StartsWith("gpt-4o", StringComparison.OrdinalIgnoreCase) ||
              m.StartsWith("gpt-4.1", StringComparison.OrdinalIgnoreCase) ||
              m.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase) ||
              m.StartsWith("o3", StringComparison.OrdinalIgnoreCase) ||
              m.StartsWith("o4", StringComparison.OrdinalIgnoreCase)))
        {
            return VendorCapabilityState.Missing;
        }
        var chatUrl = cfg.BaseUrl.TrimEnd('/') + "/chat/completions";
        var chatBody = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["model"] = cfg.DefaultModel,
            ["max_tokens"] = 16,
            ["messages"] = new object[] { new { role = "user", content = ProbeText } },
            ["stream"] = false
        }, JsonOpts);
        var chatResp = await SendAsync(cfg, HttpMethod.Post, chatUrl, chatBody, ct).ConfigureAwait(false);
        return chatResp.Ok ? VendorCapabilityState.Supported : VendorCapabilityState.Missing;
    }

    // ---------- Batch ----------

    private async Task<VendorCapabilityState> ProbeBatchAsync(ProviderConfig cfg, CancellationToken ct)
    {
        // 仅对官方文档化 batch 端点做只读 GET 探测；其余（兼容层/未知）fail-closed → Missing。
        if (!Uri.TryCreate(cfg.BaseUrl, UriKind.Absolute, out var baseUri)) return VendorCapabilityState.Missing;

        string? batchUrl = null;
        if (baseUri.Host.Equals("api.anthropic.com", StringComparison.OrdinalIgnoreCase))
        {
            batchUrl = cfg.BaseUrl.TrimEnd('/') + "/v1/messages/batches";
        }
        else if (baseUri.Host.Equals("api.openai.com", StringComparison.OrdinalIgnoreCase))
        {
            batchUrl = cfg.BaseUrl.TrimEnd('/') + "/batches";
        }
        if (batchUrl is null) return VendorCapabilityState.Missing;

        var resp = await SendAsync(cfg, HttpMethod.Get, batchUrl, json: null, ct).ConfigureAwait(false);
        return resp.Ok ? VendorCapabilityState.Supported : VendorCapabilityState.Missing;
    }
}

/// <summary>
/// capability 矩阵 v1 生成器：聚合 <see cref="IVendorCapabilityProbe"/> 判定，输出 providerId × capability 三态矩阵。
/// 两种模式：
/// - 离线/无网关 → 声明式基线矩阵（各能力 = Missing，G×StructuredOutputs = 文档化降级 Downgraded），<b>绝不伪造 Supported</b>；
/// - 在线 → runtime-probe 逐格判定，探测失败由 probe 收敛为 Missing（fail-closed）。
/// </summary>
public static class CapabilityMatrixGenerator
{
    public const string OfflineMode = "offline-declarative-baseline";
    public const string LiveMode = "runtime-probe";
    public const string OfflineDeclaration =
        "mode=offline-declarative-baseline: generated without gateway/network access; every cell is a declarative baseline (Missing) or a documented degradation (Downgraded). No state was fabricated as Supported.";
    public const string LiveDeclaration =
        "mode=runtime-probe: states come from IVendorCapabilityProbe runtime probes (provider config / model id / probe response). Probe failure collapses to Missing (fail-closed); no static claims.";

    public static readonly string[] DefaultProviderIds = { "A", "G", "O" };

    /// <summary>
    /// 矩阵单元格（R3-δ 三态化）：<paramref name="State"/> ∈ Supported/Downgraded/Missing。
    /// <b>证据纪律</b>：Supported 必带 <see cref="Evidence"/>（probe 标识 + UTC 时间戳，运行时验证）；
    /// Downgraded 必带 <see cref="Reason"/>（documented reason）；Missing 无证据（fail-closed，绝不伪造）。
    /// </summary>
    public sealed record MatrixCell(
        string ProviderId,
        string Capability,
        string State,
        string Method,
        string? Evidence = null,
        string? Reason = null);

    public sealed record CapabilityMatrix(string GeneratedAtUtc, string Mode, string ProbeDeclaration, IReadOnlyList<MatrixCell> Cells);

    /// <summary>离线声明式基线：全 Missing；G×StructuredOutputs = Downgraded（文档化降级 prompt-JSON+校验）。绝不产生 Supported。</summary>
    public static CapabilityMatrix BuildBaseline(
        IEnumerable<string>? providerIds = null,
        DateTimeOffset? generatedAtUtc = null)
    {
        var cells = new List<MatrixCell>();
        foreach (var id in (providerIds ?? DefaultProviderIds).ToArray())
        foreach (VendorCapability cap in Enum.GetValues<VendorCapability>())
        {
            var documentedDowngrade =
                id.Equals("G", StringComparison.OrdinalIgnoreCase) && cap == VendorCapability.StructuredOutputs;
            cells.Add(new MatrixCell(
                id,
                cap.ToString(),
                documentedDowngrade ? nameof(VendorCapabilityState.Downgraded) : nameof(VendorCapabilityState.Missing),
                documentedDowngrade ? "documented-degradation" : "offline-baseline",
                Evidence: null,
                Reason: documentedDowngrade
                    ? "documented: gemini structured outputs degrade path = prompt-JSON + local validation (offline declaration, not runtime-verified)"
                    : null));
        }
        return new CapabilityMatrix(
            (generatedAtUtc ?? DateTimeOffset.UtcNow).ToString("o"),
            OfflineMode,
            OfflineDeclaration,
            cells);
    }

    /// <summary>
    /// 在线模式（R3-δ 三态化）：逐格消费真实 probe 结果——
    /// Supported（probe 证实）→ 带 evidence（probe 标识 + 矩阵 UTC 时间戳）；
    /// Downgraded（probe 证实 + 文档化降级路径）→ 带 documented reason（probe 自述，矩阵不代拟）；
    /// Missing（probe 失败/离线/未证实）→ 无证据（fail-closed）。绝不伪造 Supported。
    /// </summary>
    /// <param name="probe">运行时探测实现。</param>
    /// <param name="providerIds">providerId 集合（null = A/G/O 默认三族）。</param>
    /// <param name="generatedAtUtc">矩阵时间戳（可注入以便确定性测试；evidence 复用同一时间戳）。</param>
    /// <param name="ct">取消令牌：逐格探测前检查并透传给 probe（取消如实上抛，不落半份矩阵）。</param>
    /// <param name="probeId">探测标识（evidence 用）；null = probe 类型名。</param>
    public static async Task<CapabilityMatrix> BuildWithProbeAsync(
        IVendorCapabilityProbe probe,
        IEnumerable<string>? providerIds = null,
        DateTimeOffset? generatedAtUtc = null,
        CancellationToken ct = default,
        string? probeId = null)
    {
        ArgumentNullException.ThrowIfNull(probe);
        var generatedAt = (generatedAtUtc ?? DateTimeOffset.UtcNow).ToString("o");
        var evidenceProbeId = string.IsNullOrWhiteSpace(probeId) ? probe.GetType().Name : probeId.Trim();
        var cells = new List<MatrixCell>();
        foreach (var id in (providerIds ?? DefaultProviderIds).ToArray())
        foreach (VendorCapability cap in Enum.GetValues<VendorCapability>())
        {
            ct.ThrowIfCancellationRequested();
            var state = await probe.ProbeAsync(id, cap, ct).ConfigureAwait(false);
            // 证据纪律：只有运行时证实的 Supported 才带 evidence；Downgraded 带 probe 自述 reason；
            // Missing 无证据。矩阵层绝不升级、绝不伪造。
            var evidence = state == VendorCapabilityState.Supported
                ? $"probe={evidenceProbeId};at={generatedAt}"
                : null;
            var reason = state == VendorCapabilityState.Downgraded
                ? probe.DescribeDowngrade(id, cap)
                : null;
            cells.Add(new MatrixCell(id, cap.ToString(), state.ToString(), "runtime-probe", evidence, reason));
        }
        return new CapabilityMatrix(generatedAt, LiveMode, LiveDeclaration, cells);
    }

    private static readonly System.Text.Json.JsonSerializerOptions MatrixJsonOpts = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        // evidence/reason 为 null（Missing/离线基线）时不落字段——离线产物结构与 v1 逐字段一致。
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public static string ToJson(CapabilityMatrix matrix) =>
        System.Text.Json.JsonSerializer.Serialize(matrix, MatrixJsonOpts);

    public static string ToMarkdown(CapabilityMatrix matrix)
    {
        var capabilities = Enum.GetValues<VendorCapability>().Select(c => c.ToString()).ToArray();
        var sb = new StringBuilder();
        sb.AppendLine("# Capability Matrix v1 (AeroCode wave2 R2)");
        sb.AppendLine();
        sb.AppendLine($"- GeneratedAtUtc: {matrix.GeneratedAtUtc}");
        sb.AppendLine($"- Mode: {matrix.Mode}");
        sb.AppendLine($"- Probe declaration: {matrix.ProbeDeclaration}");
        sb.AppendLine();
        sb.Append("| ProviderId |");
        foreach (var cap in capabilities) sb.Append($" {cap} |");
        sb.AppendLine();
        sb.Append("|---|");
        for (var i = 0; i < capabilities.Length; i++) sb.Append("---|");
        sb.AppendLine();
        foreach (var group in matrix.Cells.GroupBy(c => c.ProviderId, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append($"| {group.Key} |");
            foreach (var cap in capabilities)
            {
                var cell = group.FirstOrDefault(c => c.Capability.Equals(cap, StringComparison.OrdinalIgnoreCase));
                sb.Append(cell is null ? " - |" : $" {cell.State} ({cell.Method}) |");
            }
            sb.AppendLine();
        }
        sb.AppendLine();
        sb.AppendLine("> State legend: Supported = runtime-verified; Downgraded = documented degradation path; Missing = unverified / probe failed (fail-closed).");
        // R4 δ-5：EffortTiers 行例外——配置/模型 id 判定，非运行时验证（防误读 Supported 语义）。
        sb.AppendLine("> EffortTiers note: EffortTiers Supported is judged from provider config / model id (no outbound probe), not runtime-verified.");

        // R3-δ 证据附录：Supported 的 evidence 与 Downgraded 的 documented reason 逐行落 md（Missing 无证据，不落行）。
        var evidenced = matrix.Cells
            .Where(c => c.State == nameof(VendorCapabilityState.Supported) || c.State == nameof(VendorCapabilityState.Downgraded))
            .Where(c => !string.IsNullOrWhiteSpace(c.Evidence) || !string.IsNullOrWhiteSpace(c.Reason))
            .ToList();
        if (evidenced.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Evidence / documented reasons");
            sb.AppendLine();
            foreach (var cell in evidenced)
            {
                if (!string.IsNullOrWhiteSpace(cell.Evidence))
                {
                    sb.AppendLine($"- {cell.ProviderId} x {cell.Capability}: evidence → {cell.Evidence}");
                }
                if (!string.IsNullOrWhiteSpace(cell.Reason))
                {
                    sb.AppendLine($"- {cell.ProviderId} x {cell.Capability}: reason → {cell.Reason}");
                }
            }
        }

        return sb.ToString();
    }

    /// <summary>写出 eval/reports/capability-matrix-v1.json + .md（仅写这两个文件名）。</summary>
    public static void Write(CapabilityMatrix matrix, string reportsDirectory)
    {
        System.IO.Directory.CreateDirectory(reportsDirectory);
        System.IO.File.WriteAllText(
            System.IO.Path.Combine(reportsDirectory, "capability-matrix-v1.json"), ToJson(matrix));
        System.IO.File.WriteAllText(
            System.IO.Path.Combine(reportsDirectory, "capability-matrix-v1.md"), ToMarkdown(matrix));
    }
}
