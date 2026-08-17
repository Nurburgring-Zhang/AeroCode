using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AeroCode.AI.Models;
using AeroCode.AI.Providers;
using Microsoft.Extensions.Logging;

namespace AeroCode.AI.Capabilities;

/// <summary>
/// 自动从笔记提取 1-5 个标签。返回 List&lt;string&gt;。
/// </summary>
public sealed class AutoTagger : ICapability
{
    private readonly IAiProvider _provider;
    private readonly ILogger<AutoTagger> _logger;
    public string Name => "auto_tag";
    public string Description => "从笔记内容自动提取 1-5 个简短标签(2-6 字)";

    public AutoTagger(IAiProvider provider, ILogger<AutoTagger> logger) { _provider = provider; _logger = logger; }

    public async Task<IReadOnlyList<string>> ExtractAsync(string content, int maxTags = 5, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(content)) return Array.Empty<string>();
        var req = new ChatRequest
        {
            Model = string.Empty,
            Messages = new[]
            {
                new ChatMessage { Role = "system", Content = "You are a tag extractor. Output ONLY a JSON array of 1-5 short tags (each 2-6 characters). Do NOT explain. Do NOT output any prose. Example: [\"AI\", \"notes\", \"productivity\"]" },
                new ChatMessage { Role = "user", Content = $"Extract up to {maxTags} tags for: {content}\nOutput: JSON array only, no other text." }
            },
            Stream = false,
            Temperature = 0.1,
            MaxTokens = 800,
            EnableThinking = false
        };
        var resp = await _provider.ChatAsync(req, ct);
        var text = (resp.Content ?? "").Trim();
        // 兼容模型偶尔带 ```json ... ``` 包装
        if (text.StartsWith("```"))
        {
            var start = text.IndexOf('[');
            var end = text.LastIndexOf(']');
            if (start >= 0 && end > start) text = text[start..(end + 1)];
        }
        try
        {
            var arr = JsonSerializer.Deserialize<List<string>>(text);
            return (arr ?? new()).Where(s => !string.IsNullOrWhiteSpace(s)).Take(maxTags).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AutoTagger failed to parse: {Text}", text);
            return Array.Empty<string>();
        }
    }
}
