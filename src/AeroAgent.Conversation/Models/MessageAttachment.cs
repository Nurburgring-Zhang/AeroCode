// Copyright (c) AeroCode
// MessageAttachment — 用户消息附带的附件（R5.3 多类型 + 分块注入 + vision 上送）。
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json.Serialization;
using AeroCode.AI.Models;

namespace AeroAgent.Conversation.Models;

/// <summary>
/// 消息附件。支持任意类型文件；文本类文件在附加时抽取正文（可能截断），
/// 发送时经 <see cref="BuildInjection"/> 按总预算分块注入模型上下文。
/// 完整文件字节不入消息持久化；PreviewBytes 仅占位，TextContent 为注入用文本。
/// 超出上下文预算的附件如实标注为「已引用未注入」，不伪造已读全文。
/// </summary>
public sealed record MessageAttachment
{
    public MessageAttachment(
        string fileName,
        string mimeType,
        long sizeBytes,
        byte[]? previewBytes = null,
        string? textContent = null,
        bool contentTruncated = false)
    {
        FileName = fileName;
        MimeType = mimeType;
        SizeBytes = sizeBytes;
        PreviewBytes = previewBytes;
        TextContent = textContent;
        ContentTruncated = contentTruncated;
    }

    /// <summary>原始文件名（如 report.md）。</summary>
    public string FileName { get; init; }

    /// <summary>MIME 类型（如 text/markdown、image/png）。</summary>
    public string MimeType { get; init; }

    /// <summary>原始文件大小（字节）。</summary>
    public long SizeBytes { get; init; }

    /// <summary>附件源文件绝对路径（文件型附件；剪贴板型可为 null）。vision 上送时据此读取图像字节。</summary>
    [JsonIgnore]
    public string? SourcePath { get; init; }

    /// <summary>可选缩略预览（图片字节占位，上限约 4KB）。</summary>
    [JsonIgnore]
    public byte[]? PreviewBytes { get; init; }

    /// <summary>文本类文件抽取出的正文（可能已按单文件预算截断；二进制为 null）。</summary>
    [JsonIgnore]
    public string? TextContent { get; init; }

    /// <summary>TextContent 是否因单文件预算被截断。</summary>
    public bool ContentTruncated { get; init; }

    /// <summary>
    /// 文本类文件抽取正文是否失败（编码/锁等，review L3）——区别于"本就是二进制/图片"，
    /// 供 <see cref="ToInjectionBlock"/> 如实标注，避免把读取失败的文本误报为二进制。
    /// </summary>
    public bool ExtractionFailed { get; init; }

    /// <summary>是否抽到了可注入正文。</summary>
    [JsonIgnore]
    public bool HasText => !string.IsNullOrEmpty(TextContent);

    /// <summary>人类可读大小描述。</summary>
    public string DisplaySize => SizeBytes switch
    {
        < 1024 => $"{SizeBytes}B",
        < 1024 * 1024 => $"{SizeBytes / 1024.0:F1}KB",
        < 1024L * 1024 * 1024 => $"{SizeBytes / (1024.0 * 1024.0):F1}MB",
        _ => $"{SizeBytes / (1024.0 * 1024.0 * 1024.0):F2}GB",
    };

    /// <summary>单行描述（消息气泡/持久化摘要用）。</summary>
    public string ToDescription() => $"[Attached file: {FileName} ({DisplaySize}, {MimeType})]";

    /// <summary>
    /// 单个附件的注入块：文本类给出正文（截断时如实标注），二进制仅给出元信息。
    /// </summary>
    public string ToInjectionBlock()
    {
        if (!HasText)
        {
            // review L3：区分"文本读取失败"与"本就是二进制/图片"，如实标注不误导。
            return ExtractionFailed
                ? $"{ToDescription()}（文本读取失败，未注入正文，仅告知存在）"
                : $"{ToDescription()}（二进制/图片附件，未注入正文，仅告知存在）";
        }

        var sb = new StringBuilder();
        sb.Append($"[Attached file: {FileName} ({DisplaySize}, {MimeType})]\n```\n");
        sb.Append(TextContent);
        sb.Append("\n```");
        if (ContentTruncated)
        {
            sb.Append($"\n（该文件较大，仅注入开头部分，全文 {DisplaySize}）");
        }

        return sb.ToString();
    }

