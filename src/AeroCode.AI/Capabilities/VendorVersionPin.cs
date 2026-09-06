using System;
using System.Collections.Generic;
using AeroCode.AI.Configuration;

namespace AeroCode.AI.Capabilities;

/// <summary>
/// B6 版本 pinning：单个 provider 的 API 版本锁（随请求头发送）。
/// 字段值全部来自 <see cref="ProviderConfig.ApiVersionHeaders"/> 配置，绝不在此硬编码版本/密钥。
/// 配置未设置（null）= 无 pin = 各 provider 沿用自身现行为。
/// </summary>
public sealed record VendorVersionPin
{
    /// <summary>pin 归属的 providerId（取自 ProviderConfig.Id）。</summary>
    public required string ProviderId { get; init; }

    /// <summary>版本锁 header（键=header 名，值=版本串，例如 anthropic-version=2023-06-01）。只读快照。</summary>
    public required IReadOnlyDictionary<string, string> Headers { get; init; }

    /// <summary>从 ProviderConfig 解析版本锁；未配置 ApiVersionHeaders 或为空 → null（= 现行为）。</summary>
    public static VendorVersionPin? From(ProviderConfig config)
    {
        if (config is null) return null;
        if (config.ApiVersionHeaders is not { Count: > 0 }) return null;
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in config.ApiVersionHeaders)
        {
            if (string.IsNullOrWhiteSpace(kv.Key) || kv.Value is null) continue;
            headers[kv.Key] = kv.Value;
        }
        if (headers.Count == 0) return null;
        return new VendorVersionPin { ProviderId = config.Id, Headers = headers };
    }
}
