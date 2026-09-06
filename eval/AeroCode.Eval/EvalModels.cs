using System.Text.Json;
using System.Text.Json.Serialization;
using AeroAgent.Moa.Gateway;

namespace AeroCode.Eval;

/// <summary>
/// fixture 数据集顶层模型（版本冻结 v0）。
/// schema（写死于每份 JSON 顶部的 <c>$schema_note</c>）：顶层必填
/// <c>dataset_id / metric(口径) / version(版本戳) / criteria(数据集级判据) / tasks[]</c>；
/// <c>tasks[]</c> 每条必填 <c>id / metric(口径) / criteria(判据) / version(版本戳) / payload</c>，
/// 由 <see cref="FixtureLoader"/> fail-fast 校验。
/// </summary>
public sealed record EvalFixtureDataset
{
    [JsonPropertyName("dataset_id")]
    public string DatasetId { get; init; } = string.Empty;

    /// <summary>口径标识（指标键），须与 runner 的 MetricKey 一致。</summary>
    [JsonPropertyName("metric")]
    public string Metric { get; init; } = string.Empty;

    /// <summary>数据集版本戳（冻结 v0）。</summary>
    [JsonPropertyName("version")]
    public string Version { get; init; } = string.Empty;

    /// <summary>数据集级判据说明。</summary>
    [JsonPropertyName("criteria")]
    public string Criteria { get; init; } = string.Empty;

    [JsonPropertyName("tasks")]
    public IReadOnlyList<EvalFixtureTask> Tasks { get; init; } = [];
}

/// <summary>单条评测任务：四类必填字段 id / 口径 / 判据 / 版本戳 + 指标判题载荷。</summary>
public sealed record EvalFixtureTask
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    /// <summary>口径（与所属数据集的 metric 一致）。</summary>
    [JsonPropertyName("metric")]
    public string Metric { get; init; } = string.Empty;

    /// <summary>本条任务的判据（人类可读，冻结）。</summary>
    [JsonPropertyName("criteria")]
    public string Criteria { get; init; } = string.Empty;

    /// <summary>版本戳（冻结 v0）。</summary>
    [JsonPropertyName("version")]
    public string Version { get; init; } = string.Empty;

    /// <summary>指标判题载荷（结构由各指标 runner 定义）。</summary>
    [JsonPropertyName("payload")]
    public JsonElement Payload { get; init; }
}

/// <summary>指标结果的常量状态标签。</summary>
public static class EvalStatus
{
    public const string Pending = "PENDING";
    public const string Ok = "OK";
    public const string Failed = "FAILED";
}

/// <summary>单任务评测结果（报告表格的一行）。</summary>
public sealed record EvalTaskOutcome
{
    public required string TaskId { get; init; }

    /// <summary>PENDING（未取得值）/ OK（已判定）/ FAILED（fixture 载荷非法）。</summary>
    public required string Status { get; init; }

    /// <summary>任务级结果值（如 HALLUCINATED / 3/4）。</summary>
    public string? Value { get; init; }

    /// <summary>说明（采集失败原因、判定依据等；已脱敏、单行）。</summary>
    public string? Detail { get; init; }
}

/// <summary>单个指标段的结果。</summary>
public sealed record EvalMetricResult
{
    public required string MetricKey { get; init; }
    public required string DisplayName { get; init; }

    /// <summary>口径。</summary>
    public required string MetricDefinition { get; init; }

    /// <summary>判据（取数据集级判据）。</summary>
    public required string CriteriaSummary { get; init; }

    /// <summary>PENDING / OK。</summary>
    public required string Status { get; init; }

    /// <summary>指标值（PENDING 或数值文本）。</summary>
    public string? Value { get; init; }

    /// <summary>补充说明（dry-run 原因、采集失败原因等；已脱敏）。</summary>
    public string? Note { get; init; }

    public required IReadOnlyList<EvalTaskOutcome> Tasks { get; init; }
}

/// <summary>为缺网关/缺任务场景统一构造 PENDING 结果。</summary>
internal static class EvalMetricResultFactory
{
    public static EvalMetricResult Pending(
        IEvalMetricRunner runner,
        EvalFixtureDataset? dataset,
        string note,
        string? perTaskNote = null)
    {
        return new EvalMetricResult
        {
            MetricKey = runner.MetricKey,
            DisplayName = runner.DisplayName,
            MetricDefinition = runner.MetricDefinition,
            CriteriaSummary = string.IsNullOrWhiteSpace(dataset?.Criteria)
                ? "（未找到该指标的 fixture 数据集）"
                : dataset!.Criteria,
            Status = EvalStatus.Pending,
            Value = EvalStatus.Pending,
            Note = note,
            Tasks = (dataset?.Tasks ?? []).Select(t => new EvalTaskOutcome
            {
                TaskId = t.Id,
                Status = EvalStatus.Pending,
                Detail = perTaskNote ?? note,
            }).ToList(),
        };
    }
}

/// <summary>指标 runner 契约：每个三指标模块实现一个。</summary>
public interface IEvalMetricRunner
{
    /// <summary>口径标识（与 fixture 的 metric 字段一致）。</summary>
    string MetricKey { get; }

    /// <summary>展示名。</summary>
    string DisplayName { get; }

    /// <summary>口径定义（写进报告）。</summary>
    string MetricDefinition { get; }

    Task<EvalMetricResult> EvaluateAsync(EvalFixtureDataset? dataset, EvalRunContext context);
}

/// <summary>一次基线运行的共享上下文。</summary>
public sealed record EvalRunContext
{
    public required SensitiveScrubber Scrubber { get; init; }

    /// <summary>null = 不经网关采集（dry-run 或探活失败），runner 一律产出 PENDING。</summary>
    public MoaGatewayClient? Gateway { get; init; }

    /// <summary>网关不可用原因（探活失败等，已脱敏）；Gateway 为 null 时用于说明。</summary>
    public string? GatewayUnavailableReason { get; init; }

    public CancellationToken CancellationToken { get; init; }
}
