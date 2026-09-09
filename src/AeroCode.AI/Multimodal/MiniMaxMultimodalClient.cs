// Copyright (c) AeroCode
// MiniMaxMultimodalClient — 真接 MiniMax 多模态端点（文生图 image-01 / 文生视频 video-01）。
// 零假装：每次调用都是真实 HTTP 请求，返回真实媒体 URL。API key 从 MINIMAX_API_KEY 环境变量读取。
// 探测实证（2026-09-09）：image_generation 同步返回 image_urls；video_generation 返回 task_id，
// 需经 query/video_generation 轮询取结果。TTS(t2a_v2) 端点存在但参数格式未确认，暂未接入。
using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace AeroCode.AI.Multimodal;

/// <summary>文生图结果（真实图片 URL 列表）。</summary>
public sealed record ImageGenResult(string[] ImageUrls, string RawJson);

/// <summary>文生视频任务创建结果（task_id，异步）。</summary>
public sealed record VideoTaskCreated(string TaskId, string RawJson);

/// <summary>文生视频任务查询结果（状态 + 完成后的视频 URL）。</summary>
public sealed record VideoTaskStatus(string Status, string? VideoUrl, string RawJson)
{
    public bool IsCompleted => string.Equals(Status, "Success", StringComparison.OrdinalIgnoreCase) && VideoUrl is not null;
    public bool IsFailed => string.Equals(Status, "Fail", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// MiniMax 多模态客户端。Base URL 默认 https://api.minimax.chat/v1。
/// 全部为真实 HTTP 调用；失败抛异常由调用方如实呈现，绝不伪造媒体。
/// </summary>
public sealed class MiniMaxMultimodalClient
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly Func<string?> _apiKeyProvider;

    public MiniMaxMultimodalClient(HttpClient? http = null, string baseUrl = "https://api.minimax.chat/v1", Func<string?>? apiKeyProvider = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        _baseUrl = baseUrl.TrimEnd('/');
        _apiKeyProvider = apiKeyProvider ?? (() => Environment.GetEnvironmentVariable("MINIMAX_API_KEY"));
    }

    private string RequireKey()
    {
        var key = _apiKeyProvider();
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("缺少 MINIMAX_API_KEY 环境变量，无法调用多模态端点");
        return key;
    }

    /// <summary>文生图（image-01，同步）。返回真实图片 URL。</summary>
    public async Task<ImageGenResult> GenerateImageAsync(string prompt, CancellationToken ct = default)
    {
        var key = RequireKey();
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/image_generation");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
        req.Content = JsonContent.Create(new { model = "image-01", prompt });
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"生图失败 HTTP {(int)resp.StatusCode}: {Truncate(body, 300)}");

        using var doc = JsonDocument.Parse(body);
        var urls = doc.RootElement.TryGetProperty("data", out var data)
                   && data.TryGetProperty("image_urls", out var arr)
            ? arr.EnumerateArray().Select(e => e.GetString() ?? string.Empty)
                  .Where(s => s.Length > 0).ToArray()
            : Array.Empty<string>();
        if (urls.Length == 0)
            throw new InvalidOperationException($"生图未返回图片 URL: {Truncate(body, 300)}");
        return new ImageGenResult(urls, body);
    }

    /// <summary>文生视频（video-01，创建异步任务）。返回 task_id。</summary>
    public async Task<VideoTaskCreated> CreateVideoTaskAsync(string prompt, CancellationToken ct = default)
    {
        var key = RequireKey();
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/video_generation");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
        req.Content = JsonContent.Create(new { model = "video-01", prompt });
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"创建视频任务失败 HTTP {(int)resp.StatusCode}: {Truncate(body, 300)}");

        using var doc = JsonDocument.Parse(body);
        var taskId = doc.RootElement.TryGetProperty("task_id", out var t) ? t.GetString() : null;
        if (string.IsNullOrWhiteSpace(taskId))
            throw new InvalidOperationException($"视频任务未返回 task_id: {Truncate(body, 300)}");
        return new VideoTaskCreated(taskId, body);
    }

    /// <summary>查询文生视频任务状态（完成时携带视频 URL）。</summary>
    public async Task<VideoTaskStatus> QueryVideoTaskAsync(string taskId, CancellationToken ct = default)
    {
        var key = RequireKey();
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/query/video_generation?task_id={Uri.EscapeDataString(taskId)}");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"查询视频任务失败 HTTP {(int)resp.StatusCode}: {Truncate(body, 300)}");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var status = root.TryGetProperty("status", out var s) ? s.GetString() ?? "" : "";
        string? videoUrl = null;
        if (root.TryGetProperty("file_id", out var fid))
        {
            // 完成时通过 file_id 取下载链接
            var fileId = fid.GetString();
            if (!string.IsNullOrWhiteSpace(fileId))
                videoUrl = await RetrieveFileUrlAsync(fileId, key, ct).ConfigureAwait(false);
        }
        return new VideoTaskStatus(status, videoUrl, body);
    }

    /// <summary>经 file/retrieve 取 file_id 对应的真实下载 URL。</summary>
    private async Task<string?> RetrieveFileUrlAsync(string fileId, string key, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/file/retrieve");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
            req.Content = JsonContent.Create(new { file_id = fileId });
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("file", out var file)
                && file.TryGetProperty("download_url", out var dl))
                return dl.GetString();
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
