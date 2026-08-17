using System.Text.Json;
using AeroCode.AI.Models;
using AeroCode.Harness.Permission;

namespace AeroAgent.Moa.Tools;

/// <summary>
/// 工具调用执行入口：授权裁决 → 注册中心执行。
/// <see cref="PermissionPolicy"/> 是唯一裁决源（Allow/Deny/Ask）：
/// Ask 交由 <see cref="IPermissionBroker"/> 向用户要决定；无代理时诚实拒绝，绝不静默放行。
/// 用户显式 Deny 的工具不会被任何 Override 翻回 Allow（策略内部已保证）。
/// 本类只执行裁决与分发，不新增也不吞掉任何裁决。
/// </summary>
public sealed class ToolRouter
{
    private readonly ToolboxRegistry _registry;
    private readonly PermissionPolicy _policy;
    private readonly IPermissionBroker? _broker;

    public ToolRouter(ToolboxRegistry registry, PermissionPolicy policy, IPermissionBroker? broker = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _broker = broker;
    }

    /// <summary>是否有可用工具（false 时 worker 走普通调用，不携带 tools）。</summary>
    public bool HasTools => _registry.HasTools;

    /// <summary>全部工具定义（注入 ChatRequest.Tools）。</summary>
    public IReadOnlyList<ToolDefinition> Definitions => _registry.AllDefinitions();

    /// <summary>
    /// 裁决并执行一次工具调用。永不抛业务异常——授权拒绝与执行失败都以
    /// <see cref="ToolInvokeResult"/> 如实返回，模型能看到原因并自行调整。
    /// </summary>
    public async Task<ToolInvokeResult> InvokeAsync(
        string toolName, string argumentsJson, CancellationToken ct)
    {
        var args = MaterializeArgs(argumentsJson);
        var decision = _policy.Check(toolName, args).Decision;

        if (decision == PermissionDecision.Ask)
        {
            if (_broker is null)
            {
                return ToolInvokeResult.Deny(
                    $"Permission denied: tool '{toolName}' requires user authorization, " +
                    "but no authorization broker is available");
            }

            decision = await _broker.ResolveAsync(toolName, args, ct);
            if (decision != PermissionDecision.Allow)
            {
                return ToolInvokeResult.Deny($"Permission denied: user declined tool '{toolName}'");
            }
        }

        if (decision == PermissionDecision.Deny)
        {
            return ToolInvokeResult.Deny($"Permission denied: tool '{toolName}' is forbidden by policy");
        }

        return await _registry.InvokeAsync(toolName, argumentsJson, ct);
    }

    /// <summary>
    /// 参数 JSON → 物化字典，供 <see cref="PermissionPolicy"/> 的 Override 读取
    /// （如 run_shell 需要以 string 取 command 做危险模式匹配）。
    /// JsonElement 会被解包为 string/long/double/bool/null；数组与嵌套对象保留原始文本。
    /// 空串/非法 JSON/非对象根 → null（策略按默认裁决走，工具域执行时自行校验参数）。
    /// </summary>
    internal static IReadOnlyDictionary<string, object?>? MaterializeArgs(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                dict[prop.Name] = Unwrap(prop.Value);
            }

            return dict;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static object? Unwrap(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => UnwrapNumber(element),
        JsonValueKind.Null => null,
        _ => element.GetRawText(),
    };

    /// <summary>整数保持 long、小数保持 double——不能用条件表达式合并（会被统一提升为 double）。</summary>
    private static object UnwrapNumber(JsonElement element)
    {
        if (element.TryGetInt64(out var integral))
        {
            return integral;
        }

        return element.GetDouble();
    }
}
