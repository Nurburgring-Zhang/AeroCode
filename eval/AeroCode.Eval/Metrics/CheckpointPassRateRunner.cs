using System.Text.Json;
using System.Text.Json.Serialization;
using AeroAgent.Moa.Gateway;

namespace AeroCode.Eval.Metrics;

/// <summary>
/// 指标②困难题 checkpoint 通过率。口径：CritPt 式——困难任务分解为有序 checkpoint，
/// 模型一次作答后逐项核验；判据 = 通过的 checkpoint 数 / checkpoint 总数（跨数据集聚合）。
/// v0 判题为确定性关键词启发式：checkpoint 的 detect_any 关键词命中任一即该项通过。
/// </summary>
public sealed class CheckpointPassRateRunner : IEvalMetricRunner
{
    private static readonly JsonSerializerOptions PayloadOptions = new() { PropertyNameCaseInsensitive = true };

    public string MetricKey => "checkpoint_pass_rate";

    public string DisplayName => "困难题 checkpoint 通过率";

    public string MetricDefinition =>
        "CritPt 式口径：困难任务分解为有序 checkpoint，模型一次作答后逐项核验；" +
        "指标值 = 通过的 checkpoint 数 / checkpoint 总数（跨数据集聚合）。";

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
        var totalCheckpoints = 0;
        var passedCheckpoints = 0;
        var judgedTasks = 0;
        foreach (var task in tasks)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var payload = ParsePayload(task, context.Scrubber, outcomes);
            if (payload is null)
            {
                continue;
            }

            var request = new MoaGatewayExecuteRequest { Query = payload.Prompt };
            var result = await context.Gateway.ExecuteAsync(request, context.CancellationToken);
            if (!result.IsSuccess || result.Value is null)
            {
                outcomes.Add(new EvalTaskOutcome
                {
                    TaskId = task.Id,
                    Status = EvalStatus.Pending,
                    Detail = MultiTurnHallucinationRunner.SingleLine(
                        context.Scrubber.Scrub($"采集失败: {result.Error}")),
                });
                continue;
            }

            var content = result.Value.FinalContent ?? string.Empty;
            judgedTasks++;
            var passedHere = 0;
            var failedNames = new List<string>();
            foreach (var checkpoint in payload.Checkpoints)
            {
                totalCheckpoints++;
                var detectable = checkpoint.DetectAny.Count > 0;
                var passed = detectable && checkpoint.DetectAny.Any(keyword =>
                    !string.IsNullOrEmpty(keyword) && content.Contains(keyword, StringComparison.Ordinal));
                if (passed)
                {
                    passedCheckpoints++;
                    passedHere++;
                }
                else
                {
                    failedNames.Add(checkpoint.Name);
                }
            }

            var detail =
                $"checkpoint {passedHere}/{payload.Checkpoints.Count} 通过" +
                (failedNames.Count == 0
                    ? string.Empty
                    : $"；未过: {string.Join("、", failedNames)}") +
                (result.IsMock ? "；网关标注 mock" : string.Empty);
            outcomes.Add(new EvalTaskOutcome
            {
                TaskId = task.Id,
                Status = EvalStatus.Ok,
                Value = $"{passedHere}/{payload.Checkpoints.Count}",
                Detail = MultiTurnHallucinationRunner.SingleLine(context.Scrubber.Scrub(detail)),
            });
        }

        var allJudged = judgedTasks == tasks.Count && totalCheckpoints > 0;
        return new EvalMetricResult
        {
            MetricKey = MetricKey,
            DisplayName = DisplayName,
            MetricDefinition = MetricDefinition,
            CriteriaSummary = string.IsNullOrWhiteSpace(dataset?.Criteria)
                ? "（未找到该指标的 fixture 数据集）"
                : dataset!.Criteria,
            Status = allJudged ? EvalStatus.Ok : EvalStatus.Pending,
            Value = allJudged
                ? $"{(double)passedCheckpoints / totalCheckpoints:P2}（{passedCheckpoints}/{totalCheckpoints} checkpoint）"
                : EvalStatus.Pending,
            Note = allJudged
                ? null
                : $"仅 {judgedTasks}/{tasks.Count} 任务取得可核验答案（已脱敏）。",
            Tasks = outcomes,
        };
    }

    private static CheckpointPayload? ParsePayload(
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

        CheckpointPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<CheckpointPayload>(task.Payload, PayloadOptions);
        }
        catch (JsonException ex)
        {
            outcomes.Add(new EvalTaskOutcome
            {
                TaskId = task.Id,
                Status = EvalStatus.Failed,
                Detail = MultiTurnHallucinationRunner.SingleLine(
                    scrubber.Scrub($"payload 解析失败: {ex.Message}")),
            });
            return null;
        }

        if (payload is null || string.IsNullOrWhiteSpace(payload.Prompt) || payload.Checkpoints.Count == 0)
        {
            outcomes.Add(new EvalTaskOutcome
            {
                TaskId = task.Id,
                Status = EvalStatus.Failed,
                Detail = "payload 非法：prompt 非空且至少 1 个 checkpoint",
            });
            return null;
        }

        return payload;
    }
}

/// <summary>checkpoint 通过率任务的判题载荷。</summary>
public sealed record CheckpointPayload
{
    [JsonPropertyName("prompt")]
    public string Prompt { get; init; } = string.Empty;

    [JsonPropertyName("checkpoints")]
    public IReadOnlyList<Checkpoint> Checkpoints { get; init; } = [];

    public sealed record Checkpoint
    {
        [JsonPropertyName("index")]
        public int Index { get; init; }

        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        /// <summary>判据关键词（any-of）：最终答案命中任一即该 checkpoint 通过。</summary>
        [JsonPropertyName("detect_any")]
        public IReadOnlyList<string> DetectAny { get; init; } = [];
    }
}
