using System;
using System.Threading;
using System.Threading.Tasks;
using AeroCode.AI.Models;
using AeroCode.AI.Providers;
using Microsoft.Extensions.Logging;

namespace AeroCode.AI.Capabilities;

public sealed class Translator : ITranslationCapability
{
    private readonly IAiProvider _provider;
    private readonly ILogger<Translator> _logger;
    public string Name => "translate";
    public string Description => "把文本翻译到目标语言,保持原意";
    public Translator(IAiProvider provider, ILogger<Translator> logger) { _provider = provider; _logger = logger; }

    public async Task<string> TranslateAsync(string text, string targetLanguage, string? sourceLanguage = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("text 不能为空");
        var src = string.IsNullOrWhiteSpace(sourceLanguage) ? "自动检测" : sourceLanguage;
        var req = new ChatRequest
        {
            Model = string.Empty,
            Messages = new[]
            {
                new ChatMessage { Role = "system", Content = $"你是一名专业翻译。从 {src} 翻译到 {targetLanguage},保持原意、风格、术语。只输出翻译结果,不要解释。" },
                new ChatMessage { Role = "user", Content = text }
            },
            Stream = false,
            Temperature = 0.2,
            MaxTokens = 2048,
            EnableThinking = false
        };
        var resp = await _provider.ChatAsync(req, ct);
        return (resp.Content ?? string.Empty).Trim();
    }
}
