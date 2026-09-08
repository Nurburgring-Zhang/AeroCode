// Copyright (c) AeroCode
// MessageAttachment — 用户消息附带的图片附件（R4-γ 最小可用：选择/粘贴/消息记录/发送描述）。
using System.Text.Json.Serialization;

namespace AeroAgent.Conversation.Models;

/// <summary>
/// 消息附件（图片）。最小可用范围：文件名/MIME/大小/可选预览占位。
/// 完整图片字节不入消息持久化；PreviewBytes 仅前 4KB 占位（R4-γ 最小实现不渲染缩略图，
/// 附件以文本描述注入模型上下文，非多模态上传）。
/// </summary>
public sealed record MessageAttachment
{
    public MessageAttachment(string fileName, string mimeType, long sizeBytes, byte[]? previewBytes = null)
    {
        FileName = fileName;
        MimeType = mimeType;
        SizeBytes = sizeBytes;
        PreviewBytes = previewBytes;
    }

    /// <summary>原始文件名（如 screenshot.png）。</summary>
    public string FileName { get; init; }

    /// <summary>MIME 类型（如 image/png）。</summary>
    public string MimeType { get; init; }

    /// <summary>原始文件大小（字节）。</summary>
    public long SizeBytes { get; init; }

    /// <summary>可选缩略预览（JPEG/PNG 字节，上限约 64KB）。</summary>
    [JsonIgnore]
    public byte[]? PreviewBytes { get; init; }

    /// <summary>人类可读大小描述。</summary>
    public string DisplaySize => SizeBytes switch
    {
        < 1024 => $"{SizeBytes}B",
        < 1024 * 1024 => $"{SizeBytes / 1024.0:F1}KB",
        _ => $"{SizeBytes / (1024.0 * 1024.0):F1}MB",
    };

    /// <summary>生成发送时嵌入用户消息的描述文本。</summary>
    public string ToDescription() => $"[Attached image: {FileName} ({DisplaySize}, {MimeType})]";
}
