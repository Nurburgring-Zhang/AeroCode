using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AeroCode.AI.Models;
using AeroCode.AI.Providers;
using Microsoft.Extensions.Logging;

namespace AeroCode.AI.Capabilities;

/// <summary>
/// 能力基类。封装 provider 选择 + prompt 构造 + 响应解析。
/// 所有具体能力(摘要/翻译/打标签/...)继承此类只需写 prompt 和后处理。
/// </summary>
public abstract class CapabilityBase : ITextCapability
{
    protected readonly IAiProvider Provider;
    protected readonly ILogger Logger;

    public abstract string Name { get; }
    public abstract string Description { get; }

    protected abstract string SystemPrompt { get; }
    protected abstract string BuildUserPrompt(string input, string? systemHint);

    protected virtual double Temperature => 0.3;
    protected virtual int MaxTokens => 2048;

    protected CapabilityBase(IAiProvider provider, ILogger logger)
    {
        Provider = provider;
        Logger = logger;
    }

    public virtual async Task<string> ExecuteAsync(string input, string? systemHint = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(input))
            throw new ArgumentException("input 不能为空", nameof(input));
        var req = new ChatRequest
        {
            Model = string.Empty,
            Messages = new[]
            {
                new ChatMessage { Role = "system", Content = SystemPrompt },
                new ChatMessage { Role = "user", Content = BuildUserPrompt(input, systemHint) }
            },
            Stream = false,
            Temperature = Temperature,
            MaxTokens = MaxTokens,
            EnableThinking = false // 摘要/翻译不需要深度推理,关闭提速
        };
        var resp = await Provider.ChatAsync(req, ct).ConfigureAwait(false);
        return (resp.Content ?? string.Empty).Trim();
    }
}
