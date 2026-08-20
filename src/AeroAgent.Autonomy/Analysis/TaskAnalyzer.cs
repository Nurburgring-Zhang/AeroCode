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

namespace AeroAgent.Autonomy.Analysis;

/// <summary>
/// 任务分析器：任务文本 → <see cref="TaskAnalysis"/>（类型/复杂度/能力需求）。
/// 实现 = 确定性启发式（中英文关键词 + 结构特征打分，规则全程可解释）
///      + 可选 LLM 增强（经 <see cref="AutonomyLlmClient"/>；未配置 provider 或
///        LLM 输出不可解析时仅用启发式，并记录 [DEGRADED]）。
/// </summary>
public sealed class TaskAnalyzer
{
    /// <summary>单一类型被视为"显著"的最低关键词得分。</summary>
    internal const int SignificantScore = 2;

    private readonly AutonomyLlmClient _llm;
    private readonly ILogger<TaskAnalyzer> _logger;

    public TaskAnalyzer(AutonomyLlmClient llm, ILogger<TaskAnalyzer>? logger = null)
    {
        _llm = llm ?? throw new ArgumentNullException(nameof(llm));
        _logger = logger ?? NullLogger<TaskAnalyzer>.Instance;
    }

    /// <summary>分析任务文本。空/空白文本抛 <see cref="ArgumentException"/>。</summary>
    public async Task<TaskAnalysis> AnalyzeAsync(string taskText, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(taskText))
        {
            throw new ArgumentException("任务文本不能为空。", nameof(taskText));
        }

        var heuristic = AnalyzeHeuristic(taskText);

        if (!_llm.IsAvailable)
        {
            _logger.LogWarning("[DEGRADED] 无已配置 LLM provider，任务分析仅用确定性启发式。");
            return heuristic;
        }

        var completion = await _llm.CompleteAsync(
            BuildAnalysisSystemPrompt(),
            taskText,
            temperature: 0.1,
            ct);
        if (completion is null)
        {
            return heuristic; // CompleteAsync 内部已记录 [DEGRADED]。
        }

        var llmAnalysis = TryParseLlmAnalysis(taskText, completion.Content, heuristic);
        if (llmAnalysis is null)
        {
            _logger.LogWarning("[DEGRADED] LLM 任务分析输出不可解析，退回启发式结果。");
            return heuristic;
        }

