using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AeroAgent.Autonomy.Common;
using AeroAgent.Autonomy.Llm;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AeroAgent.Autonomy.Steelman;

/// <summary>
/// 执行前双向钢人论证协议。对任务同时构造"支持最强论证"与"反对最强论证"，
/// 产出重述/正/反/分歧/关键问题五要素；两种模式：
/// <see cref="SteelmanMode.Interactive"/>（抛出关键问题等待应答后继续）与
/// <see cref="SteelmanMode.AutoApprove"/>（记录假设与理由后继续）。
/// 有 LLM 时真实调用生成五要素；无 LLM 时用结构化模板从任务文本真实提取要素填充，
/// 并记录 [DEGRADED]——绝不产出空串占位。
/// </summary>
public sealed class SteelmanProtocol
{
    private readonly AutonomyLlmClient _llm;
    private readonly ILogger<SteelmanProtocol> _logger;

    public SteelmanProtocol(AutonomyLlmClient llm, ILogger<SteelmanProtocol>? logger = null)
    {
        _llm = llm ?? throw new ArgumentNullException(nameof(llm));
        _logger = logger ?? NullLogger<SteelmanProtocol>.Instance;
    }

    /// <summary>
    /// 运行钢人论证。空任务文本抛 <see cref="ArgumentException"/>。
    /// </summary>
    /// <param name="taskText">任务文本。</param>
    /// <param name="mode">运行模式。</param>
    /// <param name="responder">Interactive 模式的应答方；null 时如实降级 AutoApprove。</param>
    /// <param name="ct">取消令牌。</param>
    public async Task<SteelmanRecord> RunAsync(
        string taskText,
        SteelmanMode mode,
        ISteelmanResponder? responder,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(taskText))
        {
            throw new ArgumentException("任务文本不能为空。", nameof(taskText));
        }

        var degradedToAuto = false;
        if (mode == SteelmanMode.Interactive && responder is null)
        {
            _logger.LogWarning("[DEGRADED] Interactive 模式未提供应答方，钢人论证降级为 AutoApprove。");
            mode = SteelmanMode.AutoApprove;
            degradedToAuto = true;
        }

        var fields = await GenerateFieldsAsync(taskText, ct);

        string? answer = null;
        if (mode == SteelmanMode.Interactive && responder is not null)
        {
            answer = await responder.AnswerAsync(taskText, fields.OneKeyQuestion, ct);
        }

        var assumptions = mode == SteelmanMode.AutoApprove
            ? BuildAssumptions(taskText)
            : Array.Empty<string>();

