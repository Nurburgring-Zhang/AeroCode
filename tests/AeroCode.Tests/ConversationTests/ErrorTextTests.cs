using AeroAgent.Conversation.Models;
using Xunit;

namespace AeroCode.Tests.ConversationTests;

/// <summary>错误文本规范化：截断边界行为回归。</summary>
public sealed class ErrorTextTests
{
    [Fact]
    public void Truncate_Null_ReturnsNull()
    {
        Assert.Null(ErrorText.Truncate(null));
    }

    [Fact]
    public void Truncate_ShortText_Unchanged()
    {
        Assert.Equal("模型 404", ErrorText.Truncate("模型 404"));
    }

    [Fact]
    public void Truncate_ExactlyAtLimit_Unchanged()
    {
        var text = new string('x', ErrorText.MaxLength);
        Assert.Equal(text, ErrorText.Truncate(text));
    }

    [Fact]
    public void Truncate_OverLimit_KeepsPrefixAndMarksTruncation()
    {
        var text = new string('y', ErrorText.MaxLength + 500);

        var truncated = ErrorText.Truncate(text);

        Assert.NotNull(truncated);
        Assert.StartsWith(new string('y', ErrorText.MaxLength), truncated);
        Assert.EndsWith("…（已截断）", truncated);
        Assert.Equal(ErrorText.MaxLength + "…（已截断）".Length, truncated!.Length);
    }
}
