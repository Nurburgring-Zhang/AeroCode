using System.Collections;
using System.Text.RegularExpressions;

namespace AeroCode.Eval;

/// <summary>
/// eval 专用网关设置。硬性约束：网关凭据只从 <c>AEROCODE_EVAL_*</c> 环境变量读取，
/// 不落盘、不进日志；值为空/缺失 → 返回 null（调用方进入 dry-run）。
/// </summary>
public sealed record EvalGatewaySettings
{
    public required Uri BaseUrl { get; init; }

    public required string ApiKey { get; init; }

    public static EvalGatewaySettings? FromEnvironment()
    {
        var key = Environment.GetEnvironmentVariable(EvalSecrets.GatewayKeyVariable);
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        var url = Environment.GetEnvironmentVariable(EvalSecrets.GatewayUrlVariable);
        var baseUrl = Uri.TryCreate(url, UriKind.Absolute, out var parsed) &&
                      (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps)
            ? parsed
            : new Uri("http://127.0.0.1:8910");
        return new EvalGatewaySettings { BaseUrl = baseUrl, ApiKey = key };
    }
}

/// <summary>
/// 凭据候选收集：只看 <c>AEROCODE_EVAL_*</c> 前缀中凭据类变量（*_KEY/*_TOKEN/*_SECRET/*_PASSWORD/*_CREDENTIAL）
/// 与 moa-gateway 官方约定的 <c>MOA_GATEWAY_KEY</c>。收集结果只喂给 <see cref="SensitiveScrubber"/>。
/// </summary>
public static class EvalSecrets
{
    public const string GatewayKeyVariable = "AEROCODE_EVAL_GATEWAY_KEY";
    public const string GatewayUrlVariable = "AEROCODE_EVAL_GATEWAY_URL";

    private static readonly string[] CredentialSuffixes =
    [
        "_KEY",
        "_TOKEN",
        "_SECRET",
        "_PASSWORD",
        "_CREDENTIAL",
        "_CREDENTIALS",
    ];

    /// <summary>从当前进程环境变量收集可能出现的凭据原文（用于统一脱敏）。</summary>
    public static IReadOnlyList<string> CollectCandidateSecrets()
    {
        var secrets = new List<string>();
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is not string name || entry.Value is not string value || value.Length == 0)
            {
                continue;
            }

            var isEvalCredential = name.StartsWith("AEROCODE_EVAL_", StringComparison.OrdinalIgnoreCase) &&
                                   IsCredentialName(name);
            var isMoaGatewayKey = string.Equals(name, "MOA_GATEWAY_KEY", StringComparison.OrdinalIgnoreCase);
            if (isEvalCredential || isMoaGatewayKey)
            {
                secrets.Add(value);
            }
        }

        return secrets;
    }

    private static bool IsCredentialName(string name)
    {
        foreach (var suffix in CredentialSuffixes)
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// 统一脱敏器：所有报告、日志、异常消息出口必须过一遍。
/// S-L1 词表收敛（R2 缝合 #20）：形态词表以 src 侧 <see cref="AeroCode.Harness.Curation.SensitiveTextScrubber"/>
/// 为 canonical（单一事实源），本类经直接委托复用同一份模式集（sk- / Bearer / GitHub / AWS /
/// key=value（api_key|apikey|access_token|secret|password|pwd|token）/ 长 hex-base64）；
/// eval 特有职责仅保留「已注册凭据原文的精确替换」（环境变量采集的凭据候选是 eval 管线自有概念）。
/// 收敛口径说明：原 eval 自带词表与 canonical 的差异（sk-{8,} vs sk-{12,}、credential 关键字）按
/// canonical 统一——eval 报告中的真实凭据由字面量精确替换兜底，形态匹配以两侧同源为准。
/// 脱敏为硬验收：网关密钥/凭据不得出现在报告、日志、异常消息。
/// </summary>
public sealed class SensitiveScrubber
{
    public const string RedactionMark = "[REDACTED]";

    private readonly IReadOnlyList<string> _literalSecrets;

    public SensitiveScrubber(IEnumerable<string?> literalSecrets)
    {
        _literalSecrets = literalSecrets
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>脱敏任意文本（null 安全）。先字面量精确替换，再过 canonical 形态词表。</summary>
    public string Scrub(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var result = text;
        foreach (var secret in _literalSecrets)
        {
            result = result.Replace(secret, RedactionMark, StringComparison.Ordinal);
        }

        // S-L1：形态词表单一事实源 = src 侧 canonical（eval 工程经 Moa 传递引用 Harness）。
        return AeroCode.Harness.Curation.SensitiveTextScrubber.Scrub(result);
    }

    /// <summary>异常消息出口统一脱敏（ToString 全量，含堆栈与内层异常）。</summary>
    public string ScrubException(Exception exception) => Scrub(exception.ToString());
}
