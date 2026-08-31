// Copyright (c) AeroCode
// LLM 智能审批契约（批次 B G3 契约钉死）。builder-β 实现 PermissionAdvisor。
// 对标 goose permission_judge / hermes approval / codex guardian：辅助模型只读判定，
// 结果并入审批弹窗（建议+风险+理由）；低风险自动采纳需显式设置开关。
using System.Text.Json;
using AeroCode.AI.Models;
using AeroCode.AI.Providers;

namespace AeroAgent.Moa.Safety;

/// <summary>判定结果（risk 三级；unknown=模型无法判定，按无建议处理）。</summary>
public sealed record PermissionAdvice(string Recommend, string Risk, string Reason)
{
    public static PermissionAdvice Unknown() => new("none", "unknown", "advisor unavailable");
}

/// <summary>
/// 审批建议器（只读：绝不执行工具，只给建议）。实现约束：
/// 1. 走 Provider 层小模型（可用模型画像挑便宜档），独立短超时（默认 8s）；
/// 2. 任何失败返回 <see cref="PermissionAdvice.Unknown"/>——建议器故障绝不阻塞审批主链；
/// 3. 输出 JSON 解析容错（模型输出非 JSON 时按 unknown 处理）。
/// </summary>
public interface IPermissionAdvisor
{
    /// <summary>是否可用（未配置判定模型时 false，调用方跳过）。</summary>
    bool IsAvailable { get; }

    Task<PermissionAdvice> AdviseAsync(string toolName, IReadOnlyDictionary<string, object?>? args, CancellationToken ct);
}

/// <summary>
/// LLM 审批建议器（builder-β）：走 Provider 层小模型只读判定。
/// 任何失败（超时/取消/网络/协议/坏 JSON）一律返回 <see cref="PermissionAdvice.Unknown"/>——
/// 建议器故障绝不阻塞审批主链；未配置（provider/model 缺失）时 IsAvailable=false，
/// 调用方跳过，行为与无 advisor 完全一致。模型与超时构造注入（便宜档画像由组合根挑选）。
/// </summary>
public sealed class PermissionAdvisor : IPermissionAdvisor
{
    /// <summary>默认独立超时：8 秒（契约钉死；判定模型必须比审批流程快）。</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(8);

    /// <summary>参数预览注入提示词的长度上限（超长截断并如实标注）。</summary>
    public const int MaxArgsPreviewLength = 4000;

    private const string SystemPrompt =
        "You are a read-only permission advisor for a coding agent. Assess the requested tool call " +
        "and reply with ONLY a JSON object, no other text:\n" +
        "{\"recommend\":\"allow|ask|deny\",\"risk\":\"low|medium|high\",\"reason\":\"one short sentence\"}\n" +
        "recommend = what the human should decide; risk = blast radius if it goes wrong. " +
        "You must never execute anything; you only judge.";

    private readonly IAiProvider? _provider;
    private readonly string _model;
    private readonly TimeSpan _timeout;

    public PermissionAdvisor(IAiProvider? provider, string model, TimeSpan? timeout = null)
    {
        _provider = provider;
        _model = model ?? string.Empty;
        _timeout = timeout is { } t && t > TimeSpan.Zero ? t : DefaultTimeout;
    }

    /// <inheritdoc />
    public bool IsAvailable => _provider is not null && !string.IsNullOrWhiteSpace(_model);

    /// <inheritdoc />
    public async Task<PermissionAdvice> AdviseAsync(
        string toolName, IReadOnlyDictionary<string, object?>? args, CancellationToken ct)
    {
        var provider = _provider;
        if (provider is null || string.IsNullOrWhiteSpace(_model))
        {
            return PermissionAdvice.Unknown();
        }

        var request = new ChatRequest
        {
            Model = _model,
            Temperature = 0,
            MaxTokens = 256,
            EnableThinking = false,
            ThinkingEffort = null, // 判定模型不需要 thinking：快与便宜优先
            Messages = new List<ChatMessage>
            {
                new() { Role = "system", Content = SystemPrompt },
                new() { Role = "user", Content = BuildUserPrompt(toolName, args) },
            },
        };

        // 独立短超时（与调用方 ct 链接）：判定卡死不能拖住审批主链。
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_timeout);
        try
        {
            var response = await provider.ChatAsync(request, cts.Token).ConfigureAwait(false);
            return ParseAdvice(response?.Content);
        }
        catch (Exception)
        {
            // 契约：任何失败（超时/取消/网络/协议）一律 Unknown，绝不向审批主链抛异常。
            return PermissionAdvice.Unknown();
        }
    }

    /// <summary>容错解析：剥掉前后废话/代码围栏取首个 {…} 块；recommend 缺失或不可识别 → Unknown。</summary>
    internal static PermissionAdvice ParseAdvice(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return PermissionAdvice.Unknown();
        }

        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return PermissionAdvice.Unknown();
        }

        try
        {
            using var doc = JsonDocument.Parse(content[start..(end + 1)]);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return PermissionAdvice.Unknown();
            }

            var recommendRaw = GetString(root, "recommend");
            var recommend = NormalizeRecommend(recommendRaw);
            if (recommend is null)
            {
                return PermissionAdvice.Unknown();
            }

            var riskRaw = GetString(root, "risk");
            var risk = riskRaw is null ? "unknown" : NormalizeRisk(riskRaw);
            var reason = GetString(root, "reason") ?? string.Empty;
            return new PermissionAdvice(recommend, risk, reason);
        }
        catch (JsonException)
        {
            return PermissionAdvice.Unknown();
        }
    }

    private static string BuildUserPrompt(string toolName, IReadOnlyDictionary<string, object?>? args)
    {
        string preview;
        try
        {
            preview = JsonSerializer.Serialize(args ?? new Dictionary<string, object?>());
        }
        catch (Exception)
        {
            // 参数含不可序列化对象时退化为逐项列举——绝不吞掉信息。
            preview = string.Join("\n", (args ?? new Dictionary<string, object?>())
                .Select(kv => $"{kv.Key} = {kv.Value ?? "null"}"));
        }

        if (preview.Length > MaxArgsPreviewLength)
        {
            preview = preview[..MaxArgsPreviewLength] + "\n…(args preview truncated)";
        }

        return $"Tool: {toolName}\nArguments: {preview}\nJudge and reply with the JSON object only.";
    }

    private static string? GetString(JsonElement root, string name)
    {
        foreach (var prop in root.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase) &&
                prop.Value.ValueKind == JsonValueKind.String)
            {
                return prop.Value.GetString();
            }
        }

        return null;
    }

    /// <summary>recommend 同义词归一；无法识别返回 null（= Unknown）。</summary>
    private static string? NormalizeRecommend(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "allow" or "approve" or "permit" or "yes" => "allow",
            "ask" or "confirm" or "prompt" or "review" => "ask",
            "deny" or "block" or "reject" or "forbid" or "refuse" => "deny",
            _ => null,
        };
    }

    /// <summary>risk 归一到 low/medium/high；其余一律 "unknown"（按无风险分级处理）。</summary>
    private static string NormalizeRisk(string raw) => raw.Trim().ToLowerInvariant() switch
    {
        "low" => "low",
        "medium" or "mid" => "medium",
        "high" => "high",
        _ => "unknown",
    };
}
