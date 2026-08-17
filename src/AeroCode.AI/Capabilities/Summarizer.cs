using AeroCode.AI.Providers;
using Microsoft.Extensions.Logging;

namespace AeroCode.AI.Capabilities;

/// <summary>
/// 摘要当前笔记。MaxTokens 较小,温度低。
/// </summary>
public sealed class Summarizer : CapabilityBase
{
    public override string Name => "summarize";
    public override string Description => "把一段文本压缩为简洁摘要,保留核心信息";

    protected override string SystemPrompt =>
        "你是一名专业编辑。请根据用户要求把文本压缩为简洁摘要,保留核心信息,去除冗余。" +
        "输出仅摘要正文,不要任何解释、标题、Markdown 装饰。";

    public Summarizer(IAiProvider provider, ILogger<Summarizer> logger) : base(provider, logger) { }

    protected override string BuildUserPrompt(string input, string? systemHint)
    {
        var hint = string.IsNullOrWhiteSpace(systemHint) ? "压缩到 3-5 句,100 字以内" : systemHint;
        return $"[要求] {hint}\n\n[原文]\n{input}";
    }

    protected override int MaxTokens => 512;
}
