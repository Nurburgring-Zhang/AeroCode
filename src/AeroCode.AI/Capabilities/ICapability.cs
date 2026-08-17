using System.Threading;
using System.Threading.Tasks;

namespace AeroCode.AI.Capabilities;

/// <summary>
/// AI 能力抽象。所有内置能力实现此接口:摘要 / 翻译 / 智能打标签 / 语义搜索 / 写作助手 / 自动问答。
/// </summary>
public interface ICapability
{
    string Name { get; }
    string Description { get; }
}

/// <summary>
/// 文本输入型能力(摘要、翻译、智能标签、写作)。
/// </summary>
public interface ITextCapability : ICapability
{
    Task<string> ExecuteAsync(string input, string? systemHint = null, CancellationToken ct = default);
}

/// <summary>
/// 双语文本能力(翻译)。</summary>
public interface ITranslationCapability : ICapability
{
    Task<string> TranslateAsync(string text, string targetLanguage, string? sourceLanguage = null, CancellationToken ct = default);
}