        return new SteelmanRecord
        {
            Restatement = fields.Restatement,
            ProArgument = fields.ProArgument,
            ConArgument = fields.ConArgument,
            Divergence = fields.Divergence,
            OneKeyQuestion = fields.OneKeyQuestion,
            KeyQuestionAnswer = string.IsNullOrWhiteSpace(answer) ? null : answer.Trim(),
            Mode = mode,
            DegradedToAutoApprove = degradedToAuto,
            Assumptions = assumptions,
            Source = fields.Source,
        };
    }

    private async Task<SteelmanFields> GenerateFieldsAsync(string taskText, CancellationToken ct)
    {
        if (_llm.IsAvailable)
        {
            var completion = await _llm.CompleteAsync(
                BuildSteelmanSystemPrompt(), taskText, temperature: 0.3, ct);
            if (completion is not null)
            {
                var parsed = TryParseLlmFields(completion.Content);
                if (parsed is not null)
                {
                    return parsed with { Source = AnalysisSource.Llm };
                }

                _logger.LogWarning("[DEGRADED] LLM 钢人输出不可解析，退回结构化模板。");
            }
        }
        else
        {
            _logger.LogWarning("[DEGRADED] 无已配置 LLM provider，钢人论证使用结构化模板 + 启发式。");
        }

        return BuildHeuristicFields(taskText);
    }

    internal static string BuildSteelmanSystemPrompt()
    {
        var sb = new StringBuilder();
        sb.AppendLine("你是严谨的钢人论证器。对用户任务做执行前双向论证，只输出 JSON，不要输出其他文本：");
        sb.AppendLine("{\"restatement\":\"对任务的最完整重述\",");
        sb.AppendLine("\"pro\":\"支持执行的最强论证\",");
        sb.AppendLine("\"con\":\"反对/质疑执行的最强论证\",");
        sb.AppendLine("\"divergence\":\"真正分歧点与最可能改变结论的关键变量\",");
        sb.AppendLine("\"key_question\":\"只问一个最关键问题\"}");
        sb.AppendLine("要求：五个字段都非空；con 必须是真实有力的质疑，不许稻草人；key_question 只允许一个问题。");
        return sb.ToString();
    }

    internal static SteelmanFields? TryParseLlmFields(string llmOutput)
    {
        if (string.IsNullOrWhiteSpace(llmOutput))
        {
            return null;
        }

        var text = llmOutput.Trim();
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(text[start..(end + 1)]);
            var root = doc.RootElement;

            var restatement = GetString(root, "restatement");
            var pro = GetString(root, "pro");
            var con = GetString(root, "con");
            var divergence = GetString(root, "divergence");
            var keyQuestion = GetString(root, "key_question");

            if (restatement is null || pro is null || con is null || divergence is null || keyQuestion is null)
            {
                return null;
            }

            return new SteelmanFields(restatement, pro, con, divergence, keyQuestion, AnalysisSource.Heuristic);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? GetString(JsonElement root, string name)
    {
        if (root.TryGetProperty(name, out var el)
            && el.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(el.GetString()))
        {
            return el.GetString()!.Trim();
        }

        return null;
    }

    /// <summary>
    /// AutoApprove 模式的假设清单：把启发式检测到的缺失维度逐条转为"假设 + 理由"，
    /// 外加主体字面含义与歧义处理两条通用假设。每条都是真实推导，非空话。
    /// </summary>
    internal static IReadOnlyList<string> BuildAssumptions(string taskText)
    {
        var assumptions = new List<string>();
        var missing = DetectMissingDimensions(taskText);

        foreach (var dimension in missing)
        {
            assumptions.Add(dimension switch
            {
                "验收标准" => "假设验收标准为：产出与任务目标直接对应、可检验的成果（理由：原文未写明验收标准，取最小可验证默认）。",
                "范围约束" => "假设任务范围仅限任务原文明确提及的对象，不扩大到相邻模块（理由：原文未写明范围，取最小影响默认）。",
                "期限与优先级" => "假设无紧急期限约束，按正常优先级执行（理由：原文未写明期限，不做加急假设）。",
                _ => $"假设「{dimension}」按最保守默认处理（理由：原文未显式说明）。",
            });
        }

        assumptions.Add(
            $"假设任务主体为「{ExtractSubject(taskText)}」的字面含义（理由：钢人重述以此为准，见 Restatement）。");
        assumptions.Add(
            "假设执行过程中遇到原文未覆盖的新歧义时，记录为待澄清缺口进入复盘，而不是擅自扩大执行（理由：与反对论证的风险控制一致）。");
        return assumptions;
    }

    /// <summary>
    /// 结构化模板 + 启发式填充。所有要素从任务文本真实提取（主体/动作/缺失维度），
    /// 绝不产出空串占位。
    /// </summary>
    internal static SteelmanFields BuildHeuristicFields(string taskText)
    {
        var subject = ExtractSubject(taskText);
        var missing = DetectMissingDimensions(taskText);
        var missingText = missing.Count > 0 ? string.Join("、", missing) : "验收标准与范围";

        var restatement =
            $"本任务的核心目标是：{subject}。" +
            $"执行方需在任务原文给定的范围内完成该目标，并产出与目标直接对应的可检验成果；" +
            $"原文未显式说明之处，按最小改动、不扩大范围的默认原则处理。";

        var pro =
            $"任务文本已显式给出明确对象（{subject}），目标可执行、边界可识别；" +
            $"完成后可直接产出与目标对应的成果并进入校验，执行路径清晰、收益确定。";

        var con =
            $"原文缺失 {missingText} 等关键信息，执行结果可能与真实期望偏离；" +
            $"若执行方自行补全这些缺口，存在做多余功或方向性返工的风险，" +
            $"因此反对在关键信息未确认前盲目全量执行。";

        var divergence =
            $"真正的分歧在于对「{subject}」完成标准的理解：执行方倾向按字面最小实现，" +
            $"而真实期望可能包含未写明的隐含要求。最可能改变结论的关键变量是：{FirstMissingOrAcceptance(missing)}。";

        var keyQuestion = BuildKeyQuestion(missing);

        return new SteelmanFields(restatement, pro, con, divergence, keyQuestion, AnalysisSource.Heuristic);
    }

    /// <summary>从任务文本提取主体：去掉礼貌前缀与尾部标点，取首个分句的核心内容。</summary>
    internal static string ExtractSubject(string taskText)
    {
        var text = taskText.Trim();
        foreach (var prefix in new[] { "请", "请你", "帮我", "帮忙", "麻烦", "我想", "我要", "需要", "希望" })
        {
            if (text.StartsWith(prefix, StringComparison.Ordinal) && text.Length > prefix.Length)
            {
                text = text[prefix.Length..].TrimStart('，', ',', '：', ':', ' ');
                break;
            }
        }

        // 取第一个分句（中英文分号/句号/换行切分）。
        var cut = text.IndexOfAny(new[] { '。', '；', '\n', ';' });
        if (cut > 0)
        {
            text = text[..cut];
        }

        return text.Length > 80 ? text[..80] : text;
    }

    /// <summary>检测任务文本缺失的维度（用于反对论证与关键问题定向）。</summary>
    internal static List<string> DetectMissingDimensions(string taskText)
    {
        var missing = new List<string>();

        if (!ContainsAny(taskText, "标准", "验收", "达到", "通过", "合格", "criteria", "acceptance", "pass"))
        {
            missing.Add("验收标准");
        }

        if (!ContainsAny(taskText, "范围", "边界", "仅限", "限于", "之内", "不包括", "scope", "boundary", "only"))
        {
            missing.Add("范围约束");
        }

        if (!ContainsAny(taskText, "期限", "截止", "时间", "优先级", "deadline", "priority", "by when"))
        {
            missing.Add("期限与优先级");
        }

        return missing;
    }

    internal static string BuildKeyQuestion(List<string> missing)
    {
        if (missing.Contains("验收标准"))
        {
            return "这个任务用什么具体标准判定完成算成功？";
        }

        if (missing.Contains("范围约束"))
        {
            return "这个任务应遵守的范围和边界是什么？";
        }

        if (missing.Contains("期限与优先级"))
        {
            return "这个任务的完成期限和优先级要求是什么？";
        }

        return "是否存在任务原文未写明、但会影响本次任务结果的关键背景信息？";
    }

    private static string FirstMissingOrAcceptance(List<string> missing) =>
        missing.Count > 0 ? missing[0] : "验收标准";

    private static bool ContainsAny(string text, params string[] keywords) =>
        keywords.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase));

    /// <summary>五要素中间结构（LLM 解析与启发式共用）。</summary>
    internal sealed record SteelmanFields(
        string Restatement,
        string ProArgument,
        string ConArgument,
        string Divergence,
        string OneKeyQuestion,
        AnalysisSource Source);
}
