// Copyright (c) AeroCode
// McpGateway remote transport 扩展（批次 B G4，builder-γ）：
// TransportKind{Stdio,Sse,StreamableHttp} 配置（Url/Headers）+ OAuth Device-Code 流（RFC 8628）。
// 传输层直接基于 ModelContextProtocol 1.0.0 官方 SDK 的 HttpClientTransport
// （内部即 SseClientSessionTransport / StreamableHttpClientSessionTransport）。
// 凭据纪律：token 只在内存（ITokenProvider 实现），绝不落盘；headers 支持 ${ENV_VAR} 引用展开，
// API key 不必明文写进 settings.json（与 stdio 环境变量 ${ENV} 引用同语义）。
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

namespace AeroCode.Mcp.Client;

/// <summary>传输种类。Stdio=拉起子进程（既有路径）；Sse/StreamableHttp=远程 HTTP 传输。</summary>
public enum McpTransportKind
{
    Stdio,
    Sse,
    StreamableHttp,
}

/// <summary>
/// 访问令牌来源（OAuth device-code 等流的可注入抽象）。
/// 实现必须只在内存持有凭据——绝不写文件（凭据不落盘纪律）。
/// </summary>
public interface ITokenProvider
{
    /// <summary>返回可用的 access token（实现方自行缓存/刷新）。失败如实抛异常。</summary>
    Task<string> GetAccessTokenAsync(CancellationToken ct = default);
}

/// <summary>Device authorization 阶段产出（RFC 8628 §3.2）：交给用户完成授权。</summary>
/// <param name="UserCode">用户在验证页输入的码。</param>
/// <param name="VerificationUri">用户需访问的验证页。</param>
/// <param name="VerificationUriComplete">可免输入 user_code 的直达验证 URL（可空）。</param>
/// <param name="ExpiresUtc">挑战过期时刻（UTC）。</param>
public sealed record DeviceCodeChallenge(string UserCode, string VerificationUri, string? VerificationUriComplete, DateTimeOffset ExpiresUtc);

/// <summary>Device-code 流成功换得的 token（仅内存持有，绝不落盘）。</summary>
/// <param name="AccessToken">访问令牌。</param>
/// <param name="TokenType">令牌类型（通常 bearer）。</param>
/// <param name="ExpiresUtc">到期时刻（UTC；端点未给 expires_in 时按 1h 兜底）。</param>
public sealed record DeviceCodeGrant(string AccessToken, string TokenType, DateTimeOffset ExpiresUtc);

/// <summary>
/// OAuth 2.0 Device Authorization Grant（RFC 8628）真实实现：
/// 1) POST 设备授权端点（client_id + scope）→ device_code/user_code/interval/expires_in；
/// 2) 按 interval 轮询 token 端点（authorization_pending 继续、slow_down +5s、
///    access_denied/expired_token 如实抛）；
/// 3) 成功 token 仅缓存在内存（<see cref="LastGrant"/>），到期前 60s 内自动重走流程。
/// </summary>
public sealed class DeviceCodeTokenProvider : ITokenProvider
{
    private static readonly TimeSpan ChallengeSafetyMargin = TimeSpan.FromSeconds(60);

    private readonly Uri _deviceAuthorizationEndpoint;
    private readonly Uri _tokenEndpoint;
    private readonly string _clientId;
    private readonly string? _scope;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Func<DeviceCodeChallenge, Task>? _onChallenge;
    private readonly object _grantLock = new();

    private DeviceCodeGrant? _grant;

    /// <param name="deviceAuthorizationEndpoint">设备授权端点（如 https://example.com/login/device/code）。</param>
    /// <param name="tokenEndpoint">token 端点（RFC 8628 §3.4）。</param>
    /// <param name="clientId">OAuth client id。</param>
    /// <param name="scope">可选 scope 空格串。</param>
    /// <param name="httpClient">可注入 HttpClient（测试指向本地端点）；缺省自建。</param>
    /// <param name="onChallenge">拿到 user_code 后回调（UI 展示“去这里输入这个码”）；测试可捕获。</param>
    public DeviceCodeTokenProvider(
        Uri deviceAuthorizationEndpoint,
        Uri tokenEndpoint,
        string clientId,
        string? scope = null,
        HttpClient? httpClient = null,
        Func<DeviceCodeChallenge, Task>? onChallenge = null)
    {
        _deviceAuthorizationEndpoint = deviceAuthorizationEndpoint ?? throw new ArgumentNullException(nameof(deviceAuthorizationEndpoint));
        _tokenEndpoint = tokenEndpoint ?? throw new ArgumentNullException(nameof(tokenEndpoint));
        _clientId = string.IsNullOrWhiteSpace(clientId)
            ? throw new ArgumentException("client id must not be empty", nameof(clientId))
            : clientId;
        _scope = scope;
        _onChallenge = onChallenge;
        _ownsHttp = httpClient is null;
        _http = httpClient ?? new HttpClient();
    }

    /// <summary>最近一次成功授权的 token（仅内存；含到期时刻，诊断/测试观测用）。</summary>
    public DeviceCodeGrant? LastGrant
    {
        get { lock (_grantLock) { return _grant; } }
    }

