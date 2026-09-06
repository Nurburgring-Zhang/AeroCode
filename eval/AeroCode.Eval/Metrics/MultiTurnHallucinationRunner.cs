using System.Text.Json;
using System.Text.Json.Serialization;
using AeroAgent.Moa.Gateway;

namespace AeroCode.Eval.Metrics;

/// <summary>
/// 指标①多轮幻觉率。口径：多轮交互（非单轮 QA）——每条 fixture 是一段多轮对话序列，
/// 证据只出现在前序轮次；经 MoaGatewayClient 以「历史轮 + 最终轮提问」真实采集最终轮答案，
/// 判据：答案与对话内证据一致（禁答断言命中 → HALLUCINATED；证据锚点缺失 → ungrounded 备注）。
/// v0 判题为确定性关键词启发式（判据冻结在 fixture 的 criteria 字段）。
/// </summary>
public sealed class MultiTurnHallucinationRunner : IEvalMetricRunner
{
    private static readonly JsonSerializerOptions PayloadOptions = new() { PropertyNameCaseInsensitive = true };

    public string MetricKey => "multi_turn_hallucination_rate";

    public string DisplayName => "多轮幻觉率";

    public string MetricDefinition =>
        "多轮交互口径（非单轮 QA）：每条 fixture 为一段多轮对话序列，对话内证据只出现在前序轮次；" +
        "对模型最终轮答案判幻觉。指标值 = 判定幻觉的任务数 / 总任务数。";

    public async Task<EvalMetricResult> EvaluateAsync(EvalFixtureDataset? dataset, EvalRunContext context)
    {
        var tasks = dataset?.Tasks ?? [];
        if (tasks.Count == 0)
        {
            return EvalMetricResultFactory.Pending(this, dataset, "无 fixture 任务。");
        }

        if (context.Gateway is null)
        {
            var why = context.GatewayUnavailableReason
                      ?? "dry-run：未配置网关（AEROCODE_EVAL_GATEWAY_KEY 缺失），未执行真实采集。";
            return EvalMetricResultFactory.Pending(this, dataset, why, why);
        }

        var outcomes = new List<EvalTaskOutcome>();
        var hallucinated = 0;
        var judged = 0;
        foreach (var task in tasks)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var payload = ParsePayload(task, context.Scrubber, outcomes);
            if (payload is null)
            {
                continue;
            }

            var result = await context.Gateway.ExecuteAsync(BuildRequest(payload), context.CancellationToken);
            if (!result.IsSuccess || result.Value is null)
            {
                outcomes.Add(new EvalTaskOutcome
                {
                    TaskId = task.Id,
                    Status = EvalStatus.Pending,
                    Detail = SingleLine(context.Scrubber.Scrub($"采集失败: {result.Error}")),
                });
                continue;
            }

            var content = result.Value.FinalContent ?? string.Empty;
            var forbiddenHit = payload.ForbiddenClaims.FirstOrDefault(claim =>
                !string.IsNullOrEmpty(claim) && content.Contains(claim, StringComparison.Ordinal));
            var missingGrounding = payload.RequiredGrounding.Where(anchor =>
                !string.IsNullOrEmpty(anchor) && !content.Contains(anchor, StringComparison.Ordinal)).ToList();
            var isHallucinated = forbiddenHit is not null;
            judged++;
            if (isHallucinated)
            {
                hallucinated++;
            }

            var detail =
                $"禁答断言命中: {(forbiddenHit is null ? "无" : forbiddenHit)}；" +
                $"缺失证据锚点: {(missingGrounding.Count == 0 ? "无" : string.Join("、", missingGrounding))}" +
                (result.IsMock ? "；网关标注 mock" : string.Empty);
            outcomes.Add(new EvalTaskOutcome
            {
                TaskId = task.Id,
                Status = EvalStatus.Ok,
                Value = isHallucinated ? "HALLUCINATED" : "GROUNDED",
                Detail = SingleLine(context.Scrubber.Scrub(detail)),
            });
        }

