// Copyright (c) AeroCode
// SoulPresets — 内置长系统提示词预设（随双端打包的嵌入资源）。
// 预设内容为完整的参考级系统提示词（约 39 万字符），一键安装到 AppData/SOUL.md
// 后由 InstructionLoader 全文注入 system 消息——验证 2 万字以上长提示词的真实承载。
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace AeroCode.App.Services;

/// <summary>内置 SOUL/长系统提示词预设的读取与安装（桌面/Android 共享同一嵌入资源）。</summary>
public static class SoulPresets
{
    /// <summary>内置预设资源名后缀（嵌入资源全名 = 根命名空间.目录路径.文件名）。</summary>
    public const string BuiltinResourceSuffix = "reference-system-prompt.md";

    /// <summary>内置预设在设置 UI 中的展示名。</summary>
    public const string BuiltinDisplayName = "参考级长系统提示词（内置预设）";

    private static string? _cachedContent;

    /// <summary>
    /// 读取内置预设全文（嵌入资源，随包分发）。资源缺失返回 null——不伪造内容。
    /// </summary>
    public static string? GetBuiltinContent()
    {
        if (_cachedContent is not null)
        {
            return _cachedContent;
        }

        var asm = typeof(SoulPresets).Assembly;
        var fullName = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(BuiltinResourceSuffix, StringComparison.OrdinalIgnoreCase));
        if (fullName is null)
        {
            return null;
        }

        using var stream = asm.GetManifestResourceStream(fullName);
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream, Encoding.UTF8);
        _cachedContent = reader.ReadToEnd();
        return _cachedContent;
    }

    /// <summary>内置预设字符数（资源缺失为 0；设置 UI 展示用）。</summary>
    public static int BuiltinCharCount => GetBuiltinContent()?.Length ?? 0;

    /// <summary>
    /// 把内置预设安装（覆盖写入）为指定 SOUL 文件，UTF-8 无 BOM。
    /// 返回写入字符数；预设缺失抛 InvalidOperationException（由调用方如实展示）。
    /// </summary>
    public static int InstallBuiltin(string soulPath)
    {
        var content = GetBuiltinContent()
            ?? throw new InvalidOperationException("内置预设资源缺失（嵌入资源未随包分发？）");
        var dir = Path.GetDirectoryName(soulPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(soulPath, content, new UTF8Encoding(false));
        return content.Length;
    }
}
