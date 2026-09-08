// Copyright (c) AeroCode
// AcsThresholds — ACS v2.3.0 阈值单一真相源的 C# 投影。
// 阈值只存内嵌资源 Acs/thresholds.json（直接来自 ACS spec/thresholds.json，逐字节同源）；
// 本类只做类型化读取，不提供第二套默认值（防阈值漂移）。
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AeroCode.Harness.Acs;

/// <summary>ACS 门禁退出码语义（0=PASS / 1=BLOCK / 2=USAGE_ERROR）。</summary>
public static class AcsExitCodes
{
    /// <summary>通过。</summary>
    public const int Pass = 0;

    /// <summary>阻断（未验证 = 未完成）。</summary>
    public const int Block = 1;

    /// <summary>用法错误（同样视为未验证）。</summary>
    public const int UsageError = 2;
}

/// <summary>任务分级参数（T0-T3）：候选数 N / 重复评估 R / 验证阈值 / 安全阀（轮数与分钟）。</summary>
public sealed record AcsTierSpec(
    [property: JsonPropertyName("n")] int N,
    [property: JsonPropertyName("r")] int R,
    [property: JsonPropertyName("threshold")] double Threshold,
    [property: JsonPropertyName("max_rounds")] int MaxRounds,
    [property: JsonPropertyName("max_minutes")] int MaxMinutes);

/// <summary>Loop 层成本闸门阈值（窄步/回灌/空转/思考预算的量化参数）。</summary>
public sealed record AcsLoopThresholds
{
    /// <summary>窄步闸：单步可验证产出上限。</summary>
    [JsonPropertyName("max_outputs_per_step")]
    public int MaxOutputsPerStep { get; init; } = 1;

    /// <summary>窄步闸：单步预算（分钟）。</summary>
    [JsonPropertyName("max_budget_min")]
    public int MaxBudgetMin { get; init; } = 30;

    /// <summary>回灌禁令：单步引用上文字节上限。</summary>
    [JsonPropertyName("max_context_bytes")]
    public int MaxContextBytes { get; init; } = 20000;

    /// <summary>回灌禁令：单步读文件数上限。</summary>
    [JsonPropertyName("max_files_read")]
    public int MaxFilesRead { get; init; } = 5;

    /// <summary>回灌禁令：跨步总结字符上限。</summary>
    [JsonPropertyName("max_summary_chars")]
    public int MaxSummaryChars { get; init; } = 1000;

    /// <summary>思考预算闸：思考占比警戒线。</summary>
    [JsonPropertyName("max_think_ratio")]
    public double MaxThinkRatio { get; init; } = 0.40;

    /// <summary>空转闸：连续无新证据的 strike 上限（two-strike）。</summary>
    [JsonPropertyName("spin_strikes")]
    public int SpinStrikes { get; init; } = 2;

    /// <summary>总结字符容差（超出上限但在此容差内仅告警）。</summary>
    [JsonPropertyName("summary_chars_tolerance")]
    public int SummaryCharsTolerance { get; init; } = 20;

    /// <summary>步骤必填字段清单（缺字段 = 无法核验 = BLOCK）。</summary>
    [JsonPropertyName("required_step_fields")]
    public IReadOnlyList<string> RequiredStepFields { get; init; } = Array.Empty<string>();
}

/// <summary>压缩交接契约阈值（目标锚点/三要素/字数硬上限/偏离自检）。</summary>
public sealed record AcsHandoffThresholds
{
    /// <summary>交接文件路径（单文件）。</summary>
    [JsonPropertyName("path")]
    public string Path { get; init; } = ".acs/handoff.md";

    /// <summary>字数硬上限（超出即 BLOCK）。</summary>
    [JsonPropertyName("max_chars_hard")]
    public int MaxCharsHard { get; init; } = 1000;

    /// <summary>字数目标（省 token 导向）。</summary>
    [JsonPropertyName("target_chars")]
    public int TargetChars { get; init; } = 400;

    /// <summary>字数容差。</summary>
    [JsonPropertyName("chars_tolerance")]
    public int CharsTolerance { get; init; } = 20;

    /// <summary>目标锚点最小字符数。</summary>
    [JsonPropertyName("min_goal_anchor_chars")]
    public int MinGoalAnchorChars { get; init; } = 8;

    /// <summary>下一步要素最小字符数。</summary>
    [JsonPropertyName("min_next_chars")]
    public int MinNextChars { get; init; } = 8;

