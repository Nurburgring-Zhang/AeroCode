// Copyright (c) AeroCode
// LeakScanner — ACS 泄漏扫描：密钥/凭据模式检测。
// 扫描文本中的常见密钥/凭据模式（sk-/ghp_/gho_/AKIA/私钥块等），
// 命中即报 finding（含模式类型与位置，不回显完整密钥本体）。
using System.Text.RegularExpressions;

namespace AeroCode.Harness.Acs;

/// <summary>泄漏 finding。</summary>
public sealed record LeakFinding(string PatternName, int Index, string Excerpt);

/// <summary>
/// 密钥/凭据泄漏扫描器（只读，纯正则）。
/// </summary>
public sealed class LeakScanner
{
    /// <summary>扫描模式表（名称 → 正则）。模式只报位置与类型，不回显完整密钥。</summary>
    public static readonly IReadOnlyList<(string Name, Regex Rx)> Patterns = new List<(string, Regex)>
    {
        ("openai-key", new Regex(@"\bsk-[A-Za-z0-9]{20,}", RegexOptions.Compiled)),
        ("github-pat", new Regex(@"\bghp_[A-Za-z0-9]{30,}", RegexOptions.Compiled)),
        ("github-oauth", new Regex(@"\bgho_[A-Za-z0-9]{30,}", RegexOptions.Compiled)),
        ("github-app-token", new Regex(@"\b(?:ghu|ghs)_[A-Za-z0-9]{30,}", RegexOptions.Compiled)),
        ("aws-access-key", new Regex(@"\bAKIA[0-9A-Z]{16}\b", RegexOptions.Compiled)),
        ("private-key-block", new Regex(@"-----BEGIN (?:RSA |EC |DSA |OPENSSH )?PRIVATE KEY-----", RegexOptions.Compiled)),
        ("jwt-token", new Regex(@"\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\b", RegexOptions.Compiled)),
        ("generic-api-key", new Regex(@"\b(?:api[_-]?key|apikey)\s*[:=]\s*['""]?[A-Za-z0-9_\-]{16,}", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        ("bearer-token", new Regex(@"\bbearer\s+[A-Za-z0-9_\-\.]{20,}", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
    }.AsReadOnly();

    /// <summary>
    /// 扫描文本，返回全部泄漏 finding（截断摘录，不回显完整密钥）。
    /// </summary>
    public IReadOnlyList<LeakFinding> Scan(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return Array.Empty<LeakFinding>();
        }

        var findings = new List<LeakFinding>();
        foreach (var (name, rx) in Patterns)
        {
            foreach (Match m in rx.Matches(text))
            {
                // 摘录只取前 8 字符 + 省略号（不回显完整密钥本体）
                var excerpt = m.Value.Length > 8 ? m.Value[..8] + "…" : m.Value;
                findings.Add(new LeakFinding(name, m.Index, excerpt));
            }
        }

        return findings;
    }

    /// <summary>是否干净（无泄漏）。</summary>
    public bool IsClean(string? text) => Scan(text).Count == 0;
}
