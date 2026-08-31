// Copyright (c) AeroCode
// OutputTruncator — 工具大结果落盘（对标 opencode truncate.ts：超限 spill 到文件，模型拿引用不爆上下文）。
using System.Globalization;
using System.Text;

namespace AeroAgent.Moa.Tools;

/// <summary>工具输出的持久化汇（真实文件系统；测试注入临时目录实现）。</summary>
public interface IToolOutputSink
{
    /// <summary>保存完整输出，返回可引用的文件路径。</summary>
    string Write(string toolName, string fullOutput);
}

/// <summary>按日期分目录落盘到本地输出区（默认 %LOCALAPPDATA%/AeroCode/tool-outputs）。</summary>
public sealed class FileToolOutputSink : IToolOutputSink
{
    private readonly string _root;

    public FileToolOutputSink(string? root = null)
    {
        _root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AeroCode", "tool-outputs");
        Directory.CreateDirectory(_root);
    }

    public string Write(string toolName, string fullOutput)
    {
        var day = DateTime.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var dir = Path.Combine(_root, day);
        Directory.CreateDirectory(dir);
        var name = $"{DateTime.UtcNow:HHmmss_fff}_{Sanitize(toolName)}_{Guid.NewGuid():N}.txt";
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, fullOutput, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private static string Sanitize(string toolName)
    {
        var sb = new StringBuilder(toolName.Length);
        foreach (var c in toolName)
        {
            sb.Append(char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_');
        }

        return sb.ToString();
    }
}

/// <summary>
/// 截断判定与改写：超过 <see cref="MaxChars"/> 字符或 <see cref="MaxLines"/> 行的结果，
/// 头部保留 <see cref="HeadLines"/> 行 + 完整内容落盘路径引用，让模型按需取用。
/// 静态纯函数（判定与改写）与汇分离，便于机检断言。
/// </summary>
public static class OutputTruncator
{
    public const int MaxChars = 50_000;
    public const int MaxLines = 2_000;
    public const int HeadLines = 2_000;

    public static bool NeedsTruncation(string output) =>
        output.Length > MaxChars ||
        output.AsSpan().Count('\n') + (output.Length > 0 && !output.EndsWith('\n') ? 1 : 0) > MaxLines;

    /// <summary>需要截断时返回改写后的引用型输出；不需要时返回 null（调用方保留原文）。</summary>
    public static string? Truncate(string toolName, string output, IToolOutputSink sink)
    {
        if (!NeedsTruncation(output))
        {
            return null;
        }

        var lines = output.Split('\n');
        var head = lines.Take(HeadLines);
        var path = sink.Write(toolName, output);
        var sb = new StringBuilder();
        foreach (var line in head)
        {
            sb.AppendLine(line);
        }

        sb.AppendLine($"[aerocode] output truncated: showing first {HeadLines} of {lines.Length} lines " +
                      $"({output.Length:N0} chars). Full output saved to: {path}");
        return sb.ToString();
    }
}
