// Copyright (c) AeroCode
// OutputTruncator 判定/改写 + FileToolOutputSink 真实落盘验证。
using System;
using System.IO;
using System.Linq;
using AeroAgent.Moa.Tools;
using Xunit;

namespace AeroCode.Tests.MoaTests;

/// <summary>
/// 截断阈值按常量钉死（50K 字符 / 2000 行 / Head 2000 行），
/// 截断必须把完整输出真实落盘且引用路径可回读原文——禁止只断言不落盘。
/// </summary>
public sealed class OutputTruncatorTests : IDisposable
{
    private readonly string _dir;
    private readonly FileToolOutputSink _sink;

    public OutputTruncatorTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"outtrunc_{Guid.NewGuid():N}");
        _sink = new FileToolOutputSink(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // 临时目录清理失败不影响测试结论
        }
    }

    private static string Lines(int count, string prefix = "line") =>
        string.Join("\n", Enumerable.Range(1, count).Select(i => $"{prefix}-{i}"));

    [Fact]
    public void ShortOutput_NoTruncation()
    {
        var output = Lines(100);

        Assert.False(OutputTruncator.NeedsTruncation(output));
        Assert.Null(OutputTruncator.Truncate("tool", output, _sink));
    }

    [Fact]
    public void CharBoundary_Exactly50K_NotTruncated_OneMore_Truncated()
    {
        Assert.False(OutputTruncator.NeedsTruncation(new string('a', OutputTruncator.MaxChars)));
        Assert.True(OutputTruncator.NeedsTruncation(new string('a', OutputTruncator.MaxChars + 1)));
    }

    [Fact]
    public void LineBoundary_Exactly2000_NotTruncated_OneMore_Truncated()
    {
        Assert.False(OutputTruncator.NeedsTruncation(Lines(OutputTruncator.MaxLines)));
        Assert.True(OutputTruncator.NeedsTruncation(Lines(OutputTruncator.MaxLines + 1)));
    }

    [Fact]
    public void Truncate_LargeOutput_SpillsFullContentToDiskAndReturnsReference()
    {
        var original = Lines(3000);

        var truncated = OutputTruncator.Truncate("grep_search", original, _sink);

        Assert.NotNull(truncated);
        Assert.Contains("showing first 2000 of 3000 lines", truncated);
        var marker = "Full output saved to: ";
        var idx = truncated.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(idx >= 0, "截断输出必须携带落盘路径引用");
        var path = truncated[(idx + marker.Length)..].TrimEnd('\r', '\n');
        Assert.True(File.Exists(path), $"落盘文件应存在：{path}");
        // 落盘内容 = 未截断的完整原文（回读比对，不许伪造）
        Assert.Equal(original, File.ReadAllText(path));
    }

    [Fact]
    public void Truncate_KeepsFirstHeadLinesInOrder()
    {
        var original = Lines(2500);

        var truncated = OutputTruncator.Truncate("tool", original, _sink)!;
        var outLines = truncated.Replace("\r\n", "\n").Split('\n');

        Assert.Equal("line-1", outLines[0]);
        Assert.Equal("line-2000", outLines[1999]);
        Assert.StartsWith("[aerocode] output truncated", outLines[2000]);
        Assert.DoesNotContain("line-2001", truncated);
    }

    [Fact]
    public void FileToolOutputSink_WritesRealFile_WithSanitizedName()
    {
        var path = _sink.Write("grep.search/x", "payload 内容");

        Assert.True(File.Exists(path));
        Assert.Equal("payload 内容", File.ReadAllText(path));
        Assert.Contains("grep_search_x", Path.GetFileName(path));
        Assert.StartsWith(_dir, path);
    }
}
