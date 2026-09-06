using System;
using System.Collections.Generic;

namespace AeroCode.AI.Capabilities;

/// <summary>能力三态（wave2 R2 钉死签名，禁止改动）。Supported=运行时证实；Downgraded=文档化降级路径；Missing=未证实/探测失败。</summary>
public enum VendorCapabilityState
{
    Supported,
    Downgraded,
    Missing
}

/// <summary>被探测的厂商能力集合（wave2 R2 钉死签名，禁止改动）。</summary>
public enum VendorCapability
{
    StructuredOutputs,
    EffortTiers,
    PromptCache,
    Batch
}

/// <summary>
/// B2 厂商缓存政策数据模型：每厂商 cache 政策字段化，按 providerId 判定。
/// 数据层对 A(Anthropic)/G(Gemini)/O(OpenAI) 三家全部建模；
/// provider 侧实现只落 ClaudeProvider/OpenAIProvider/OpenAICompatibleProvider（仓库无 Gemini 专属 provider）。
/// 所有字段为不可变事实值，不含密钥。
/// </summary>
public sealed record VendorCachePolicy
{
    /// <summary>政策归属 providerId：A / G / O（与 VendorCachePolicies.For 的键一致）。</summary>
    public required string ProviderId { get; init; }

    /// <summary>政策形态：显式断点+TTL（Anthropic）/ 隐式共享前缀折扣（Gemini）/ off-peak 折扣（OpenAI）。</summary>
    public required VendorCachePolicyKind Kind { get; init; }

    // ---- A(Anthropic): cache_control TTL ----
    /// <summary>支持的 TTL 档位，例如 ["5m","1h"]；非 Anthropic 为空。</summary>
    public IReadOnlyList<string> SupportedTtls { get; init; } = Array.Empty<string>();

    /// <summary>5m 缓存写价倍率（1.25×）；未建模为 null。</summary>
    public double? CacheWriteMultiplier5m { get; init; }

    /// <summary>1h 缓存写价倍率（2.0×）；未建模为 null。</summary>
    public double? CacheWriteMultiplier1h { get; init; }

    // ---- G(Gemini): implicit 共享前缀折扣 ----
    /// <summary>隐式缓存命中折扣（0.75 = 75% 折扣）；未建模为 null。</summary>
    public double? ImplicitCacheDiscount { get; init; }

    /// <summary>隐式缓存最小前缀 token 数（Flash≥1024）；未建模为 null。</summary>
    public int? MinPrefixTokensFlash { get; init; }

    /// <summary>隐式缓存最小前缀 token 数（Pro≥2048）；未建模为 null。</summary>
    public int? MinPrefixTokensPro { get; init; }

    // ---- O(OpenAI): off-peak 折扣 ----
    /// <summary>off-peak 缓存折扣倍率（0.5×）；未建模为 null。</summary>
    public double? OffPeakMultiplier { get; init; }

    /// <summary>off-peak 窗口说明（"~1h"）；未建模为 null。</summary>
    public string? OffPeakWindow { get; init; }
}

/// <summary>缓存政策形态。</summary>
public enum VendorCachePolicyKind
{
    /// <summary>显式 cache_control 断点 + TTL（Anthropic Messages API）。</summary>
    ExplicitBreakpointTtl,

    /// <summary>隐式共享前缀折扣（Gemini，无需显式断点）。</summary>
    ImplicitSharedPrefix,

    /// <summary>off-peak 折扣窗口（OpenAI，约 1h）。</summary>
    OffPeakDiscount,

    /// <summary>无缓存政策（未知 providerId）。</summary>
    None
}

/// <summary>按 providerId 解析缓存政策。静态事实表，非能力探测（探测见 IVendorCapabilityProbe）。</summary>
public static class VendorCachePolicies
{
    /// <summary>Anthropic：cache_control TTL 5m/1h，缓存写价 1.25×/2×。</summary>
    public static readonly VendorCachePolicy Anthropic = new()
    {
        ProviderId = "A",
        Kind = VendorCachePolicyKind.ExplicitBreakpointTtl,
        SupportedTtls = new[] { "5m", "1h" },
        CacheWriteMultiplier5m = 1.25,
        CacheWriteMultiplier1h = 2.0
    };

    /// <summary>Gemini：implicit 共享前缀 75% 折扣，最小前缀 Flash≥1024 / Pro≥2048。仅数据/probe 层，无 provider 实现。</summary>
    public static readonly VendorCachePolicy Gemini = new()
    {
        ProviderId = "G",
        Kind = VendorCachePolicyKind.ImplicitSharedPrefix,
        ImplicitCacheDiscount = 0.75,
        MinPrefixTokensFlash = 1024,
        MinPrefixTokensPro = 2048
    };

    /// <summary>OpenAI：off-peak 0.5×（约 1h 窗口）。</summary>
    public static readonly VendorCachePolicy OpenAI = new()
    {
        ProviderId = "O",
        Kind = VendorCachePolicyKind.OffPeakDiscount,
        OffPeakMultiplier = 0.5,
        OffPeakWindow = "~1h"
    };

    /// <summary>按 providerId 判定（大小写不敏感，接受 "A"/"Anthropic"、"G"/"Gemini"、"O"/"OpenAI" 等别名）；未知返回 null。</summary>
    public static VendorCachePolicy? For(string? providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return null;
        }

        var id = providerId.Trim();
        if (id.Equals("A", StringComparison.OrdinalIgnoreCase) ||
            id.Equals("Anthropic", StringComparison.OrdinalIgnoreCase) ||
            id.Contains("claude", StringComparison.OrdinalIgnoreCase))
        {
            return Anthropic;
        }

        if (id.Equals("G", StringComparison.OrdinalIgnoreCase) ||
            id.Equals("Gemini", StringComparison.OrdinalIgnoreCase) ||
            id.Contains("gemini", StringComparison.OrdinalIgnoreCase))
        {
            return Gemini;
        }

        if (id.Equals("O", StringComparison.OrdinalIgnoreCase) ||
            id.Equals("OpenAI", StringComparison.OrdinalIgnoreCase) ||
            id.EndsWith("openai-compatible", StringComparison.OrdinalIgnoreCase) ||
            id.StartsWith("openai", StringComparison.OrdinalIgnoreCase))
        {
            return OpenAI;
        }

        return null;
    }
}
