// Copyright (c) AeroCode
// ToolCallGuardrailValidator（R2 延后项 → 批次 C 安全切片交付）：GuardrailPipeline 工具段
// 独立验证器（R3 波次 γ builder-γ）。设计约束（IGuardrailValidator 契约）：
// 无 IO、纯裁决、可脱离管道单独实例化与单测；只产出 finding——MarkOnly 默认拦截语义
// 由 GuardrailPipeline 决定，本类不改变它（是否注册、是否翻转 Enforce 均归组合根/缝合）。
using System.Text.RegularExpressions;
using AeroCode.Harness.Curation;
using AeroAgent.Moa.Safety;

namespace AeroAgent.Moa.Guard;

/// <summary>
/// 工具调用段独立验证器（纯启发式 v0，检测维度如实标注为非穷尽）：
/// 1. 敏感形态：物化参数的序列化文本命中 canonical 词表（Harness SensitiveTextScrubber，
///    S-L1 收敛唯一词表）→ Warning 标记；finding 文本只含参数名与工具名，绝不夹带敏感原文；
/// 2. 不可逆破坏命令（run_shell 的 command 参数命中 v0 高置信破坏形态：递归强删根/home/盘符、
///    Windows 递归强删、PowerShell 递归强删、磁盘格式化/分区、git 强推）→ Critical 阻断候选
///    （MarkOnly 默认下仍只标记；Enforce 是否真拦截由组合根翻转，本验证器无权决定）。
/// </summary>
public sealed class ToolCallGuardrailValidator : IGuardrailValidator
{
    /// <summary>验证器名（审计与日志）。</summary>
    public const string ValidatorName = "tool-call";

    /// <summary>finding 文本中命令摘录的截断长度（脱敏后仍封顶，防超长命令撑爆审计）。</summary>
    public const int MaxExcerptChars = 120;

    // v0 不可逆破坏命令形态（大小写不敏感；刻意保守：只钉高置信破坏形态，避免误报淹没真信号）。
    private static readonly Regex[] DestructiveCommandPatterns =
    {
        // 递归强删根/home/盘符：rm -rf /、rm -fr ~、rm -rf C:\（-r 与 -f 同 flag 合写，顺序不限）。
        new(@"\brm\s+-(?:[a-z]*r[a-z]*f|[a-z]*f[a-z]*r)[a-z]*\s+[""']?(/|~|\w:)(\s|$|\\|/)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase),
        // 递归强删（-r 与 -f 分写）：rm -r -f /、rm -f -r ~。
        new(@"\brm\s+-(?:[a-z]*r[a-z]*f|[a-z]*f[a-z]*r)[a-z]*\s+-[a-z]+\s+[""']?(/|~|\w:)(\s|$|\\|/)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase),
        // Windows 递归强删：rd /s /q、del /s /q、rmdir /s /q。
        new(@"\b(?:rd|rmdir|del)\s+/s\s+/q\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        // PowerShell 递归强删。
        new(@"\bRemove-Item\b[^;|&\r\n]*-Recurse[^;|&\r\n]*-Force", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        // 磁盘格式化 / 分区工具。
        new(@"\b(?:format|mkfs(?:\.\w+)?|diskpart)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        // git 强推（覆盖远端历史）。
        new(@"\bgit\s+push\s+[^;|&\r\n]*(?:--force(?:-with-lease)?\b|\s-f\b)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase),
    };

    /// <inheritdoc />
    public string Name => ValidatorName;

    /// <inheritdoc />
    public GuardrailVerdict Validate(GuardrailRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); // 取消不吞：向上传播（安全硬门 #2）

        if (request.Stage != GuardrailStage.ToolCall)
        {
            // 只处理工具段负载；误投其他段时无发现（不越段裁决）。
            return new GuardrailVerdict { Stage = request.Stage };
        }

        var findings = new List<GuardrailFinding>();

        // 维度 1：敏感形态（经 AdvisorArgsSanitizer 复用 canonical 词表；命中的参数名入 finding，值绝不入文）。
        var sanitized = AdvisorArgsSanitizer.Sanitize(request.ToolArguments);
        if (sanitized.Modified)
        {
            findings.Add(new GuardrailFinding(
                ValidatorName,
                request.Stage,
                GuardrailSeverity.Warning,
                "tool-call.sensitive-args",
                SensitiveTextScrubber.Scrub(
                    $"tool '{request.ToolName}' arguments contain sensitive-looking material; " +
                    $"redacted parameter(s): {string.Join(", ", sanitized.ModifiedParameterNames)}"),
                Blocking: false));
        }

        // 维度 2：不可逆破坏命令（仅 command 形态的字符串参数参与；其他工具跳过）。
        if (request.ToolArguments is not null &&
            request.ToolArguments.TryGetValue("command", out var command) &&
            command is string commandText)
        {
            foreach (var pattern in DestructiveCommandPatterns)
            {
                var match = pattern.Match(commandText);
                if (!match.Success)
                {
                    continue;
                }

                var excerpt = SensitiveTextScrubber.Scrub(match.Value); // 脱敏责任在验证器（契约 #4）
                if (excerpt.Length > MaxExcerptChars)
                {
                    excerpt = excerpt[..MaxExcerptChars];
                }

                findings.Add(new GuardrailFinding(
                    ValidatorName,
                    request.Stage,
                    GuardrailSeverity.Critical,
                    "tool-call.destructive-command",
                    $"tool '{request.ToolName}' command matches destructive pattern: {excerpt}",
                    Blocking: true));
                break; // 一条命令只报一条破坏形态 finding（同类去重，避免噪音）
            }
        }

        return new GuardrailVerdict { Stage = request.Stage, Findings = findings };
    }
}
