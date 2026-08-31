using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace AeroCode.Mcp.Client;

/// <summary>网关发现的单个远端工具（SDK 无关 DTO）。</summary>
/// <param name="Name">服务器上的原始工具名。</param>
/// <param name="Description">工具描述（可空）。</param>
/// <param name="ParametersJsonSchema">入参 JSON Schema 原文（供 provider function calling 直接使用）。</param>
public sealed record McpToolInfo(string Name, string? Description, string ParametersJsonSchema);

/// <summary>一次工具调用的真实结果。IsError = 服务器端报告执行失败。</summary>
public sealed record McpCallOutcome(bool IsError, string Text);

/// <summary>
/// 单个 MCP server 的连接抽象（可测性；生产实现为 <see cref="McpGateway"/>）。
/// </summary>
public interface IMcpGateway : IAsyncDisposable
{
    /// <summary>对应 <see cref="McpServerConfig.Id"/>。</summary>
    string ServerId { get; }

    /// <summary>发现服务器暴露的全部工具。</summary>
    Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken ct = default);

    /// <summary>调用指定工具；协议层错误（连接丢失）抛异常，业务层错误以 IsError 返回。</summary>
    Task<McpCallOutcome> CallToolAsync(
        string toolName,
        IReadOnlyDictionary<string, object?>? arguments,
        CancellationToken ct = default);
}

/// <summary>
/// 生产级 MCP 网关：官方 SDK McpClient + StdioClientTransport 管理一个子进程服务器；
/// 远程配置（Url）走 HttpClientTransport（SSE / Streamable HTTP，见 McpTransportFactory）。
/// 重启容错：调用/发现因连接丢失失败时，自动重连一次再重试（进程崩溃/被杀后自愈）；
/// 二次失败如实上抛，不静默吞错。全程由信号量串行化，防并发重复建连。
/// </summary>
public sealed class McpGateway : IMcpGateway
{
    private readonly McpServerConfig _config;
    private readonly ILogger? _logger;
    private readonly ITokenProvider? _tokenProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private McpClient? _client;
    private bool _disposed;

    public McpGateway(McpServerConfig config, ILogger? logger = null, ITokenProvider? tokenProvider = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        if (string.IsNullOrWhiteSpace(config.Id))
            throw new ArgumentException("MCP server config must carry a non-empty Id", nameof(config));
        var isRemote = !string.IsNullOrWhiteSpace(config.Url);
        if (isRemote)
        {
            if (!Uri.TryCreate(config.Url, UriKind.Absolute, out var endpoint) || endpoint.Scheme is not ("http" or "https"))
                throw new ArgumentException(
                    $"MCP server config '{config.Id}': url must be an absolute http(s) address, got '{config.Url}'", nameof(config));
        }
        else if (string.IsNullOrWhiteSpace(config.Command))
            throw new ArgumentException("MCP server config must carry a non-empty Command (or a remote Url)", nameof(config));
        _logger = logger;
        _tokenProvider = tokenProvider;
    }

    /// <summary>握手（initialize）超时。可注入——测试无需等满 30 秒即可验证超时路径。</summary>
    public TimeSpan InitializationTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 单次工具调用/发现的无响应超时上限：远端工具挂死时不能永久占用串行化闸门，
    /// 否则该服务器后续所有调用全部阻塞。超时抛 <see cref="TimeoutException"/>（诚实上抛）。
    /// </summary>
    public TimeSpan CallTimeout { get; init; } = TimeSpan.FromMinutes(2);

    public string ServerId => _config.Id;

