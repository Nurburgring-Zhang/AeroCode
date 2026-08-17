// Copyright (c) AeroCode V3.0
// NotePrompts (MCP) tests — verifies the prompt templates.
using AeroCode.Mcp.Prompts;
using Xunit;

namespace AeroCode.Tests.McpTests;

public class NotePromptsTests
{
    [Fact]
    public void SummarizeNote_ContainsNoteId()
    {
        var s = new NotePrompts().SummarizeNote(42);
        Assert.Contains("42", s);
        Assert.Contains("摘要", s);
    }

    [Fact]
    public void ExpandNote_ContainsNoteId()
    {
        var s = new NotePrompts().ExpandNote(7);
        Assert.Contains("7", s);
        Assert.Contains("扩写", s);
    }

    [Fact]
    public void AutoTagNote_RequestsJsonArray()
    {
        var s = new NotePrompts().AutoTagNote(99);
        Assert.Contains("99", s);
        Assert.Contains("JSON", s);
    }

    [Fact]
    public void TranslateNote_ContainsTargetLanguage()
    {
        var s = new NotePrompts().TranslateNote(5, "English");
        Assert.Contains("5", s);
        Assert.Contains("English", s);
    }

    [Fact]
    public void AnswerFromNotes_ListsNoteIds()
    {
        var s = new NotePrompts().AnswerFromNotes("什么是 X?", new long[] { 1, 2, 3 });
        Assert.Contains("什么是 X?", s);
        Assert.Contains("1, 2, 3", s);
    }

    [Fact]
    public void AnswerFromNotes_NullIds_HandledGracefully()
    {
        var s = new NotePrompts().AnswerFromNotes("Q", null!);
        Assert.Contains("Q", s);
    }
}
