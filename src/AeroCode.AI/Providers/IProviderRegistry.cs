using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using AeroCode.AI.Configuration;

namespace AeroCode.AI.Providers;

/// <summary>
/// Provider 解析抽象。编排层依赖此接口而非具体工厂（依赖倒置），
/// 生产实现为 <see cref="ProviderFactory"/>，测试可注入编程式注册表。
/// </summary>
public interface IProviderRegistry
{
    /// <summary>全局默认 provider 的 Id。</summary>
    string DefaultProviderId { get; }

    /// <summary>按 Id 取 provider 实例（未配置时抛出）。</summary>
    IAiProvider Get(string providerId);

    /// <summary>查询 provider 配置（编排层解析默认模型用）。未配置返回 false。</summary>
    bool TryGetConfig(string providerId, [NotNullWhen(true)] out ProviderConfig? config);

    /// <summary>列出已配置的 provider Id（UI 下拉枚举用）。</summary>
    IEnumerable<string> ListConfiguredIds();
}
