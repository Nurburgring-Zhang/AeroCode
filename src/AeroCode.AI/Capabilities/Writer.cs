using System.Threading;
using System.Threading.Tasks;
using AeroCode.AI.Models;
using AeroCode.AI.Providers;
using Microsoft.Extensions.Logging;

namespace AeroCode.AI.Capabilities;

/// <summary>
/// 写作助手:基于用户给的主题 + 上下文,生成结构化内容。
/// </summary>
public sealed class Writer : ITextCapability
{
    private readonly IAiProvider _provider;
    private readonly ILogger<Writer> _logger;
    public string Name => "write";
    public string Description => "基于主题/上下文生成结构化文本";

    public Writer(IAiProvider provider, ILogger<Writer> logger) { _provider = provider; _logger = logger; }

    public async Task<string> ExecuteAsync(string input, string? systemHint = null, CancellationToken ct = default)
    {
        var req = new ChatRequest
        {
            Model = string.Empty,
            Messages = new[]
            {
                new ChatMessage { Role = "system", Content = "你是资深写作助手。基于用户的题目/上下文,生成结构清晰、可直接使用的内容。用 Markdown 排版,不要寒暄。" },
                new ChatMessage { Role = "user", Content = string.IsNullOrEmpty(systemHint) ? input : $"[要求]\n{systemHint}\n\n[题目]\n{input}" }
            },
            Stream = false,
            Temperature = 0.7,
            MaxTokens = 4096,
            EnableThinking = false
        };
        var resp = await _provider.ChatAsync(req, ct);
        return (resp.Content ?? "").Trim();
    }
}
