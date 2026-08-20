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

namespace AeroAgent.Autonomy.Clarification;

/// <summary>
/// 澄清门：对任务文本做歧义度评分（主体/动作/验收标准/范围约束/上下文五维度
/// + 可选 LLM 判断），超过阈值时生成最多 3 个针对性澄清问题；未超阈值直接放行。
/// 无 LLM 时仅用确定性维度评分并记录 [DEGRADED]。
/// </summary>
public sealed class ClarificationGate
{
    /// <summary>默认歧义阈值：综合分 ≥ 该值即触发澄清。</summary>
    public const double DefaultThreshold = 0.45;

    /// <summary>澄清问题数量上限。</summary>
    public const int MaxQuestions = 3;

    private readonly AutonomyLlmClient _llm;
    private readonly ILogger<ClarificationGate> _logger;

    public ClarificationGate(AutonomyLlmClient llm, ILogger<ClarificationGate>? logger = null)
    {
        _llm = llm ?? throw new ArgumentNullException(nameof(llm));
        _logger = logger ?? NullLogger<ClarificationGate>.Instance;
    }

    /// <summary>
    /// 评估任务文本的歧义度。空文本抛 <see cref="ArgumentException"/>。
    /// </summary>
    /// <param name="taskText">任务文本。</param>
    /// <param name="threshold">触发澄清的阈值（默认 <see cref="DefaultThreshold"/>）。</param>
    /// <param name="ct">取消令牌。</param>
    public async Task<ClarificationResult> EvaluateAsync(
        string taskText,
        double threshold = DefaultThreshold,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(taskText))
        {
            throw new ArgumentException("任务文本不能为空。", nameof(taskText));
        }

        var heuristic = EvaluateHeuristic(taskText, threshold);

        if (!_llm.IsAvailable)
        {
            _logger.LogWarning("[DEGRADED] 无已配置 LLM provider，澄清门仅用确定性维度评分。");
            return heuristic;
        }

        var completion = await _llm.CompleteAsync(
            BuildClarificationSystemPrompt(), taskText, temperature: 0.1, ct);
        if (completion is null)
        {
            return heuristic; // CompleteAsync 内部已记录 [DEGRADED]。
        }

        var llmResult = TryParseLlmResult(completion.Content, threshold);
        if (llmResult is null)
        {
            _logger.LogWarning("[DEGRADED] LLM 澄清判断输出不可解析，退回维度评分结果。");
            return heuristic;
        }