    public async Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken ct = default)
        => await WithClientAsync(async (client, token) =>
        {
            var tools = await client.ListToolsAsync(cancellationToken: token);
            return (IReadOnlyList<McpToolInfo>)tools
                .Select(t => new McpToolInfo(
                    t.Name,
                    t.Description,
                    SchemaText(t.JsonSchema)))
                .ToList();
        }, ct);

    public async Task<McpCallOutcome> CallToolAsync(
        string toolName,
        IReadOnlyDictionary<string, object?>? arguments,
        CancellationToken ct = default)
        => await WithClientAsync(async (client, token) =>
        {
            var result = await client.CallToolAsync(toolName, arguments, cancellationToken: token);
            var text = string.Join(
                "\n",
                result.Content.OfType<TextContentBlock>().Select(c => c.Text));
            return new McpCallOutcome(result.IsError == true, text);
        }, ct);

    /// <summary>显式重启：断开并终止子进程，下次调用自动重建（配置热更新场景）。</summary>
    public async Task RestartAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await DisposeClientNoLockAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _gate.WaitAsync();
        try
        {
            await DisposeClientNoLockAsync();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private async Task<T> WithClientAsync<T>(
        Func<McpClient, CancellationToken, Task<T>> action,
        CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(ct);
        try
        {
            var client = await EnsureConnectedNoLockAsync(ct);
            try
            {
                return await RunWithCallTimeoutAsync(action, client, ct);
            }
            catch (Exception ex) when (IsConnectionLoss(ex) && !ct.IsCancellationRequested)
            {
                // 子进程死亡/管道断裂：重连一次再重试；再失败如实上抛。
                _logger?.LogWarning(
                    "[DEGRADED] MCP server '{ServerId}' connection lost ({Error}); reconnecting once",
                    ServerId, ex.Message);
                await DisposeClientNoLockAsync();
                client = await EnsureConnectedNoLockAsync(ct);
                return await RunWithCallTimeoutAsync(action, client, ct);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 给单次调用套无响应看门狗：CallTimeout 内无结果即取消并抛 TimeoutException。
    /// 调用方自己取消（ct）时保持 OperationCanceledException 语义，不混淆两种终止原因。
    /// </summary>
    private async Task<T> RunWithCallTimeoutAsync<T>(
        Func<McpClient, CancellationToken, Task<T>> action,
        McpClient client,
        CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(CallTimeout);
        try
        {
            return await action(client, cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"MCP server '{ServerId}' call exceeded {CallTimeout.TotalSeconds:0}s no-response timeout (remote tool hang).");
        }
    }

    private async Task<McpClient> EnsureConnectedNoLockAsync(CancellationToken ct)
    {
        if (_client is not null)
        {
            return _client;
        }

        // 远程配置（Url）→ SDK HttpClientTransport（SSE / Streamable HTTP）；
        // 本地配置 → 既有 StdioClientTransport（拉起子进程）。传输选择集中在这里。
        IClientTransport transport;
        if (McpTransportFactory.ResolveKind(_config) == McpTransportKind.Stdio)
        {
            transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = ServerId,
                Command = _config.Command,
                Arguments = _config.Arguments,
                EnvironmentVariables = ToEnvironmentMap(_config.EnvironmentVariables, _logger, ServerId),
                WorkingDirectory = _config.WorkingDirectory,
            });
        }
        else
        {
            transport = McpTransportFactory.CreateHttpTransport(
                _config, _logger, _tokenProvider, InitializationTimeout);
        }

        try
        {
            _client = await McpClient.CreateAsync(
                transport,
                new McpClientOptions { InitializationTimeout = InitializationTimeout },
                loggerFactory: null,
                cancellationToken: ct);
        }
        catch
        {
            // 握手失败如实上抛；子进程/HTTP 连接生命周期由 SDK transport 内部管理。
            throw;
        }

        _logger?.LogInformation("MCP server '{ServerId}' connected via {Kind}", ServerId, McpTransportFactory.ResolveKind(_config));
        return _client;
    }

    private async Task DisposeClientNoLockAsync()
    {
        if (_client is null)
        {
            return;
        }

        var client = _client;
        _client = null;
        try
        {
            await client.DisposeAsync(); // transport 随之终止子进程
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("[DEGRADED] disposing MCP client '{ServerId}' raised: {Error}", ServerId, ex.Message);
        }
    }

    /// <summary>连接丢失类异常（stdio i/o 断裂、客户端已处置、帧解析失败）。
    /// SDK 可能把这些再包一层，解包看 InnerException。</summary>
    private static bool IsConnectionLoss(Exception ex) =>
        IsConnectionLossCore(ex)
        || (ex is not OperationCanceledException
            && ex.InnerException is { } inner
            && IsConnectionLossCore(inner));

    private static bool IsConnectionLossCore(Exception ex) =>
        ex is IOException or EndOfStreamException or ObjectDisposedException or JsonException;

    private static string SchemaText(JsonElement schema) =>
        schema.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? "{}" : schema.GetRawText();

    /// <summary>${ENV_NAME} 引用展开（整值引用才展开；字面量原样透传，向后兼容）。</summary>
    private static readonly System.Text.RegularExpressions.Regex EnvRefRx =
        new(@"^\$\{([A-Za-z_][A-Za-z0-9_]*)\}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// 子进程环境变量：值支持 ${ENV_NAME} 引用——从当前进程环境展开，
    /// 让 settings.json 不必把 API key 等敏感值明文落盘；字面量继续透传（向后兼容）。
    /// 引用未解析（变量未设置）时按"未设置"传给子进程（null）并大声 [DEGRADED] 记录。
    /// </summary>
    private static IDictionary<string, string?>? ToEnvironmentMap(
        Dictionary<string, string>? source, ILogger? logger, string serverId)
    {
        if (source is null)
        {
            return null;
        }

        var map = new Dictionary<string, string?>(source.Count, StringComparer.Ordinal);
        foreach (var kv in source)
        {
            var match = kv.Value is null ? null : EnvRefRx.Match(kv.Value);
            if (match is { Success: true })
            {
                var envName = match.Groups[1].Value;
                var expanded = Environment.GetEnvironmentVariable(envName);
                if (expanded is null)
                {
                    logger?.LogWarning(
                        "[DEGRADED] MCP server '{ServerId}' 的 {Key} 引用 ${Var} 未设置，子进程该变量将为未设置",
                        serverId, kv.Key, envName);
                }
                map[kv.Key] = expanded;
            }
            else
            {
                map[kv.Key] = kv.Value;
            }
        }

        return map;
    }
}
