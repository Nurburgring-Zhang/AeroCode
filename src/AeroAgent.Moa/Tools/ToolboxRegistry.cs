using System.Text.RegularExpressions;
using AeroCode.AI.Models;

namespace AeroAgent.Moa.Tools;

/// <summary>
/// 工具域注册中心。模型只看到一份扁平工具清单，因此工具名必须全局唯一；
/// 名称还必须满足 provider 命名约束 ^[a-zA-Z0-9_-]{1,64}$（点号等字符非法——
/// MCP 域拼接 server 前缀时必须用下划线而非点号）。注册期即校验，
/// 不合规定义在进入模型请求前就被拒绝（诚实失败，不静默改名）。
/// </summary>
public sealed partial class ToolboxRegistry
{
    [GeneratedRegex("^[a-zA-Z0-9_-]{1,64}$")]
    private static partial Regex ToolNamePattern();

    private readonly object _sync = new();
    private readonly Dictionary<string, IWorkerToolbox> _toolByName = new(StringComparer.Ordinal);
    private readonly List<IWorkerToolbox> _toolboxes = new();

    /// <summary>已注册的工具总数（0 = 本轮调用不携带 tools）。</summary>
    public bool HasTools
    {
        get
        {
            lock (_sync)
            {
                return _toolByName.Count > 0;
            }
        }
    }

    /// <summary>注册一个工具域。工具名非法或与已注册工具重名时抛异常。</summary>
    public void Register(IWorkerToolbox toolbox)
    {
        ArgumentNullException.ThrowIfNull(toolbox);

        lock (_sync)
        {
            // 先全量校验再落库：避免注册到一半失败留下半成品域。
            foreach (var def in toolbox.Definitions)
            {
                if (!ToolNamePattern().IsMatch(def.Name))
                {
                    throw new ArgumentException(
                        $"toolbox '{toolbox.Domain}' declares invalid tool name '{def.Name}' " +
                        "(must match ^[a-zA-Z0-9_-]{1,64}$)",
                        nameof(toolbox));
                }

                if (_toolByName.ContainsKey(def.Name))
                {
                    throw new ArgumentException(
                        $"tool name '{def.Name}' already registered by another toolbox",
                        nameof(toolbox));
                }
            }

            foreach (var def in toolbox.Definitions)
            {
                _toolByName[def.Name] = toolbox;
            }

            _toolboxes.Add(toolbox);
        }
    }

    /// <summary>按域名移除工具箱（如 MCP server 断开）。未注册返回 false。</summary>
    public bool Unregister(string domain)
    {
        lock (_sync)
        {
            var box = _toolboxes.FirstOrDefault(b => string.Equals(b.Domain, domain, StringComparison.Ordinal));
            if (box is null)
            {
                return false;
            }

            foreach (var def in box.Definitions)
            {
                _toolByName.Remove(def.Name);
            }

            _toolboxes.Remove(box);
            return true;
        }
    }

    /// <summary>全部工具定义的快照（注入 ChatRequest.Tools 用）。</summary>
    public IReadOnlyList<ToolDefinition> AllDefinitions()
    {
        lock (_sync)
        {
            return _toolboxes.SelectMany(b => b.Definitions).ToList();
        }
    }

    /// <summary>按名执行。未知工具返回诚实失败（不抛异常）。</summary>
    public Task<ToolInvokeResult> InvokeAsync(string toolName, string argumentsJson, CancellationToken ct)
    {
        IWorkerToolbox? box;
        lock (_sync)
        {
            _toolByName.TryGetValue(toolName, out box);
        }

        if (box is null)
        {
            return Task.FromResult(ToolInvokeResult.Fail($"Tool '{toolName}' not found"));
        }

        return box.InvokeAsync(toolName, argumentsJson, ct);
    }
}
