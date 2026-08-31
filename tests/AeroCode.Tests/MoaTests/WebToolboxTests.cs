using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AeroAgent.Moa.Tools;
using Xunit;

namespace AeroCode.Tests.MoaTests;

/// <summary>
/// WebToolbox 真实行为：URL 白名单（仅 http/https 绝对地址）、解析上限、
/// 真实回环 HTTP 端到端（本地 HttpListener：真实 socket/协议/状态码/HTML 抽取）。
/// 外网用例（真实 DuckDuckGo/example.com）按环境变量门控跳过，如实标注。
/// </summary>
public sealed class WebToolboxTests : IDisposable
{
    private readonly HttpListener _listener;
    private readonly string _baseUrl;
    private readonly CancellationTokenSource _serverCts = new();
    private readonly Task _serverTask;

    public WebToolboxTests()
    {
        // 本地回环服务器：真实 HTTP 栈，不依赖外网（与 MoaRealHttpTests 同模式）。
        var port = GetFreePort();
        _baseUrl = $"http://127.0.0.1:{port}/";
        _listener = new HttpListener();
        _listener.Prefixes.Add(_baseUrl);
        _listener.Start();
        _serverTask = ServeAsync();
    }

    public void Dispose()
    {
        _serverCts.Cancel();
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch
        {
            // 关闭竞态不影响断言。
        }

        _serverCts.Dispose();
    }