    /// <summary>阶段一（可单测/真实端点门控测试）：发起设备授权并返回挑战，不进入轮询。</summary>
    public async Task<DeviceCodeChallenge> StartAuthorizationAsync(CancellationToken ct = default)
    {
        using var form = new FormUrlEncodedContent(BuildDeviceAuthFields());
        using var resp = await _http.PostAsync(_deviceAuthorizationEndpoint, form, ct);
        resp.EnsureSuccessStatusCode();
        var payload = await resp.Content.ReadFromJsonAsync<DeviceAuthResponse>(cancellationToken: ct)
                      ?? throw new InvalidOperationException("device authorization endpoint returned empty body");
        if (string.IsNullOrWhiteSpace(payload.DeviceCode) || string.IsNullOrWhiteSpace(payload.UserCode))
        {
            throw new InvalidOperationException(
                $"device authorization endpoint returned no device_code/user_code: {payload.Error ?? "(no error field)"}");
        }

        var interval = payload.Interval is > 0 ? payload.Interval.Value : 5;
        var challenge = new DeviceCodeChallenge(
            payload.UserCode,
            payload.VerificationUri ?? string.Empty,
            payload.VerificationUriComplete,
            DateTimeOffset.UtcNow.AddSeconds(payload.ExpiresIn is > 0 ? payload.ExpiresIn.Value : 600));
        _nextPollDelay = TimeSpan.FromSeconds(interval);
        CurrentDeviceCode = payload.DeviceCode; // 供 GetAccessTokenAsync 轮询使用
        if (_onChallenge is not null)
        {
            await _onChallenge(challenge);
        }

        return challenge;
    }

    private TimeSpan _nextPollDelay = TimeSpan.FromSeconds(5);

    public async Task<string> GetAccessTokenAsync(CancellationToken ct = default)
    {
        DeviceCodeGrant? cached;
        lock (_grantLock)
        {
            cached = _grant;
        }

        if (cached is not null && cached.ExpiresUtc - DateTimeOffset.UtcNow > ChallengeSafetyMargin)
        {
            return cached.AccessToken;
        }

        var challenge = await StartAuthorizationAsync(ct);
        var deviceCode = CurrentDeviceCode ?? throw new InvalidOperationException("device code missing after authorization start");

        var deadline = challenge.ExpiresUtc;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(_nextPollDelay, ct);
            var grant = await PollForTokenAsync(deviceCode, ct);
            if (grant is not null)
            {
                lock (_grantLock)
                {
                    _grant = grant;
                }

                return grant.AccessToken;
            }
        }

        throw new InvalidOperationException("device code flow timed out waiting for user authorization");
    }

    /// <summary>阶段间传递（StartAuthorizationAsync 记录 device_code 供轮询；普通调用方无需感知）。</summary>
    private string? CurrentDeviceCode { get; set; }

    private async Task<DeviceCodeGrant?> PollForTokenAsync(string deviceCode, CancellationToken ct)
    {
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
            ["device_code"] = deviceCode,
            ["client_id"] = _clientId,
        });
        using var resp = await _http.PostAsync(_tokenEndpoint, form, ct);
        var payload = await resp.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken: ct)
                      ?? throw new InvalidOperationException("token endpoint returned empty body");

        switch (payload.Error)
        {
            case "authorization_pending":
                return null;
            case "slow_down":
                _nextPollDelay = _nextPollDelay.Add(TimeSpan.FromSeconds(5));
                return null;
            case null:
                if (string.IsNullOrWhiteSpace(payload.AccessToken))
                {
                    throw new InvalidOperationException("token endpoint returned success without access_token");
                }

                return new DeviceCodeGrant(
                    payload.AccessToken,
                    payload.TokenType ?? "bearer",
                    DateTimeOffset.UtcNow.AddSeconds(payload.ExpiresIn is > 0 ? payload.ExpiresIn.Value : 3600));
            default:
                throw new InvalidOperationException($"device code flow failed: {payload.Error}");
        }
    }

    private Dictionary<string, string> BuildDeviceAuthFields()
    {
        var fields = new Dictionary<string, string> { ["client_id"] = _clientId };
        if (!string.IsNullOrWhiteSpace(_scope))
        {
            fields["scope"] = _scope;
        }

        return fields;
    }

    private sealed class DeviceAuthResponse
    {
        // RFC 8628 响应字段为 snake_case（真实端点惯例）
        [JsonPropertyName("device_code")]
        public string? DeviceCode { get; set; }

        [JsonPropertyName("user_code")]
        public string? UserCode { get; set; }

        [JsonPropertyName("verification_uri")]
        public string? VerificationUri { get; set; }

        [JsonPropertyName("verification_uri_complete")]
        public string? VerificationUriComplete { get; set; }

        [JsonPropertyName("expires_in")]
        public int? ExpiresIn { get; set; }

        [JsonPropertyName("interval")]
        public int? Interval { get; set; }

        [JsonPropertyName("error")]
        public string? Error { get; set; }
    }

    private sealed class TokenResponse
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; set; }

        [JsonPropertyName("token_type")]
        public string? TokenType { get; set; }

        [JsonPropertyName("expires_in")]
        public int? ExpiresIn { get; set; }

        [JsonPropertyName("error")]
        public string? Error { get; set; }
    }
}

