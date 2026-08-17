using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AeroCode.AI.Models;
using AeroCode.AI.Providers;
using Microsoft.Extensions.Logging;

namespace AeroCode.AI.Capabilities;

/// <summary>
/// 笔记问答:基于候选笔记回答用户问题,如未找到答案说"无相关信息"。
/// </summary>
public sealed class QuestionAnswerer : ICapability
{
    private readonly IAiProvider _provider;
    private readonly ILogger<QuestionAnswerer> _logger;
    public string Name => "qa";
    public string Description => "基于候选笔记回答问题,无答案时明确说明";

    public QuestionAnswerer(IAiProvider provider, ILogger<QuestionAnswerer> logger) { _provider = provider; _logger = logger; }

    public async Task<string> AnswerAsync(string question, IReadOnlyList<(long Id, string Title, string Content)> notes, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(question)) return string.Empty;
        if (notes.Count == 0) return "无相关信息 (无候选笔记)";  // Real-world guard
        var sb = new StringBuilder();
        sb.AppendLine("[候选笔记]");
        foreach (var n in notes)
        {
            sb.AppendLine($"---\n#{n.Id} {n.Title}\n{n.Content}");
        }
        sb.AppendLine("---");
        sb.AppendLine($"[问题]\n{question}");
        sb.AppendLine("\n[要求] 严格基于上述笔记回答。无相关信息就直说'无相关信息'。引用笔记时用 #id。");

        var req = new ChatRequest
        {
            Model = string.Empty,
            Messages = new[]
            {
                new ChatMessage { Role = "system", Content = "你是基于给定上下文的问答助手。严守事实,不编造,不超出上下文范围。" },
                new ChatMessage { Role = "user", Content = sb.ToString() }
            },
            Stream = false,
            Temperature = 0.2,
            MaxTokens = 2048,
            EnableThinking = false
        };
        var resp = await _provider.ChatAsync(req, ct);
        return (resp.Content ?? "").Trim();
    }
}
