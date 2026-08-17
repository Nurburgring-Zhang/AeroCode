// Copyright (c) AeroCode V3.0
// AutoTagNoteSkill — auto-tag a note (Hermes-style productivity).
using AeroCode.Skills.Registry;

namespace AeroCode.Skills.Bundled.Productivity;

/// <summary>
/// Auto-tag a note based on its content (Hermes-style productivity skill).
/// Returns a list of suggested tags.
/// </summary>
public sealed class AutoTagNoteSkill : ISkill
{
    public string Id => "productivity/auto-tag-note";
    public string Name => "Auto-Tag Note";
    public string Description => "Suggest tags for a note based on content.";
    public string Category => "productivity";
    public string Author => "AeroCode V3.0";
    public string Version => "1.0.0";
    public IReadOnlyList<string> Tags => new[] { "productivity", "notes", "tagging" };

    public string GetSystemPrompt() => """
        # Auto-Tag Note
        Given a note's content, suggest 3-5 tags.
        Tags should:
        - Be lowercase
        - Be single words (use hyphens for multi-word)
        - Be relevant to the content
        - Be reusable across notes
        Avoid overly generic tags like "note" or "text".
        """;

    public bool IsAvailable() => true;

    public Task<SkillResult> ExecuteAsync(SkillInput input, SkillContext ctx, CancellationToken ct = default)
    {
        var content = input.Args.TryGetValue("content", out var c) ? c as string : null;
        if (string.IsNullOrWhiteSpace(content))
            return Task.FromResult(new SkillResult { Text = "Error: 'content' argument is required", Success = false });

        var maxTags = input.Args.TryGetValue("max_tags", out var m) && m is int mt ? mt : 5;

        var prompt = $"""
            Suggest up to {maxTags} tags for the following note.
            Each tag should be a single lowercase word or hyphenated phrase.
            Output as a JSON array: ["tag1", "tag2", "tag3"]

            Note content:
            ---
            {content}
            ---
            """;

        return Task.FromResult(new SkillResult
        {
            Text = prompt,
            Success = true,
            Data = new { MaxTags = maxTags },
            NextActions = new[] { "call-llm", "parse-json", "apply-tags" },
        });
    }
}