        return llmResult;
    }

    /// <summary>
    /// 确定性维度评分（无 LLM 依赖，可独立测试）。
    /// 各维度独立打分后按权重合成综合歧义度。
    /// </summary>
    internal static ClarificationResult EvaluateHeuristic(string taskText, double threshold)
    {
        var scores = new Dictionary<string, double>
        {
            [AmbiguityDimension.Subject] = ScoreSubject(taskText),
            [AmbiguityDimension.Action] = ScoreAction(taskText),
            [AmbiguityDimension.Acceptance] = ScoreAcceptance(taskText),
            [AmbiguityDimension.Scope] = ScoreScope(taskText),
            [AmbiguityDimension.Context] = ScoreContext(taskText),
        };

        var composite =
            scores[AmbiguityDimension.Subject] * 0.25 +
            scores[AmbiguityDimension.Action] * 0.25 +
            scores[AmbiguityDimension.Acceptance] * 0.20 +
            scores[AmbiguityDimension.Scope] * 0.15 +
            scores[AmbiguityDimension.Context] * 0.15;
        composite = Math.Round(Math.Clamp(composite, 0.0, 1.0), 4);

        var requires = composite >= threshold;
        var questions = requires ? BuildQuestions(scores) : Array.Empty<ClarificationQuestion>();

        return new ClarificationResult
        {
            AmbiguityScore = composite,
            RequiresClarification = requires,
            Questions = questions,
            DimensionScores = scores,
            Source = AnalysisSource.Heuristic,
        };
    }

    internal static string BuildClarificationSystemPrompt()
    {
        var sb = new StringBuilder();
        sb.AppendLine("你是需求澄清评估器。判断用户任务的歧义程度，只输出 JSON，不要输出其他文本：");
        sb.AppendLine("{\"score\":0到1的小数,");
        sb.AppendLine("\"questions\":[\"针对性澄清问题1\",\"问题2\",\"问题3\"]}");
        sb.AppendLine("score 越高越模糊。questions 最多 3 个，只在确实需要澄清时给出；");
        sb.AppendLine("任务已经足够明确时 questions 必须是空数组。每个问题必须针对具体缺失信息，不许泛泛而问。");
        return sb.ToString();
    }

    internal static ClarificationResult? TryParseLlmResult(string llmOutput, double threshold)
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

            if (!root.TryGetProperty("score", out var scoreEl) || !scoreEl.TryGetDouble(out var score))
            {
                return null;
            }

            score = Math.Round(Math.Clamp(score, 0.0, 1.0), 4);

            var questions = new List<ClarificationQuestion>();
            if (root.TryGetProperty("questions", out var qEl) && qEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in qEl.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                    {
                        questions.Add(new ClarificationQuestion(AmbiguityDimension.Context, item.GetString()!.Trim()));
                    }

                    if (questions.Count >= MaxQuestions)
                    {
                        break;
                    }
                }
            }

            var requires = score >= threshold;
            return new ClarificationResult
            {
                AmbiguityScore = score,
                RequiresClarification = requires,
                Questions = requires ? questions : Array.Empty<ClarificationQuestion>(),
                DimensionScores = new Dictionary<string, double> { ["llm_overall"] = score },
                Source = AnalysisSource.Llm,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>按维度得分降序生成最多 3 个针对性问题（只取得分 ≥ 0.5 的维度）。</summary>
    internal static IReadOnlyList<ClarificationQuestion> BuildQuestions(IReadOnlyDictionary<string, double> scores)
    {
        var questions = new List<ClarificationQuestion>();
        foreach (var (dimension, score) in scores.OrderByDescending(kv => kv.Value))
        {
            if (score < 0.5 || questions.Count >= MaxQuestions)
            {
                continue;
            }

            questions.Add(new ClarificationQuestion(dimension, QuestionFor(dimension)));
        }

        return questions;
    }

    internal static string QuestionFor(string dimension) => dimension switch
    {
        AmbiguityDimension.Subject => "这个任务要操作的具体对象（或目标主体）是哪一个？",
        AmbiguityDimension.Action => "你希望执行的具体动作是什么（生成/修改/分析/部署等）？",
        AmbiguityDimension.Acceptance => "用什么标准判定这个任务完成算成功？",
        AmbiguityDimension.Scope => "这个任务的范围、边界或期限约束是什么？",
        AmbiguityDimension.Context => "任务里提到的背景/指代具体指什么？",
        _ => "能否补充这个任务的关键细节？",
    };

    private static double ScoreSubject(string text)
    {
        var score = 0.2;
        if (text.Trim().Length < 12)
        {
            score += 0.6; // 过短，主体大概率缺失。
        }

        if (ContainsAny(text, "这个", "那个", "这东西", "那东西", "它", "this thing", "that thing", "it"))
        {
            score += 0.3; // 指示代词无明确指代。
        }

        return Math.Clamp(score, 0.0, 1.0);
    }

    private static double ScoreAction(string text)
    {
        var hasVerb = ContainsAny(text,
            "做", "写", "实现", "修复", "分析", "查", "部署", "生成", "创建", "优化", "检查", "翻译", "计算",
            "总结", "设计", "搭建", "配置", "整理", "make", "write", "fix", "analyze", "build", "create",
            "deploy", "generate", "optimize", "check", "translate", "compute", "design");
        return hasVerb ? 0.1 : 0.8;
    }

    private static double ScoreAcceptance(string text)
    {
        var hasAcceptance = ContainsAny(text,
            "标准", "验收", "达到", "通过", "完成", "交付", "验证", "期望", "成功", "合格",
            "criteria", "acceptance", "pass", "deliver", "success", "expect");
        return hasAcceptance ? 0.1 : 0.7;
    }

    private static double ScoreScope(string text)
    {
        var hasScope = ContainsAny(text,
            "范围", "边界", "仅限", "限于", "之内", "不包括", "期限", "截止", "最多", "优先级",
            "scope", "boundary", "only", "within", "deadline", "priority");
        return hasScope ? 0.1 : 0.5;
    }

    private static double ScoreContext(string text)
    {
        var score = 0.1;
        if (ContainsAny(text, "上次", "之前", "刚才", "那个方案", "还是", "还没", "继续", "接着上次",
            "as before", "the previous", "still", "continue from"))
        {
            score += 0.5; // 依赖未给出的历史上下文。
        }

        return Math.Clamp(score, 0.0, 1.0);
    }

    private static bool ContainsAny(string text, params string[] keywords) =>
        keywords.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase));
}