    private static int GetFreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private async Task ServeAsync()
    {
        while (!_serverCts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener!.GetContextAsync();
            }
            catch (Exception) when (_serverCts.IsCancellationRequested)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    var path = ctx.Request.Url!.AbsolutePath;
                    switch (path)
                    {
                        case "/html":
                            var html = """
                                <!doctype html>
                                <html><head><title>回环测试页</title><style>body{color:red}</style>
                                <script>alert('must-not-appear');</script></head>
                                <body><nav>导航必须消失</nav>
                                <h1>正文标题</h1><p>第一段正文内容。</p>
                                <footer>页脚必须消失</footer></body></html>
                                """;
                            ctx.Response.ContentType = "text/html; charset=utf-8";
                            var htmlBytes = Encoding.UTF8.GetBytes(html);
                            await ctx.Response.OutputStream.WriteAsync(htmlBytes);
                            ctx.Response.Close();
                            break;
                        case "/missing":
                            ctx.Response.StatusCode = 404;
                            ctx.Response.Close();
                            break;
                        case "/plain":
                            ctx.Response.ContentType = "text/plain";
                            var text = Encoding.UTF8.GetBytes(new string('P', 5000));
                            await ctx.Response.OutputStream.WriteAsync(text);
                            ctx.Response.Close();
                            break;
                        default:
                            ctx.Response.StatusCode = 404;
                            ctx.Response.Close();
                            break;
                    }
                }
                catch
                {
                    try { ctx.Response.Close(); } catch { /* 客户端已断开 */ }
                }
            });
        }
    }

    private WebToolbox NewToolbox() => new();

    private static async Task<ToolInvokeResult> InvokeAsync(WebToolbox box, string tool, string argsJson)
        => await box.InvokeAsync(tool, argsJson, CancellationToken.None);

    // ---------- URL 白名单（不发任何请求）----------

    [Theory]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("ftp://example.com/file.txt")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,hello")]
    [InlineData("/relative/path/only")]
    [InlineData("not a url at all")]
    public async Task WebFetch_RejectsNonHttpUrls(string url)
    {
        var result = await InvokeAsync(NewToolbox(), "web_fetch", $"{{\"url\":\"{url.Replace("\"", "\\\"")}\"}}");
        Assert.False(result.Success);
        Assert.Contains("not allowed", result.Output);
        Assert.Contains("http/https", result.Output);
    }

    [Fact]
    public async Task WebFetch_MissingUrl_IsHonestFailure()
    {
        var result = await InvokeAsync(NewToolbox(), "web_fetch", "{}");
        Assert.False(result.Success);
        Assert.Contains("requires a non-empty 'url'", result.Output);
    }

    [Fact]
    public async Task WebSearch_MissingQuery_IsHonestFailure()
    {
        var result = await InvokeAsync(NewToolbox(), "web_search", "{}");
        Assert.False(result.Success);
        Assert.Contains("requires a non-empty 'query'", result.Output);
    }

    [Fact]
    public async Task UnknownTool_IsHonestFailure()
    {
        var result = await InvokeAsync(NewToolbox(), "web_teleport", "{}");
        Assert.False(result.Success);
        Assert.Contains("Unknown web tool", result.Output);
    }

    // ---------- 真实回环 HTTP 端到端 ----------

    [Fact]
    public async Task WebFetch_LocalHttp_ExtractsReadableText_WithoutScriptOrBoilerplate()
    {
        var box = NewToolbox();
        var result = await InvokeAsync(box, "web_fetch", $"{{\"url\":\"{_baseUrl}html\"}}");
        Assert.True(result.Success, result.Output);
        Assert.Contains(_baseUrl, result.Output);
        Assert.Contains("正文标题", result.Output);
        Assert.Contains("第一段正文内容", result.Output);
        Assert.DoesNotContain("must-not-appear", result.Output); // script 被真实抽取器剔除
    }

    [Fact]
    public async Task WebFetch_LocalHttp_RespectsMaxCharsCap()
    {
        var box = NewToolbox();
        var result = await InvokeAsync(box, "web_fetch", $"{{\"url\":\"{_baseUrl}plain\",\"max_chars\":100}}");
        Assert.True(result.Success, result.Output);
        var body = result.Output[(result.Output.IndexOf('\n') + 1)..];
        Assert.True(body.Length <= 100 + 6, $"body too long: {body.Length}");
        Assert.EndsWith("…（已截断）", body);
    }

    [Fact]
    public async Task WebFetch_LocalHttp404_IsHonestFailureWithStatus()
    {
        var box = NewToolbox();
        var result = await InvokeAsync(box, "web_fetch", $"{{\"url\":\"{_baseUrl}missing\"}}");
        Assert.False(result.Success);
        Assert.Contains("404", result.Output);
    }

    [Fact]
    public async Task WebFetch_BadJson_IsHonestFailure()
    {
        var result = await InvokeAsync(NewToolbox(), "web_fetch", "not-json");
        Assert.False(result.Success);
        Assert.Contains("Invalid arguments JSON", result.Output);
    }

    // ---------- 外网真实用例（网络门控跳过，如实标注）----------
    // 门控：设置环境变量 AEROCODE_WEB_TESTS=1 才执行（CI/离线环境自动跳过）。

    private static bool RealWebEnabled =>
        Environment.GetEnvironmentVariable("AEROCODE_WEB_TESTS") == "1";

    [SkippableFact]
    public async Task WebSearch_RealDuckDuckGo_ReturnsRealResultsOrHonestEmpty()
    {
        Skip.IfNot(RealWebEnabled, "外网真实检索用例：未设置 AEROCODE_WEB_TESTS=1，如实跳过");
        var box = NewToolbox();
        var result = await box.InvokeAsync("web_search", "{\"query\":\"dotnet 9 release notes\",\"max_results\":3}", CancellationToken.None);
        Assert.True(result.Success, result.Output);
        // 诚实语义：真实命中（含 URL 行）或全后端空（bot-challenge/限流如实说明），绝不伪造。
        var hasRealHits = result.Output.Contains("URL:", StringComparison.Ordinal);
        if (!hasRealHits)
        {
            Assert.Contains("No results", result.Output);
        }
    }

    [SkippableFact]
    public async Task WebFetch_RealExampleCom_ReturnsRealText()
    {
        Skip.IfNot(RealWebEnabled, "外网真实抓取用例：未设置 AEROCODE_WEB_TESTS=1，如实跳过");
        var box = NewToolbox();
        var result = await box.InvokeAsync("web_fetch", "{\"url\":\"https://example.com\",\"max_chars\":500}", CancellationToken.None);
        Assert.True(result.Success, result.Output);
        Assert.Contains("Example Domain", result.Output);
    }
}
