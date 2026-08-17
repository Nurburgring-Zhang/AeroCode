using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AeroAgent.Moa.Tools;
using AeroCode.AI.Models;
using AeroCode.AI.Providers;
using AeroCode.Skills;
using AeroCode.Skills.Registry;
using Microsoft.Extensions.Logging;
using AiChatMessage = AeroCode.AI.Models.ChatMessage;

namespace AeroCode.App.Tools;

/// <summary>
/// 内建技能工具域：把 SkillHub 的技能库暴露为两个模型工具——
/// list_skills（发现）与 run_skill（真实执行）。
/// run_skill 走 <see cref="ISkill.ExecuteAsync"/> 真实链路：需要推理的技能
/// （如 analysis/deep_audit）经 <see cref="SkillContext.LlmInvoker"/> 调用
/// 当前默认 provider 的真实模型（真实 HTTP），绝不返回模板占位。
/// </summary>
public sealed class SkillToolbox : IWorkerToolbox
{
    private readonly SkillHub _hub;
    private readonly IProviderRegistry _providers;
    private readonly string _workspaceRoot;
    private readonly ILogger? _logger;

    public SkillToolbox(
        SkillHub hub,
        IProviderRegistry providers,
        string workspaceRoot,
        ILogger? logger = null)
    {
        _hub = hub ?? throw new ArgumentNullException(nameof(hub));
        _providers = providers ?? throw new ArgumentNullException(nameof(providers));
        _workspaceRoot = workspaceRoot ?? throw new ArgumentNullException(nameof(workspaceRoot));
        _logger = logger;
    }

    public string Domain => "skills";

    public IReadOnlyList<ToolDefinition> Definitions { get; } = new List<ToolDefinition>
    {
        new()
        {
            Name = "list_skills",
            Description = "列出技能库中全部可用技能（内建 + 用户安装），含 id/描述/分类/版本",
            ParametersJsonSchema = """
                {"type":"object","properties":{"category":{"type":"string","description":"按分类过滤，可选（engineering/productivity/analysis/...）"}}}
                """,
        },
        new()
        {
            Name = "run_skill",
            Description = "按 id 执行一个技能并返回其真实输出。args 为技能自身参数（各技能不同，见 list_skills 描述）",
            ParametersJsonSchema = """
                {"type":"object","properties":{"skill_id":{"type":"string","description":"技能 id（list_skills 返回的 id 字段）"},"user_message":{"type":"string","description":"触发该技能的用户原话，可选"},"args":{"type":"object","description":"传给技能的参数对象，可选"}},"required":["skill_id"]}
                """,
        },
    };

    public async Task<ToolInvokeResult> InvokeAsync(string toolName, string argumentsJson, CancellationToken ct)
    {
        JsonElement args;
        try
        {
            args = ParseObject(argumentsJson);
        }
        catch (JsonException ex)
        {
            return ToolInvokeResult.Fail($"工具参数 JSON 非法：{ex.Message}");
        }

        try
        {
            return toolName switch
            {
                "list_skills" => ListSkills(args),
                "run_skill" => await RunSkillAsync(args, ct),
                _ => ToolInvokeResult.Fail($"技能工具 '{toolName}' 不存在"),
            };
        }
        catch (OperationCanceledException)
        {
            // 取消必须透传：工具循环上层统一落库 Cancelled 状态。
            throw;
        }
        catch (ToolArgumentException ex)
        {
            return ToolInvokeResult.Fail(ex.Message);
        }
        catch (Exception ex)
        {
            return ToolInvokeResult.Fail($"技能工具执行失败：{ex.Message}");
        }
    }

    private ToolInvokeResult ListSkills(JsonElement args)
    {
        var category = GetOptionalString(args, "category");
        var skills = _hub.List(category)
            .Select(s => new
            {
                id = s.Id,
                name = s.Name,
                description = s.Description,
                category = s.Category,
                version = s.Version,
                tags = s.Tags,
                available = s.IsAvailable(),
            })
            .ToList();
        return ToolInvokeResult.Ok(JsonSerializer.Serialize(new { count = skills.Count, skills }));
    }

