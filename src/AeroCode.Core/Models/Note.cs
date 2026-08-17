using System;

namespace AeroCode.Core.Models;

/// <summary>
/// 一条笔记的核心实体。Markdown 内容存原文，HTML 渲染结果按需生成不持久化。
/// Tags 通过 NoteTag 关联表实现多对多。
/// </summary>
public class Note
{
    public long Id { get; set; }

    public long? NotebookId { get; set; }
    public Notebook? Notebook { get; set; }

    public string Title { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;

    public bool IsPinned { get; set; }

    public bool IsArchived { get; set; }

    public bool IsDeleted { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<NoteTag> NoteTags { get; set; } = new List<NoteTag>();

    public int WordCount => CountWords(Content);

    private static int CountWords(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        int count = 0;
        bool inLatin = false; // 连续英数序列中
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c) || char.IsPunctuation(c) || char.IsSymbol(c))
            {
                inLatin = false;
                continue;
            }
            if (IsCjk(c))
            {
                // 每个 CJK 字符独立计 1 word
                inLatin = false;
                count++;
                continue;
            }
            if (char.IsLetterOrDigit(c))
            {
                if (!inLatin)
                {
                    inLatin = true;
                    count++;
                }
            }
            else
            {
                inLatin = false;
            }
        }
        return count;
    }

    private static bool IsCjk(char c)
    {
        // CJK Unified Ideographs 范围
        return (c >= 0x4E00 && c <= 0x9FFF)
            || (c >= 0x3400 && c <= 0x4DBF)
            || (c >= 0xF900 && c <= 0xFAFF);
    }
}