        return BuildResult(tasks.Count, judged, hallucinated, outcomes, dataset, context.Scrubber);
    }

    private EvalMetricResult BuildResult(
        int totalTasks,
        int judged,
        int hallucinated,
        List<EvalTaskOutcome> outcomes,
        EvalFixtureDataset? dataset,
        SensitiveScrubber scrubber)
    {
        var value = judged == 0
            ? EvalStatus.Pending
            : $"{(double)hallucinated / judged:P2}（{hallucinated}/{judged} 任务判定幻觉）";
        var allJudged = judged == totalTasks;
        return new EvalMetricResult
        {
            MetricKey = MetricKey,
            DisplayName = DisplayName,
            MetricDefinition = MetricDefinition,
            CriteriaSummary = string.IsNullOrWhiteSpace(dataset?.Criteria)
                ? "（未找到该指标的 fixture 数据集）"
                : dataset!.Criteria,
            Status = allJudged && judged > 0 ? EvalStatus.Ok : EvalStatus.Pending,
            Value = value,
            Note = allJudged
                ? null
                : $"仅 {judged}/{totalTasks} 任务取得可判定答案（其余见任务明细；已脱敏）。",
            Tasks = outcomes,
        };
    }

    internal static MoaGatewayExecuteRequest BuildRequest(MultiTurnPayload payload)
    {
        var turns = payload.Turns;
        var context = turns.Count <= 1
            ? []
            : turns.Take(turns.Count - 1)
                .Select(t => new MoaGatewayChatMessage(t.Role, t.Content))
                .ToList();
        return new MoaGatewayExecuteRequest
        {
            Query = turns[^1].Content,
            Context = context,
        };
    }

    internal static MultiTurnPayload? ParsePayload(
        EvalFixtureTask task, SensitiveScrubber scrubber, List<EvalTaskOutcome> outcomes)
    {
        if (task.Payload.ValueKind != JsonValueKind.Object)
        {
            outcomes.Add(new EvalTaskOutcome
            {
                TaskId = task.Id,
                Status = EvalStatus.Failed,
                Detail = "fixture payload 缺失或不是对象",
            });
            return null;
        }

        MultiTurnPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<MultiTurnPayload>(task.Payload, PayloadOptions);
        }
        catch (JsonException ex)
        {
            outcomes.Add(new EvalTaskOutcome
            {
                TaskId = task.Id,
                Status = EvalStatus.Failed,
                Detail = SingleLine(scrubber.Scrub($"payload 解析失败: {ex.Message}")),
            });
            return null;
        }

        if (payload is null || payload.Turns.Count < 2 || string.IsNullOrWhiteSpace(payload.Turns[^1].Content))
        {
            outcomes.Add(new EvalTaskOutcome
            {
                TaskId = task.Id,
                Status = EvalStatus.Failed,
                Detail = "payload 非法：多轮任务至少需要 2 个 turns，且最终轮 content 非空",
            });
            return null;
        }

        return payload;
    }

    internal static string SingleLine(string text) =>
        text.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
}

/// <summary>多轮幻觉率任务的判题载荷（JSON schema 冻结于 datasets 的 $schema_note）。</summary>
public sealed record MultiTurnPayload
{
    [JsonPropertyName("turns")]
    public IReadOnlyList<Turn> Turns { get; init; } = [];

    /// <summary>对话内证据锚点词：最终答案应包含（缺失记 ungrounded 备注）。</summary>
    [JsonPropertyName("required_grounding")]
    public IReadOnlyList<string> RequiredGrounding { get; init; } = [];

    /// <summary>禁答断言：对话内证据不存在/相悖的事实；命中任一即判幻觉。</summary>
    [JsonPropertyName("forbidden_claims")]
    public IReadOnlyList<string> ForbiddenClaims { get; init; } = [];

    public sealed record Turn
    {
        [JsonPropertyName("role")]
        public string Role { get; init; } = string.Empty;

        [JsonPropertyName("content")]
        public string Content { get; init; } = string.Empty;
    }
}