    /// <summary>是否强制目标锚点。</summary>
    [JsonPropertyName("require_goal_anchor")]
    public bool RequireGoalAnchor { get; init; } = true;

    /// <summary>是否强制偏离自检标记。</summary>
    [JsonPropertyName("require_drift_checked")]
    public bool RequireDriftChecked { get; init; } = true;

    /// <summary>偏离自检标记文本。</summary>
    [JsonPropertyName("drift_marker")]
    public string DriftMarker { get; init; } = "drift_checked=true";
}

/// <summary>有界重试契约阈值（同法重试即空转）。</summary>
public sealed record AcsRetryThresholds
{
    /// <summary>最大重试次数（默认 2）。</summary>
    [JsonPropertyName("max_retries")]
    public int MaxRetries { get; init; } = 2;

    /// <summary>重试理由最小字符数（必写）。</summary>
    [JsonPropertyName("min_reason_chars")]
    public int MinReasonChars { get; init; } = 4;

    /// <summary>与上次差异说明最小字符数（必写）。</summary>
    [JsonPropertyName("min_delta_chars")]
    public int MinDeltaChars { get; init; } = 4;
}

/// <summary>每步双 AI 自审契约阈值（builder≠verifier）。</summary>
public sealed record AcsPerStepReviewThresholds
{
    /// <summary>适用分级。</summary>
    [JsonPropertyName("apply_tiers")]
    public IReadOnlyList<string> ApplyTiers { get; init; } = Array.Empty<string>();

    /// <summary>verdict 合法值（pass/pass_with_fixes/fail）。</summary>
    [JsonPropertyName("verdict_values")]
    public IReadOnlyList<string> VerdictValues { get; init; } = Array.Empty<string>();

    /// <summary>强制 verifier ≠ builder。</summary>
    [JsonPropertyName("require_verifier_ne_builder")]
    public bool RequireVerifierNeBuilder { get; init; } = true;

    /// <summary>自查最小字符数。</summary>
    [JsonPropertyName("min_self_check_chars")]
    public int MinSelfCheckChars { get; init; } = 4;

    /// <summary>问题描述最小字符数。</summary>
    [JsonPropertyName("min_issue_chars")]
    public int MinIssueChars { get; init; } = 4;

    /// <summary>T3 每步最少自审轮数。</summary>
    [JsonPropertyName("t3_min_rounds")]
    public int T3MinRounds { get; init; } = 2;
}

/// <summary>1-20 细粒度自验证排序阈值（pivot 近似排序）。</summary>
public sealed record AcsVerifyRankThresholds
{
    /// <summary>pivot 数量（O(N²)→O(Nk)）。</summary>
    [JsonPropertyName("pivot_k")]
    public int PivotK { get; init; } = 2;

    /// <summary>分差上限（超过视为显著分歧）。</summary>
    [JsonPropertyName("spread_limit")]
    public int SpreadLimit { get; init; } = 5;

    /// <summary>权重容差。</summary>
    [JsonPropertyName("weight_tol")]
    public double WeightTol { get; init; } = 0.001;

    /// <summary>加权容差。</summary>
    [JsonPropertyName("weighted_tol")]
    public double WeightedTol { get; init; } = 0.05;

    /// <summary>最小评价标准数。</summary>
    [JsonPropertyName("min_criteria")]
    public int MinCriteria { get; init; } = 5;
}

/// <summary>真实性扫描阈值（allow 标记 + finding 上限）。</summary>
public sealed record AcsRealityScanThresholds
{
    /// <summary>豁免标记（行内含此标记的模糊措辞放行）。</summary>
    [JsonPropertyName("allow_marker")]
    public string AllowMarker { get; init; } = "ACS-ALLOW";

    /// <summary>单次扫描 finding 上限。</summary>
    [JsonPropertyName("default_max_findings")]
    public int DefaultMaxFindings { get; init; } = 200;
}

/// <summary>模糊措辞词表（验收侧 + 证据侧）。</summary>
public sealed record AcsVaguePhrases
{
    /// <summary>验收标准禁用模糊措辞。</summary>
    [JsonPropertyName("acceptance")]
    public IReadOnlyList<string> Acceptance { get; init; } = Array.Empty<string>();

    /// <summary>证据描述禁用模糊措辞。</summary>
    [JsonPropertyName("evidence")]
    public IReadOnlyList<string> Evidence { get; init; } = Array.Empty<string>();
}

