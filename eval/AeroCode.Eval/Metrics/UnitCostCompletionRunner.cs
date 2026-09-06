using System.Text.Json;
using System.Text.Json.Serialization;
using AeroAgent.Moa.Gateway;

namespace AeroCode.Eval.Metrics;

/// <summary>
/// 指标③单位成本完成率。口径：每任务 token 成本 / 完成质量分。
/// 质量分 = required_keywords 命中比例（v0 确定性关键词启发式，0..1）；
/// 成本 = 网关 execute 返回的 total_cost（USD）与 references tokens；
/// 指标值 = Σcost / Σquality（质量分为 0 的任务记未完成并单列，不进入比值分母）。
/// </summary>
public sealed class UnitCostCompletionRunner : IEvalMetricRunner
{
    private static readonly JsonSerializerOptions PayloadOptions = new() { PropertyNameCaseInsensitive = true };

    public string MetricKey => "unit_cost_completion";

    public string DisplayName => "单位成本完成率";

    public string MetricDefinition =>
        "每任务 token 成本 / 完成质量分：质量分 = required_keywords 命中比例（v0 关键词启发式，0..1），" +
        "成本 = 网关 execute 的 total_cost 与 tokens；指标值 = Σcost / Σquality。";

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
        var judgedTasks = 0;
        var completedTasks = 0;
        double costSum = 0;
        long tokenSum = 0;
        double qualitySum = 0;
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

            judgedTasks++;
            var content = result.Value.FinalContent ?? string.Empty;
            var quality = payload.RequiredKeywords.Count == 0
                ? (content.Length > 0 ? 1.0 : 0.0)
                : (double)payload.RequiredKeywords.Count(keyword =>
                    !string.IsNullOrEmpty(keyword) && content.Contains(keyword, StringComparison.Ordinal)) /
                  payload.RequiredKeywords.Count;
            var cost = result.Value.TotalCost;
            var tokens = result.Value.References.Sum(reference => reference.Tokens);
            var isCompleted = quality > 0;
            if (isCompleted)
            {
                completedTasks++;
                costSum += cost;
                tokenSum += tokens;
                qualitySum += quality;
            }

            var detail =
                $"质量分 {quality:P0}（命中 {Round(quality * payload.RequiredKeywords.Count)}/{payload.RequiredKeywords.Count}）；" +
                $"cost {cost:0.000000} USD；tokens {tokens}" +
                (isCompleted ? string.Empty : "；质量分为 0，记未完成") +
                (result.IsMock ? "；网关标注 mock" : string.Empty);
            outcomes.Add(new EvalTaskOutcome
            {
                TaskId = task.Id,
                Status = EvalStatus.Ok,
                Value = isCompleted ? $"quality={quality:P0}; cost={cost:0.000000}USD; tokens={tokens}" : "NOT_COMPLETED",
                Detail = MultiTurnHallucinationRunner.SingleLine(context.Scrubber.Scrub(detail)),
            });
        }

        var allJudged = judgedTasks == tasks.Count && completedTasks > 0;
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
                ? $"Σcost/Σquality = {costSum / qualitySum:0.000000} USD/分；Σtokens/Σquality = {(double)tokenSum / qualitySum:0} tok/分"
                : EvalStatus.Pending,
            Note = allJudged
                ? null
                : $"仅 {completedTasks}/{tasks.Count} 任务计入（质量分为 0 或未取得答案的任务单列，已脱敏）。",
            Tasks = outcomes,
        };
    }

    private static int Round(double value) => (int)Math.Round(value, MidpointRounding.AwayFromZero);

    private static CostPayload? ParsePayload(
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

        CostPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<CostPayload>(task.Payload, PayloadOptions);
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

        if (payload is null || string.IsNullOrWhiteSpace(payload.Prompt))
        {
            outcomes.Add(new EvalTaskOutcome
            {
                TaskId = task.Id,
                Status = EvalStatus.Failed,
                Detail = "payload 非法：prompt 非空",
            });
            return null;
        }

        return payload;
    }
}

/// <summary>单位成本完成率任务的判题载荷。</summary>
public sealed record CostPayload
{
    [JsonPropertyName("prompt")]
    public string Prompt { get; init; } = string.Empty;

    /// <summary>质量判据关键词：质量分 = 命中数 / 总数（v0 启发式）。</summary>
    [JsonPropertyName("required_keywords")]
    public IReadOnlyList<string> RequiredKeywords { get; init; } = [];
}