/// <summary>
/// 传输工厂：把 <see cref="McpServerConfig"/>（url/transport/headers 扩展字段）解析为
/// SDK IClientTransport。url 缺省 = Stdio（既有行为）；url + transport=sse / streamableHttp
/// （缺省 streamableHttp，当前 MCP 标准）分别对应 SDK 的 Sse / StreamableHttp 模式。
/// </summary>
public static class McpTransportFactory
{
    private static readonly Regex EnvRefRx = new(@"^\$\{([A-Za-z_][A-Za-z0-9_]*)\}$", RegexOptions.Compiled);

    /// <summary>解析传输种类。非法 transport 字符串抛 ArgumentException（配置错误要大声，不静默降级）。</summary>
    public static McpTransportKind ResolveKind(McpServerConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (string.IsNullOrWhiteSpace(config.Url))
        {
            return McpTransportKind.Stdio;
        }

        switch (config.Transport?.Trim().ToLowerInvariant())
        {
            case null:
            case "":
            case "streamablehttp":
            case "streamable_http":
                return McpTransportKind.StreamableHttp; // 当前 MCP 标准传输
            case "sse":
                return McpTransportKind.Sse;            // 旧式 HTTP+SSE（兼容存量服务器）
            case "stdio":
                return McpTransportKind.Stdio;
            default:
                throw new ArgumentException(
                    $"MCP server '{config.Id}': unsupported transport '{config.Transport}' (expected stdio|sse|streamableHttp)");
        }
    }

    /// <summary>构造远程 HTTP 传输（Sse / StreamableHttp）。tokenProvider 提供时经 DelegatingHandler 注入 Authorization: Bearer。</summary>
    public static IClientTransport CreateHttpTransport(
        McpServerConfig config,
        ILogger? logger = null,
        ITokenProvider? tokenProvider = null,
        TimeSpan? connectionTimeout = null)
    {
        var kind = ResolveKind(config);
        if (kind is McpTransportKind.Stdio)
        {
            throw new ArgumentException("CreateHttpTransport requires an sse/streamableHttp (url) config", nameof(config));
        }

        if (!Uri.TryCreate(config.Url, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException(
                $"MCP server '{config.Id}': url must be an absolute http(s) address, got '{config.Url}'", nameof(config));
        }

        var headers = ExpandHeaders(config.Headers, logger, config.Id);
        if (tokenProvider is not null)
        {
            var handler = new TokenInjectionHandler(tokenProvider);
            var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            return new HttpClientTransport(BuildOptions(), http, loggerFactory: null, ownsHttpClient: true);
        }

        return new HttpClientTransport(BuildOptions());

        HttpClientTransportOptions BuildOptions() => new()
        {
            Name = config.Id,
            Endpoint = endpoint,
            TransportMode = kind == McpTransportKind.Sse
                ? ModelContextProtocol.Client.HttpTransportMode.Sse
                : ModelContextProtocol.Client.HttpTransportMode.StreamableHttp,
            AdditionalHeaders = headers,
            ConnectionTimeout = connectionTimeout ?? TimeSpan.FromSeconds(30),
        };
    }

    /// <summary>headers：整值 ${ENV_NAME} 引用从当前进程环境展开；未设置按“未发送”处理并大声告警（同 stdio env 语义）。</summary>
    private static Dictionary<string, string>? ExpandHeaders(Dictionary<string, string>? source, ILogger? logger, string serverId)
    {
        if (source is null || source.Count == 0)
        {
            return null;
        }

        var map = new Dictionary<string, string>(source.Count, StringComparer.Ordinal);
        foreach (var kv in source)
        {
            var match = kv.Value is null ? null : EnvRefRx.Match(kv.Value);
            if (match is { Success: true })
            {
                var expanded = Environment.GetEnvironmentVariable(match.Groups[1].Value);
                if (expanded is null)
                {
                    logger?.LogWarning(
                        "[DEGRADED] MCP server '{ServerId}' header {Key} references ${Var} which is not set; header will be omitted",
                        serverId, kv.Key, match.Groups[1].Value);
                    continue;
                }

                map[kv.Key] = expanded;
            }
            else
            {
                map[kv.Key] = kv.Value ?? string.Empty;
            }
        }

        return map;
    }

    /// <summary>每请求注入 Authorization: Bearer &lt;token&gt;（token 获取失败如实上抛，不降级为匿名请求）。</summary>
    private sealed class TokenInjectionHandler : DelegatingHandler
    {
        private readonly ITokenProvider _provider;

        public TokenInjectionHandler(ITokenProvider provider) : base(new HttpClientHandler())
        {
            _provider = provider;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var token = await _provider.GetAccessTokenAsync(ct);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return await base.SendAsync(request, ct);
        }
    }
}