/// <summary>ACS v2.3.0 阈值全集（内嵌资源加载，只读投影）。</summary>
public sealed class AcsThresholds
{
    private static readonly Lazy<AcsThresholds> DefaultInstance = new(LoadFromEmbeddedResource);

    /// <summary>默认实例（内嵌 thresholds.json，进程内单例）。</summary>
    public static AcsThresholds Default => DefaultInstance.Value;

    /// <summary>套件规格版本。</summary>
    public string SpecVersion { get; init; } = string.Empty;

    /// <summary>T0-T3 分级参数表。</summary>
    public IReadOnlyDictionary<string, AcsTierSpec> Tiers { get; init; } =
        new Dictionary<string, AcsTierSpec>();

    /// <summary>Loop 层成本闸门阈值。</summary>
    public AcsLoopThresholds Loop { get; init; } = new();

    /// <summary>压缩交接契约阈值。</summary>
    public AcsHandoffThresholds Handoff { get; init; } = new();

    /// <summary>有界重试契约阈值。</summary>
    public AcsRetryThresholds Retry { get; init; } = new();

    /// <summary>每步双 AI 自审契约阈值。</summary>
    public AcsPerStepReviewThresholds PerStepReview { get; init; } = new();

    /// <summary>1-20 细粒度排序阈值。</summary>
    public AcsVerifyRankThresholds VerifyRank { get; init; } = new();

    /// <summary>真实性扫描阈值。</summary>
    public AcsRealityScanThresholds RealityScan { get; init; } = new();

    /// <summary>模糊措辞词表。</summary>
    public AcsVaguePhrases VaguePhrases { get; init; } = new();

    /// <summary>取指定分级的参数；未知分级回退 T1（保守：不放任也不过度）。</summary>
    public AcsTierSpec Tier(string tier) =>
        Tiers.TryGetValue(tier, out var spec) ? spec : Tiers["T1"];

    /// <summary>从内嵌资源加载（单一真相源；资源缺失 = 硬失败，不做静默默认）。</summary>
    public static AcsThresholds LoadFromEmbeddedResource()
    {
        var asm = typeof(AcsThresholds).Assembly;
        var resourceName = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("Acs.thresholds.json", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                "ACS thresholds embedded resource not found (Acs/thresholds.json must be embedded).");

        using var stream = asm.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("ACS thresholds resource stream is null.");
        using var reader = new StreamReader(stream);
        var json = reader.ReadToEnd();
        return Parse(json);
    }

    /// <summary>从 JSON 文本解析（测试与自定义阈值注入用）。</summary>
    public static AcsThresholds Parse(string json)
    {
        var doc = JsonSerializer.Deserialize<JsonElement>(json);

        return new AcsThresholds
        {
            SpecVersion = doc.TryGetProperty("spec_version", out var sv) ? sv.GetString() ?? "" : "",
            Tiers = ParseTiers(doc),
            Loop = ParseSection<AcsLoopThresholds>(doc, "loop") ?? new AcsLoopThresholds(),
            Handoff = ParseSection<AcsHandoffThresholds>(doc, "handoff") ?? new AcsHandoffThresholds(),
            Retry = ParseSection<AcsRetryThresholds>(doc, "retry") ?? new AcsRetryThresholds(),
            PerStepReview = ParseSection<AcsPerStepReviewThresholds>(doc, "per_step_review") ?? new AcsPerStepReviewThresholds(),
            VerifyRank = ParseSection<AcsVerifyRankThresholds>(doc, "verify_rank") ?? new AcsVerifyRankThresholds(),
            RealityScan = ParseSection<AcsRealityScanThresholds>(doc, "reality_scan") ?? new AcsRealityScanThresholds(),
            VaguePhrases = ParseSection<AcsVaguePhrases>(doc, "vague_phrases") ?? new AcsVaguePhrases(),
        };
    }

    private static Dictionary<string, AcsTierSpec> ParseTiers(JsonElement doc)
    {
        var result = new Dictionary<string, AcsTierSpec>(StringComparer.Ordinal);
        if (!doc.TryGetProperty("tiers", out var tiers))
        {
            return result;
        }

        foreach (var prop in tiers.EnumerateObject())
        {
            var spec = prop.Value.Deserialize<AcsTierSpec>();
            if (spec is not null)
            {
                result[prop.Name] = spec;
            }
        }

        return result;
    }

    private static T? ParseSection<T>(JsonElement doc, string name) where T : class
    {
        if (!doc.TryGetProperty(name, out var section))
        {
            return null;
        }

        try
        {
            return section.Deserialize<T>();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