    private async Task<ToolInvokeResult> RunSkillAsync(JsonElement args, CancellationToken ct)
    {
        var skillId = GetRequiredString(args, "skill_id");
        var userMessage = GetOptionalString(args, "user_message") ?? string.Empty;
        var skillArgs = ExtractSkillArgs(args);

        var skill = _hub.Get(skillId);
        if (skill is null)
        {
            return ToolInvokeResult.Fail(
                $"技能 '{skillId}' 不存在（当前注册 {_hub.List().Count} 个技能，用 list_skills 查看 id）");
        }

        var input = new SkillInput { Args = skillArgs, UserMessage = userMessage };
        var context = new SkillContext
        {
            WorkspaceRoot = _workspaceRoot,
            UserMessage = userMessage,
            LlmInvoker = InvokeLlmAsync,
        };

        _logger?.LogInformation("run_skill 执行 '{SkillId}'", skillId);
        var result = await skill.ExecuteAsync(input, context, ct);

        if (!result.Success)
        {
            // 技能自报失败：原因如实交还模型（Degraded 行），不当作成功粉饰。
            return ToolInvokeResult.Fail(string.IsNullOrWhiteSpace(result.Text)
                ? $"技能 '{skillId}' 执行失败且未提供原因"
                : result.Text);
        }

        var payload = new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["text"] = result.Text,
            ["next_actions"] = result.NextActions,
        };
        if (result.Data is not null)
        {
            payload["data"] = result.Data;
        }

        return ToolInvokeResult.Ok(JsonSerializer.Serialize(payload));
    }

    /// <summary>
    /// 技能 LLM 通道：真实调用当前默认 provider 的默认模型（非流式）。
    /// 未配置默认 provider/模型时如实抛错——技能侧会把失败原因写进报告，绝不伪造分析。
    /// options 参数为 LlmInvoker 契约保留（当前无按次覆盖需求）。
    /// </summary>
    private async Task<string> InvokeLlmAsync(
        string prompt, IReadOnlyDictionary<string, object?>? options, CancellationToken ct)
    {
        _ = options; // 契约保留位：当前技能没有按次模型覆盖需求

        var providerId = _providers.DefaultProviderId;
        if (!_providers.TryGetConfig(providerId, out var config))
        {
            throw new InvalidOperationException(
                $"默认 provider '{providerId}' 未配置，技能 LLM 调用无法执行");
        }

        if (string.IsNullOrWhiteSpace(config.DefaultModel))
        {
            throw new InvalidOperationException(
                $"默认 provider '{providerId}' 未设置默认模型，技能 LLM 调用无法执行");
        }

        var provider = _providers.Get(providerId);
        var response = await provider.ChatAsync(new ChatRequest
        {
            Model = config.DefaultModel,
            Messages = new[] { new AiChatMessage { Role = "user", Content = prompt } },
            Stream = false,
        }, ct);
        return response.Content;
    }

    /// <summary>args 字段 → 技能参数字典。整型按范围解包为 int/long（技能代码常用 "is int" 模式匹配）。</summary>
    private static IReadOnlyDictionary<string, object?> ExtractSkillArgs(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("args", out var argsEl)
            || argsEl.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return new Dictionary<string, object?>();
        }

        if (argsEl.ValueKind != JsonValueKind.Object)
        {
            throw new ToolArgumentException("参数 'args' 必须是对象");
        }

        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var prop in argsEl.EnumerateObject())
        {
            dict[prop.Name] = Unwrap(prop.Value);
        }

        return dict;
    }

    private static object? Unwrap(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => UnwrapNumber(element),
        JsonValueKind.Null => null,
        _ => element.GetRawText(), // 数组/嵌套对象保留原始 JSON 文本，技能按需解析
    };

    private static object UnwrapNumber(JsonElement element)
    {
        if (element.TryGetInt32(out var asInt))
        {
            return asInt;
        }

        if (element.TryGetInt64(out var asLong))
        {
            return asLong;
        }

        return element.GetDouble();
    }

    private static JsonElement ParseObject(string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return default; // ValueKind.Undefined = 无参数
        }

        using var doc = JsonDocument.Parse(argumentsJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new ToolArgumentException("工具参数根节点必须是 JSON 对象");
        }

        return doc.RootElement.Clone();
    }

    private static string GetRequiredString(JsonElement root, string name)
    {
        var value = GetOptionalString(root, name);
        if (value is null)
        {
            throw new ToolArgumentException($"缺少必填参数 '{name}'");
        }

        return value;
    }

    private static string? GetOptionalString(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty(name, out var el)
            || el.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (el.ValueKind != JsonValueKind.String)
        {
            throw new ToolArgumentException($"参数 '{name}' 必须是字符串");
        }

        return el.GetString();
    }

    /// <summary>参数语义错误（缺参/类型不符）：消息直接交还模型，便于其自我纠正。</summary>
    private sealed class ToolArgumentException : Exception
    {
        public ToolArgumentException(string message)
            : base(message)
        {
        }
    }
}