        return llmAnalysis;
    }

    /// <summary>
    /// 确定性启发式分析（无 LLM 依赖，可独立测试）。
    /// 类型 = 关键词命中打分；复杂度 = 长度/结构/领域数/能力数叠加；能力需求 = 类型+关键词推导。
    /// </summary>
    internal static TaskAnalysis AnalyzeHeuristic(string taskText)
    {
        var scores = new Dictionary<TaskType, int>
        {
            [TaskType.Code] = Score(taskText, CodeKeywords),
            [TaskType.Research] = Score(taskText, ResearchKeywords),
            [TaskType.Analysis] = Score(taskText, AnalysisKeywords),
            [TaskType.Creative] = Score(taskText, CreativeKeywords),
            [TaskType.Ops] = Score(taskText, OpsKeywords),
        };

        var significant = scores.Where(kv => kv.Value >= SignificantScore).Select(kv => kv.Key).ToList();
        TaskType type;
        var rationaleParts = new List<string>();

        if (significant.Count >= 2)
        {
            type = TaskType.Composite;
            rationaleParts.Add($"多类型显著并存（{string.Join("、", significant.Select(t => t.ToString()))}）→ 判定 Composite");
        }
        else
        {
            var best = scores.OrderByDescending(kv => kv.Value).First();
            if (best.Value >= 1)
            {
                type = best.Key;
                rationaleParts.Add($"类型关键词命中：{best.Key} 得 {best.Value} 分");
            }
            else
            {
                type = LooksLikeQuestion(taskText) ? TaskType.Analysis : TaskType.Code;
                rationaleParts.Add($"无类型关键词命中，按文本形态默认 {type}");
            }
        }

        var capabilities = InferCapabilities(taskText, type);
        var complexity = ScoreComplexity(taskText, scores, capabilities.Count);
        rationaleParts.Add(BuildKeywordEvidence(taskText, scores));

        return new TaskAnalysis
        {
            Type = type,
            Complexity = complexity,
            Capabilities = capabilities,
            TypeScores = scores,
            Rationale = string.Join("；", rationaleParts.Where(s => s.Length > 0)),
            Source = AnalysisSource.Heuristic,
        };
    }

    /// <summary>复杂度评分：基础 1 分，按长度/枚举结构/多领域/连接词/能力数叠加，上限 5。</summary>
    internal static int ScoreComplexity(string text, IReadOnlyDictionary<TaskType, int> scores, int capabilityCount)
    {
        var complexity = 1;

        if (text.Length > 200)
        {
            complexity++;
        }

        if (text.Length > 600)
        {
            complexity++;
        }

        var bullets = CountListItems(text);
        if (bullets >= 3)
        {
            complexity++;
        }

        if (bullets >= 6)
        {
            complexity++;
        }

        var domains = scores.Count(kv => kv.Value >= SignificantScore);
        if (domains >= 2)
        {
            complexity++;
        }

        var connectors = Connectors.Count(c => text.Contains(c, StringComparison.OrdinalIgnoreCase));
        if (connectors >= 2)
        {
            complexity++;
        }

        if (capabilityCount >= 3)
        {
            complexity++;
        }

        return Math.Clamp(complexity, 1, 5);
    }

    /// <summary>统计枚举项数量（"1." / "-" / "*" 行首，或中文序号"一、二、"）。</summary>
    internal static int CountListItems(string text)
    {
        var count = 0;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimStart();
            if (line.Length == 0)
            {
                continue;
            }

            if ((char.IsDigit(line[0]) && line.Length > 1 && (line[1] == '.' || line[1] == '、'))
                || line[0] == '-'
                || line[0] == '*'
                || (line.Length > 1 && line[1] == '、' && "一二三四五六七八九十".Contains(line[0])))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>由类型与关键词推导所需能力集合。</summary>
    internal static IReadOnlyList<CapabilityNeed> InferCapabilities(string text, TaskType type)
    {
        var needs = new List<CapabilityNeed>();

        if (type is TaskType.Code or TaskType.Composite)
        {
            needs.Add(new CapabilityNeed(
                CapabilityKind.Skill, "engineering/code-review",
                "代码类任务需要评审/TDD 等工程技能把关产出质量"));
        }

        if (type is TaskType.Research or TaskType.Composite
            || ContainsAny(text, "检索", "搜索", "查资料", "调研", "search", "research", "网上", "全网", "网页"))
        {
            needs.Add(new CapabilityNeed(
                CapabilityKind.Retrieval, "web-research",
                "任务需要外部检索（资料/网页/搜索引擎）"));
        }

        if (ContainsAny(text, "并行", "拆解", "子任务", "分工", "parallel", "decompose", "subtask"))
        {
            needs.Add(new CapabilityNeed(
                CapabilityKind.Harness, "task-graph",
                "任务含并行/拆解结构，需要任务图原语"));
        }

        if (ContainsAny(text, "迭代", "修复环", "直到", "重试", "loop", "iterate", "自修复"))
        {
            needs.Add(new CapabilityNeed(
                CapabilityKind.Harness, "loop",
                "任务需要迭代修复循环原语"));
        }

        if (ContainsAny(text, "笔记", "note", "记录到", "存到", "文件", "file"))
        {
            needs.Add(new CapabilityNeed(
                CapabilityKind.Tool, "note-toolbox",
                "任务涉及笔记/文件读写，需要注册工具执行"));
        }

        if (type is TaskType.Ops && needs.All(n => n.Kind != CapabilityKind.Tool))
        {
            needs.Add(new CapabilityNeed(
                CapabilityKind.Tool, "ops-tooling",
                "运维类任务通常需要工具执行（命令/配置）"));
        }

        return needs;
    }

    internal static string BuildAnalysisSystemPrompt()
    {
        var sb = new StringBuilder();
        sb.AppendLine("你是任务分析器。分析用户任务，只输出 JSON，不要输出其他文本：");
        sb.AppendLine("{\"type\":\"code|research|analysis|creative|ops|composite\",");
        sb.AppendLine("\"complexity\":1到5的整数,");
        sb.AppendLine("\"capabilities\":[\"retrieval:web-research\",\"skill:engineering/code-review\",\"harness:task-graph\",\"harness:loop\",\"tool:note-toolbox\"],");
        sb.AppendLine("\"rationale\":\"不超过60字的判定理由\"}");
        sb.AppendLine("type 定义：code=编程实现/调试/测试；research=调研/检索/查资料；analysis=分析/总结/对比/评估；");
        sb.AppendLine("creative=写作/文案/创作；ops=部署/运维/监控/配置；composite=两个及以上类型显著并存。");
        sb.AppendLine("complexity 定义：1=单步琐碎，2=明确单领域任务，3=多步骤，4=多步骤且多约束，5=多领域多阶段高难度。");
        return sb.ToString();
    }

    /// <summary>解析 LLM 分析输出；任何字段缺失/越界返回 null（调用方退回启发式）。</summary>
    internal static TaskAnalysis? TryParseLlmAnalysis(
        string taskText, string llmOutput, TaskAnalysis heuristicFallback)
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

            if (!root.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            if (!TryParseTaskType(typeEl.GetString(), out var type))
            {
                return null;
            }

            var complexity = heuristicFallback.Complexity;
            if (root.TryGetProperty("complexity", out var cxEl) && cxEl.TryGetInt32(out var cx))
            {
                complexity = Math.Clamp(cx, 1, 5);
            }

            var capabilities = new List<CapabilityNeed>();
            if (root.TryGetProperty("capabilities", out var capEl) && capEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in capEl.EnumerateArray())
                {
                    var parsed = ParseCapability(item.GetString());
                    if (parsed is not null)
                    {
                        capabilities.Add(parsed);
                    }
                }
            }

            if (capabilities.Count == 0)
            {
                capabilities.AddRange(heuristicFallback.Capabilities);
            }

            var rationale = root.TryGetProperty("rationale", out var rEl) && rEl.ValueKind == JsonValueKind.String
                ? rEl.GetString() ?? string.Empty
                : string.Empty;

            return new TaskAnalysis
            {
                Type = type,
                Complexity = complexity,
                Capabilities = capabilities,
                TypeScores = heuristicFallback.TypeScores,
                Rationale = rationale.Length > 0 ? $"LLM 判定：{rationale}" : heuristicFallback.Rationale,
                Source = AnalysisSource.Llm,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>解析 "kind:name" 能力串（kind ∈ skill/harness/retrieval/tool）。</summary>
    internal static CapabilityNeed? ParseCapability(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var idx = raw.IndexOf(':');
        if (idx <= 0 || idx >= raw.Length - 1)
        {
            return null;
        }

        var kind = raw[..idx].Trim().ToLowerInvariant();
        var name = raw[(idx + 1)..].Trim();
        return kind switch
        {
            "skill" => new CapabilityNeed(CapabilityKind.Skill, name, "LLM 判定需要该技能"),
            "harness" => new CapabilityNeed(CapabilityKind.Harness, name, "LLM 判定需要该 Harness 原语"),
            "retrieval" => new CapabilityNeed(CapabilityKind.Retrieval, name, "LLM 判定需要外部检索"),
            "tool" => new CapabilityNeed(CapabilityKind.Tool, name, "LLM 判定需要工具执行"),
            _ => null,
        };
    }

    private static bool TryParseTaskType(string? raw, out TaskType type)
    {
        switch ((raw ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "code": type = TaskType.Code; return true;
            case "research": type = TaskType.Research; return true;
            case "analysis": type = TaskType.Analysis; return true;
            case "creative": type = TaskType.Creative; return true;
            case "ops": type = TaskType.Ops; return true;
            case "composite": type = TaskType.Composite; return true;
            default: type = TaskType.Code; return false;
        }
    }

    private static bool LooksLikeQuestion(string text) =>
        ContainsAny(text, "?", "？", "什么", "为什么", "如何", "怎么", "why", "what", "how");

    private static string BuildKeywordEvidence(string text, IReadOnlyDictionary<TaskType, int> scores)
    {
        var hits = new List<string>();
        foreach (var (type, keywords) in KeywordTables)
        {
            var matched = keywords.Where(k => text.Contains(k, StringComparison.OrdinalIgnoreCase)).Take(5).ToList();
            if (matched.Count > 0)
            {
                hits.Add($"{type}[{string.Join(",", matched)}]={scores[type]}");
            }
        }

        return hits.Count > 0 ? $"命中证据 {string.Join(" ", hits)}" : string.Empty;
    }

    private static int Score(string text, IReadOnlyList<string> keywords) =>
        keywords.Count(k => text.Contains(k, StringComparison.OrdinalIgnoreCase));

    private static bool ContainsAny(string text, params string[] keywords) =>
        keywords.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase));

    private static readonly IReadOnlyList<string> CodeKeywords = new[]
    {
        "代码", "编程", "函数", "实现", "调试", "编译", "脚本", "重构", "单元测试", "接口",
        "bug", "code", "debug", "implement", "refactor", "compile", "script", "api", "sql", "编译",
    };

    private static readonly IReadOnlyList<string> ResearchKeywords = new[]
    {
        "研究", "调研", "检索", "搜索资料", "文献", "全网", "网页", "爬取", "收集资料", "查一查",
        "research", "survey", "investigate", "crawl", "look up",
    };

    private static readonly IReadOnlyList<string> AnalysisKeywords = new[]
    {
        "分析", "总结", "对比", "评估", "报告", "解读", "归因", "复盘", "评价",
        "analyze", "summarize", "compare", "evaluate", "report",
    };

    private static readonly IReadOnlyList<string> CreativeKeywords = new[]
    {
        "写文章", "文案", "润色", "小说", "诗", "创作", "标语", "故事", "公众号", "推文", "剧本",
        "write a story", "poem", "slogan", "creative", "blog post", "draft an essay",
    };

    private static readonly IReadOnlyList<string> OpsKeywords = new[]
    {
        "部署", "运维", "监控", "告警", "发布", "配置服务器", "容器", "流水线", "巡检", "备份", "上线",
        "deploy", "monitor", "server", "release", "docker", "nginx", "pipeline", "backup",
    };

    private static readonly IReadOnlyList<string> Connectors = new[]
    {
        "并且", "然后", "接着", "最后", "首先", "其次", "同时", "以及", "再",
        "and then", "after that", "finally", "firstly", "also",
    };

    private static readonly IReadOnlyDictionary<TaskType, IReadOnlyList<string>> KeywordTables =
        new Dictionary<TaskType, IReadOnlyList<string>>
        {
            [TaskType.Code] = CodeKeywords,
            [TaskType.Research] = ResearchKeywords,
            [TaskType.Analysis] = AnalysisKeywords,
            [TaskType.Creative] = CreativeKeywords,
            [TaskType.Ops] = OpsKeywords,
        };
}
