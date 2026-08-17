// Copyright (c) AeroCode V3.0
// SummarizeNoteSkill — note summarization skill (Hermes-style productivity).
using AeroCode.Skills.Registry;

namespace AeroCode.Skills.Bundled.Productivity;

/// <summary>
/// Summarize a long note into a concise summary (Hermes-style productivity skill).
/// </summary>
public sealed class SummarizeNoteSkill : ISkill
{
    public string Id => "productivity/summarize-note";
    public string Name => "Summarize Note";
    public string Description => "Summarize a long note into a concise summary.";
    public string Category => "productivity";
    public string Author => "AeroCode V3.0";
    public string Version => "1.0.0";
    public IReadOnlyList<string> Tags => new[] { "productivity", "notes", "summarization" };

    public string GetSystemPrompt() => """
        # Summarize Note
        Given a long note, produce:
        1. A one-line title (max 80 chars)
        2. A 3-sentence summary
        3. A bullet list of key points (max 5)
        4. Optional: action items / next steps
        """;

    public bool IsAvailable() => true;

    public Task<SkillResult> ExecuteAsync(SkillInput input, SkillContext ctx, CancellationToken ct = default)
    {
        var content = input.Args.TryGetValue("content", out var c) ? c as string : null;
        if (string.IsNullOrWhiteSpace(content))
            return Task.FromResult(new SkillResult { Text = "Error: 'content' argument is required", Success = false });

        var maxLen = input.Args.TryGetValue("max_length", out var m) && m is int ml ? ml : 500;
        var wordCount = content.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

        var prompt = $"""
            Summarize the following note ({wordCount} words) into at most {maxLen} characters:

            ---
            {content}
            ---

            Output format:
            ## Title
            <one-line title, max 80 chars>

            ## Summary
            <3 sentences>

            ## Key Points
            - <point 1>
            - <point 2>
            - <point 3>

            ## Action Items
            - <action 1> (if any)
            """;

        return Task.FromResult(new SkillResult
        {
            Text = prompt,
            Success = true,
            Data = new { WordCount = wordCount, MaxLength = maxLen },
            NextActions = new[] { "call-llm", "save-summary" },
        });
    }
}
