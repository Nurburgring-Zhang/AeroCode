using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace AeroCode.AI.Capabilities;

/// <summary>
/// B6 弃用监控：changelog 轮询。<b>默认关闭</b>——构造时未显式 enabled=true 绝不外呼；
/// 显式启用后也只访问 URL allowlist 内的地址；无网络/超时/任何异常一律只记录状态，
/// 绝不阻塞主流程、绝不抛出。结果不含响应体原文（避免日志携带不可信内容与敏感信息）。
/// </summary>
public sealed class DeprecationMonitor
{
    /// <summary>单次外呼超时（秒）。到时只记状态。</summary>
    private const int ProbeTimeoutSeconds = 5;

    /// <summary>单条 changelog URL 的弃用关键词（大小写不敏感子串，仅计数不落地原文）。</summary>
    private const string DeprecationKeyword = "deprecat";

    private readonly HttpClient _http;
    private readonly ILogger _logger;
    private readonly IReadOnlyList<string> _allowlist;

    /// <summary>单条弃用检查结果（可安全序列化/落日志，无密钥、无响应体原文）。</summary>
    public sealed record DeprecationCheckResult(
        DateTimeOffset CheckedAtUtc,
        bool Enabled,
        bool Attempted,
        string Url,
        int? StatusCode,
        bool Success,
        int DeprecationMentions,
        string? Error)
    {
        public static DeprecationCheckResult Disabled(DateTimeOffset at) =>
            new(CheckedAtUtc: at, Enabled: false, Attempted: false, Url: string.Empty,
                StatusCode: null, Success: false, DeprecationMentions: 0, Error: null);
    }

    /// <param name="enabled">默认 false = 关闭（绝不外呼）。显式 true 才允许出网。</param>
    /// <param name="urlAllowlist">允许外呼的 URL 白名单（精确匹配，大小写不敏感）。null/空 = 不外呼任何地址。</param>
    /// <param name="http">可选注入 HttpClient（测试用 FakeHandler）；null = 自建。</param>
    /// <param name="logger">可选注入日志器；null = NullLogger。</param>
    public DeprecationMonitor(
        bool enabled = false,
        IEnumerable<string>? urlAllowlist = null,
        HttpClient? http = null,
        ILogger? logger = null)
    {
        IsEnabled = enabled;
        _allowlist = (urlAllowlist ?? Array.Empty<string>())
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Select(u => u.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (http is not null)
        {
            _http = http;
        }
        else
        {
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(ProbeTimeoutSeconds) };
        }
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }

    /// <summary>true = 已显式启用（才可能外呼）。</summary>
    public bool IsEnabled { get; }

    /// <summary>URL 白名单快照。</summary>
    public IReadOnlyList<string> Allowlist => _allowlist;

    /// <summary>
    /// 执行一轮 changelog 检查。默认关闭 → 直接返回 Enabled=false 快照（零外呼、零阻塞）。
    /// 启用时逐条检查 allowlist 内 URL；无网络/超时/解析失败只记录状态，绝不抛出、绝不阻塞。
    /// </summary>
    public async Task<IReadOnlyList<DeprecationCheckResult>> CheckAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        if (!IsEnabled || _allowlist.Count == 0)
        {
            // 默认关闭硬门：不构造请求、不触碰网络。
            return new[] { DeprecationCheckResult.Disabled(now) };
        }

        var results = new List<DeprecationCheckResult>();
        foreach (var url in _allowlist)
        {
            results.Add(await CheckOneAsync(url, now, ct).ConfigureAwait(false));
        }
        return results;
    }

    private async Task<DeprecationCheckResult> CheckOneAsync(string url, DateTimeOffset now, CancellationToken ct)
    {
        // 白名单精确门：传入 URL 必须与 allowlist 条目完全一致（大小写不敏感）才允许外呼。
        if (!_allowlist.Contains(url, StringComparer.OrdinalIgnoreCase))
        {
            return new DeprecationCheckResult(now, IsEnabled, Attempted: false, url,
                StatusCode: null, Success: false, DeprecationMentions: 0, Error: "url-not-in-allowlist");
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(ProbeTimeoutSeconds));
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var resp = await _http.SendAsync(req, cts.Token).ConfigureAwait(false);
            var code = (int)resp.StatusCode;
            var mentions = 0;
            if (resp.IsSuccessStatusCode)
            {
                // 只对响应体做关键词计数，不保存/记录原文。
                var body = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                mentions = CountMentions(body);
            }
            var success = resp.IsSuccessStatusCode;
            if (!success)
            {
                _logger.LogDebug("DeprecationMonitor: {Url} → HTTP {Code}", RedactUrl(url), code);
            }
            return new DeprecationCheckResult(now, IsEnabled, Attempted: true, url, code, success, mentions,
                success ? null : $"http-{code}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogDebug("DeprecationMonitor: {Url} timed out", RedactUrl(url));
            return new DeprecationCheckResult(now, IsEnabled, Attempted: true, url,
                StatusCode: null, Success: false, DeprecationMentions: 0, Error: "timeout");
        }
        catch (Exception ex)
        {
            // 无网络/DNS 失败等：只记录状态，绝不阻塞主流程。
            _logger.LogDebug(ex, "DeprecationMonitor: {Url} failed", RedactUrl(url));
            return new DeprecationCheckResult(now, IsEnabled, Attempted: true, url,
                StatusCode: null, Success: false, DeprecationMentions: 0, Error: ex.GetType().Name);
        }
    }

    private static int CountMentions(string body)
    {
        if (string.IsNullOrEmpty(body)) return 0;
        var count = 0;
        var idx = body.IndexOf(DeprecationKeyword, StringComparison.OrdinalIgnoreCase);
        while (idx >= 0)
        {
            count++;
            idx = body.IndexOf(DeprecationKeyword, idx + DeprecationKeyword.Length, StringComparison.OrdinalIgnoreCase);
        }
        return count;
    }

    /// <summary>
    /// 日志脱敏：URL 只保留 host+path（剥离 query，可能含 key 参数）。
    /// 公开供消费方（如网关执行路径的 MarkOnly 检查）复用同一脱敏口径，避免各自记录完整 URL。
    /// </summary>
    public static string RedactUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var u))
        {
            return u.GetLeftPart(UriPartial.Path);
        }
        return "<unparsed-url>";
    }
}