    /// <summary>
    /// 按总字符预算把一组附件拼成注入文本（分块注入）：
    /// 依次纳入附件正文，直到预算耗尽；其后仍有正文的附件降级为「已引用未注入」，
    /// 二进制附件始终只占一行元信息。保证不超预算、不伪造已注入全文。
    /// </summary>
    public static string BuildInjection(IReadOnlyList<MessageAttachment> attachments, int totalCharBudget)
    {
        if (attachments is null || attachments.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        var used = 0;
        var budgetExhausted = false;
        foreach (var a in attachments)
        {
            if (!a.HasText)
            {
                // 二进制/图片：只占一行元信息，几乎不耗预算。
                sb.Append(a.ToDescription()).Append('\n');
                continue;
            }

            if (budgetExhausted)
            {
                sb.Append(a.ToDescription()).Append("（上下文预算已满，已引用未注入正文）\n");
                continue;
            }

            var block = a.ToInjectionBlock();
            if (used + block.Length <= totalCharBudget)
            {
                sb.Append(block).Append('\n');
                used += block.Length;
            }
            else
            {
                // 放不下完整块：若剩余预算足够放一个有意义的片段则截断注入，否则降级引用。
                var remaining = totalCharBudget - used;
                var header = $"[Attached file: {a.FileName} ({a.DisplaySize}, {a.MimeType})]\n```\n";
                var footer = "\n```（预算截断，仅注入片段）";
                var minUseful = 200;
                if (remaining - header.Length - footer.Length >= minUseful && a.TextContent is not null)
                {
                    var take = remaining - header.Length - footer.Length;
                    take = System.Math.Min(take, a.TextContent.Length);
                    sb.Append(header).Append(a.TextContent, 0, take).Append(footer).Append('\n');
                    used = totalCharBudget;
                }
                else
                {
                    sb.Append(a.ToDescription()).Append("（上下文预算已满，已引用未注入正文）\n");
                }

                budgetExhausted = true;
            }
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// 从一组附件中构建 vision 图像内容（仅图片类、文件可达且在预算内）。
    /// 逐张读取源文件并 base64 编码；超出张数/单张大小预算、非图片、文件缺失或读取失败的
    /// 如实跳过（不伪造已上送）。供支持 vision 的 provider 以 content-parts 上送。
    /// </summary>
    public static IReadOnlyList<ImageContent> BuildVisionImages(
        IReadOnlyList<MessageAttachment> attachments,
        int maxImages = 8,
        long maxBytesPerImage = 8L * 1024 * 1024)
    {
        var result = new List<ImageContent>();
        if (attachments is null || attachments.Count == 0)
        {
            return result;
        }

        foreach (var a in attachments)
        {
            if (result.Count >= maxImages)
            {
                break;
            }

            if (string.IsNullOrEmpty(a.SourcePath) || !IsImageMime(a.MimeType))
            {
                continue;
            }

            try
            {
                var info = new FileInfo(a.SourcePath);
                if (!info.Exists || info.Length == 0 || info.Length > maxBytesPerImage)
                {
                    continue;
                }

                var bytes = File.ReadAllBytes(a.SourcePath);
                result.Add(new ImageContent
                {
                    Mime = a.MimeType,
                    DataBase64 = Convert.ToBase64String(bytes),
                });
            }
            catch
            {
                // 读取失败如实跳过该张，不阻塞其余图像与消息发送。
            }
        }

        return result;
    }

    /// <summary>MIME 是否为图像类型。</summary>
    public static bool IsImageMime(string? mime) =>
        !string.IsNullOrEmpty(mime) && mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
}
