using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AeroAgent.Moa.Tools;
using AeroCode.AI.Models;
using AeroCode.Mcp.Client;
using Microsoft.Extensions.Logging;

namespace AeroCode.App.Mcp;

/// <summary>
/// MCP 工具域：把若干 <see cref="IMcpGateway"/>（每个一个 stdio 子进程服务器）
/// 聚合为一个 <see cref="IWorkerToolbox"/>，供 WorkerRunner 的工具循环消费。
///
/// 跨服务器工具名去重：本地名 = 清洗后的 "{serverId}_{remoteName}"
/// （非法字符→下划线，≤64 字符，冲突追加 _2/_3…）。路由表精确映射回
/// (网关, 远端原名)，调用时还原——provider 只见合法的本地名。
///
/// 发现阶段单个服务器失败不阻塞全局：该服务器如实贡献 0 个工具，
/// 原因记入 <see cref="DiscoveryWarnings"/>（不静默、不伪造）。
/// </summary>
public sealed class McpToolbox : IWorkerToolbox, IAsyncDisposable
{
    /// <summary>工具名合法字符集（与 ToolboxRegistry 注册校验一致）。</summary>
    private const int MaxToolNameLength = 64;

    private readonly IReadOnlyList<IMcpGateway> _gateways;
    private readonly ILogger? _logger;
    private readonly Dictionary<string, Route> _routes = new(StringComparer.Ordinal);
    private readonly List<ToolDefinition> _definitions = new();
    private readonly List<string> _warnings = new();
    private bool _discovered;

    private sealed record Route(IMcpGateway Gateway, string RemoteName);

    /// <param name="gateways">已建好的网关集合；本工具箱接管其生命周期（Dispose 时一并释放）。</param>
    public McpToolbox(IReadOnlyList<IMcpGateway> gateways, ILogger? logger = null)
    {
        _gateways = gateways ?? throw new ArgumentNullException(nameof(gateways));
        _logger = logger;
    }

    public string Domain => "mcp";

    /// <inheritdoc/>
    public IReadOnlyList<ToolDefinition> Definitions => _discovered
        ? _definitions
        : throw new InvalidOperationException("McpToolbox 尚未完成 DiscoverAsync，工具定义不可用");

    /// <summary>发现期警告（某服务器连接失败等原因），只读快照。</summary>
    public IReadOnlyList<string> DiscoveryWarnings => _warnings;

    /// <summary>本地工具名 → (服务器 Id, 远端工具名)。UI 展示/调试用。</summary>
    public bool TryGetRoute(string localToolName, out string serverId, out string remoteName)
    {
        if (_routes.TryGetValue(localToolName, out var route))
        {
            serverId = route.Gateway.ServerId;
            remoteName = route.RemoteName;
            return true;
        }

        serverId = string.Empty;
        remoteName = string.Empty;
        return false;
    }

    /// <summary>连接全部启用网关并发现工具。必须在注册进 ToolboxRegistry 之前调用一次。</summary>
    public async Task DiscoverAsync(CancellationToken ct = default)
    {
        if (_discovered)
        {
            throw new InvalidOperationException("DiscoverAsync 只应调用一次");
        }

        foreach (var gateway in _gateways)
        {
            IReadOnlyList<McpToolInfo> tools;
            try
            {
                tools = await gateway.ListToolsAsync(ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var warning = $"MCP 服务器 '{gateway.ServerId}' 发现失败：{ex.Message}";
                _logger?.LogWarning("[DEGRADED] {Warning}", warning);
                _warnings.Add(warning);
                continue;
            }

            foreach (var tool in tools)
            {
                var localName = MakeLocalName(gateway.ServerId, tool.Name);
                _routes[localName] = new Route(gateway, tool.Name);
                _definitions.Add(new ToolDefinition
                {
                    Name = localName,
                    Description = $"[MCP:{gateway.ServerId}] {tool.Description}",
                    ParametersJsonSchema = string.IsNullOrWhiteSpace(tool.ParametersJsonSchema)
                        ? "{}"
                        : tool.ParametersJsonSchema,
                });
            }
        }

        _discovered = true;
        _logger?.LogInformation("McpToolbox 发现 {Count} 个工具，来自 {Servers} 个服务器",
            _definitions.Count, _gateways.Count);
    }

    /// <inheritdoc/>
    public async Task<ToolInvokeResult> InvokeAsync(string toolName, string argumentsJson, CancellationToken ct)
    {
        // 定义尚未发现：如实报错，不假装无工具可用。
        var definitions = Definitions;
        if (!_routes.TryGetValue(toolName, out var route))
        {
            return ToolInvokeResult.Fail($"MCP 工具 '{toolName}' 不存在（已发现 {definitions.Count} 个工具）");
        }

        IReadOnlyDictionary<string, object?>? arguments;
        try
        {
            arguments = ParseArguments(argumentsJson);
        }
        catch (JsonException ex)
        {
            return ToolInvokeResult.Fail($"工具参数 JSON 非法：{ex.Message}");
        }

        try
        {
            var outcome = await route.Gateway.CallToolAsync(route.RemoteName, arguments, ct);
            if (outcome.IsError)
            {
                var detail = string.IsNullOrWhiteSpace(outcome.Text)
                    ? "MCP 服务器返回错误但未提供详情"
                    : outcome.Text;
                return ToolInvokeResult.Fail(detail);
            }

            return ToolInvokeResult.Ok(outcome.Text);
        }
        catch (OperationCanceledException)
        {
            // 取消必须透传：工具循环上层统一落库 Cancelled 状态。
            throw;
        }
        catch (Exception ex)
        {
            return ToolInvokeResult.Fail($"MCP 调用失败：{ex.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var gateway in _gateways)
        {
            try
            {
                await gateway.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning("[DEGRADED] 释放 MCP 网关 '{ServerId}' 出错：{Error}",
                    gateway.ServerId, ex.Message);
            }
        }
    }

    /// <summary>参数 JSON → SDK 需要的字典。null/空 = 无参数；非对象根如实抛 JsonException。</summary>
    internal static IReadOnlyDictionary<string, object?>? ParseArguments(string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return null;
        }

        var parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(argumentsJson);
        if (parsed is null)
        {
            return null;
        }

        var map = new Dictionary<string, object?>(parsed.Count, StringComparer.Ordinal);
        foreach (var kv in parsed)
        {
            // JsonElement 原样入参：SDK 序列化时按其原始 JSON 输出，类型零损耗。
            map[kv.Key] = kv.Value;
        }

        return map;
    }

    /// <summary>"{serverId}_{remoteName}" 清洗为合法工具名，超长截断，冲突加数字后缀。</summary>
    internal string MakeLocalName(string serverId, string remoteName)
    {
        var sanitized = Sanitize($"{serverId}_{remoteName}");
        if (sanitized.Length > MaxToolNameLength)
        {
            sanitized = sanitized[^MaxToolNameLength..];
        }

        if (!_routes.ContainsKey(sanitized))
        {
            return sanitized;
        }

        for (var i = 2; ; i++)
        {
            var suffix = "_" + i.ToString(CultureInfo.InvariantCulture);
            var head = sanitized.Length + suffix.Length <= MaxToolNameLength
                ? sanitized
                : sanitized[..(MaxToolNameLength - suffix.Length)];
            var candidate = head + suffix;
            if (!_routes.ContainsKey(candidate))
            {
                return candidate;
            }
        }
    }

    private static string Sanitize(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            sb.Append(char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' ? ch : '_');
        }

        return sb.Length == 0 ? "mcp_tool" : sb.ToString();
    }
}
