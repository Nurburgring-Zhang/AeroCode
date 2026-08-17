using System.ComponentModel;
using ModelContextProtocol.Server;

namespace AeroCode.Mcp.Prompts;

/// <summary>
/// MCP Prompts: 预制 prompt 模板,客户端可调用快速发起任务。
/// </summary>
[McpServerPromptType]
public sealed class NotePrompts
{
    [McpServerPrompt(Name = "summarize_note"),
     Description("把指定笔记压缩为摘要")]
    public string SummarizeNote([Description("笔记 ID")] long note_id)
    {
        return $"请阅读笔记 #{note_id} 的完整内容,然后用 3-5 句(100 字内)输出摘要。仅输出摘要正文,不要标题或装饰。";
    }

    [McpServerPrompt(Name = "expand_note"),
     Description("把指定笔记扩写为更详细的版本")]
    public string ExpandNote([Description("笔记 ID")] long note_id)
    {
        return $"请阅读笔记 #{note_id} 的完整内容,然后扩写为更详细、更有条理的版本。保留原意,补充论证/例子。Markdown 格式。";
    }

    [McpServerPrompt(Name = "auto_tag_note"),
     Description("为指定笔记生成 1-5 个标签")]
    public string AutoTagNote([Description("笔记 ID")] long note_id)
    {
        return $"请阅读笔记 #{note_id} 的完整内容,提取最关键的 1-5 个标签(每个 2-6 字)。仅输出 JSON 数组,例如: [\"AI\", \"笔记\"]";
    }

    [McpServerPrompt(Name = "translate_note"),
     Description("把指定笔记翻译成目标语言")]
    public string TranslateNote(
        [Description("笔记 ID")] long note_id,
        [Description("目标语言,如 English, 日本語, 简体中文")] string targetLanguage)
    {
        return $"请把笔记 #{note_id} 的完整内容翻译为 {targetLanguage}。保持原意、风格、术语。仅输出翻译结果。";
    }

    [McpServerPrompt(Name = "answer_from_notes"),
     Description("基于多条笔记回答用户问题")]
    public string AnswerFromNotes(
        [Description("用户问题")] string question,
        [Description("候选笔记 ID 列表,如 [1,2,3]")] long[] note_ids)
    {
        var ids = string.Join(", ", note_ids ?? System.Array.Empty<long>());
        return $"用户问题: {question}\n\n请阅读笔记 #{ids} 的内容,严格基于这些笔记回答。引用用 #id 格式。无相关信息直说'无相关信息'。";
    }
}
