// Copyright (c) AeroCode
// ContextCuratorLlmAdapter（R1 β 可选桥）：把一次真实的 LLM 摘要调用适配为
// Compactor / ContextCurator 共用的 Func<string, Task<string>> 形态；
// 输出统一过敏感形态过滤（密钥/token 形态字符串打码）并封顶长度，
// 保证摘要文本可安全进入外置状态块（安全验收：输出不夹带敏感原文）。
using System.Text.RegularExpressions;

namespace AeroCode.Harness.Curation;

/// <summary>
/// 敏感形态字符串过滤（公共口径：Moa 策展器与本适配器共用同一份实现）。
/// 命中常见凭据形态即整体打码为 [REDACTED]；只做形态匹配，不做任何还原。
/// </summary>
public static class SensitiveTextScrubber
{
    private static readonly Regex[] Patterns =
    {
        // OpenAI 形态 sk- 前缀密钥（含 sk-proj- 等）。
        new(@"\bsk-[A-Za-z0-9_\-]{12,}\b", RegexOptions.Compiled),
        // Bearer / Authorization 头内联凭据。
        new(@"\bBearer\s+[A-Za-z0-9._\-]{8,}", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        // GitHub / AWS 形态。
        new(@"\bgh[pousr]_[A-Za-z0-9]{16,}\b", RegexOptions.Compiled),
        new(@"\bAKIA[0-9A-Z]{16}\b", RegexOptions.Compiled),
        // 通用 key=value 形态（api_key/token/secret/password/pwd）。
        new(@"\b(api[_\-]?key|apikey|access[_\-]?token|secret|password|pwd|token)\s*[:=]\s*[""']?[^\s;，,）)]{6,}",
            RegexOptions.Compiled | RegexOptions.IgnoreCase),
        // 长 hex/base64 连续串（≥32 位，凭据常见形态）。
        new(@"\b[A-Fa-f0-9]{32,}\b", RegexOptions.Compiled),
    };

    /// <summary>打码 <paramref name="text"/> 中的敏感形态字符串；null/空原样返回（空串）。</summary>
    public static string Scrub(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var result = text;
        foreach (var pattern in Patterns)
        {
            result = pattern.Replace(result, "[REDACTED]");
        }

        return result;
    }
}

/// <summary>
/// LLM 摘要适配器：包装一次真实的摘要模型调用（如 provider 的一次性补全），
/// 产出 <c>Func&lt;string, Task&lt;string&gt;&gt;</c> 形态摘要函数——与 Harness Compactor
/// 的 summarizer 参数、Moa CurationOptions.Summarizer 同形，可直接注入。
/// 失败语义：任何异常都吞掉并返回空串（策展器按空摘要降级）；异常消息不透传，
/// 防止端点/凭据信息经异常链路泄漏。
/// </summary>
public sealed class ContextCuratorLlmAdapter
{
    private readonly Func<string, CancellationToken, Task<string>>? _llmCall;
    private readonly int _maxOutputChars;

    /// <param name="llmCall">真实摘要调用（输入待摘要文本，输出摘要）；null = 无 LLM 能力。</param>
    /// <param name="maxOutputChars">摘要输出字符上限（默认 400，与外置状态块预算同量级）。</param>
    public ContextCuratorLlmAdapter(
        Func<string, CancellationToken, Task<string>>? llmCall,
        int maxOutputChars = 400)
    {
        _llmCall = llmCall;
        _maxOutputChars = Math.Max(maxOutputChars, 1);
    }

    /// <summary>是否具备真实 LLM 摘要能力（false 时 Summarize 恒返回空串，策展器确定性降级）。</summary>
    public bool IsAvailable => _llmCall is not null;

    /// <summary>Compactor / ContextCurator 注入形态的摘要函数（脱敏 + 封顶后输出）。</summary>
    public Func<string, Task<string>> Summarize => SummarizeAsync;

    /// <summary>摘要执行：调 LLM → 敏感形态过滤 → 长度封顶；任何失败返回空串。</summary>
    public async Task<string> SummarizeAsync(string conversationText)
    {
        if (_llmCall is null || string.IsNullOrWhiteSpace(conversationText))
        {
            return string.Empty;
        }

        try
        {
            var raw = await _llmCall(conversationText, CancellationToken.None).ConfigureAwait(false);
            var scrubbed = SensitiveTextScrubber.Scrub(raw);
            return scrubbed.Length <= _maxOutputChars ? scrubbed : scrubbed[.._maxOutputChars];
        }
        catch (Exception)
        {
            // 摘要失败不打断策展主链；不透传异常（可能含端点/凭据信息）。
            return string.Empty;
        }
    }
}
