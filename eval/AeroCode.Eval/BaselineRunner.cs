using System.Text;
using AeroAgent.Moa.Gateway;

namespace AeroCode.Eval;

/// <summary>基线运行选项。</summary>
public sealed record BaselineRunOptions
{
    /// <summary>仓库根（含 AeroCode.sln 的目录）。</summary>
    public required string RepoRoot { get; init; }

    /// <summary>fixture 目录；默认 = RepoRoot/eval/datasets。</summary>
    public string? DatasetsDir { get; init; }

    /// <summary>报告输出路径；默认 = RepoRoot/eval/reports/baseline.md。受 fail-closed 策略约束。</summary>
    public string? OutPath { get; init; }

    /// <summary>
    /// 额外允许的输出根目录（在默认 eval/reports/ 之外显式授权；测试/脚本注入用）。
    /// CLI 路径默认不追加——越界即拒。
    /// </summary>
    public IReadOnlyList<string> AdditionalAllowedOutputRoots { get; init; } = [];

    /// <summary>
    /// R2 修复 MED-1：拒绝把报告写到保留名 baseline.md（fail-closed）。
    /// after 子命令设 true（防 --out 覆盖基线报告）；baseline 子命令保持 false（生成基线是合法操作）。
    /// </summary>
    public bool DenyBaselineOverwrite { get; init; }

    /// <summary>null = dry-run（无网关凭据）。</summary>
    public EvalGatewaySettings? Gateway { get; init; }

    /// <summary>进度日志出口（每行经脱敏）；null = 不输出。</summary>
    public TextWriter? Log { get; init; }
}

/// <summary>一次基线运行的产物。</summary>
public sealed record BaselineRunResult
{
    public required string ReportPath { get; init; }

    /// <summary>dry-run / gateway。</summary>
    public required string Mode { get; init; }

    public required IReadOnlyList<EvalMetricResult> Metrics { get; init; }
}

/// <summary>
/// baseline 子命令执行器：fail-closed 校验 → fixture 加载 → 网关探活（或 dry-run）→
/// 三指标采集/占位 → 脱敏渲染 Markdown → 只写允许路径。
/// 网关凭据只从环境变量读取（AEROCODE_EVAL_*）；报告/日志/异常出口统一过 <see cref="SensitiveScrubber"/>。
/// </summary>
public sealed class BaselineRunner
{
    /// <summary>三指标的固定口径键与顺序。</summary>
    public static readonly string[] MetricKeys =
    [
        "multi_turn_hallucination_rate",
        "checkpoint_pass_rate",
        "unit_cost_completion",
    ];

    public async Task<BaselineRunResult> RunAsync(BaselineRunOptions options, CancellationToken cancellationToken = default)
    {
        var repoRoot = Path.GetFullPath(options.RepoRoot);
        var datasetsDir = options.DatasetsDir ?? EvalPaths.DefaultDatasetsDir(repoRoot);
        var requestedOut = options.OutPath ?? EvalPaths.DefaultReportPath(repoRoot);

        // fail-closed 第一闸：任何文件/目录创建之前先做路径裁决。
        var allowedRoots = new List<string> { EvalPaths.DefaultReportsDir(repoRoot) };
        allowedRoots.AddRange(options.AdditionalAllowedOutputRoots);
        var resolvedOut = OutputPathGuard.ValidateWritablePath(
            requestedOut, allowedRoots, options.DenyBaselineOverwrite);

        var candidateSecrets = EvalSecrets.CollectCandidateSecrets().ToList();
        if (options.Gateway?.ApiKey is { Length: > 0 } gatewayKey)
        {
            candidateSecrets.Add(gatewayKey);
        }

        var scrubber = new SensitiveScrubber(candidateSecrets);
        Log(options, scrubber, $"baseline 启动: repoRoot={repoRoot}");
        Log(options, scrubber, options.Gateway is null
            ? $"未检测到 {EvalSecrets.GatewayKeyVariable}，进入 dry-run（指标值 PENDING）"
            : $"检测到 {EvalSecrets.GatewayKeyVariable}（值不落盘），网关={options.Gateway.BaseUrl}");

        var datasets = FixtureLoader.LoadAll(datasetsDir);
        Log(options, scrubber, $"fixture 加载完成: {datasets.Count} 份数据集（版本 {FixtureLoader.ExpectedVersion}）");

        MoaGatewayClient? client = null;
        string mode;
        string modeReason;
        string? gatewayUnavailableReason = null;
        if (options.Gateway is null)
        {
            mode = "dry-run";
            modeReason =
                $"未检测到 {EvalSecrets.GatewayKeyVariable}（网关凭据只从 AEROCODE_EVAL_* 环境变量读取），" +
                "本次为管线 dry-run，所有指标值以 PENDING 占位，报告结构完整。";
        }
        else
        {
            client = new MoaGatewayClient(new MoaGatewayClientOptions
            {
                BaseUrl = options.Gateway.BaseUrl,
                ApiKey = options.Gateway.ApiKey,
            });
            var health = await client.HealthAsync(cancellationToken);
            if (health.IsSuccess)
            {
                mode = "gateway";
                modeReason =
                    $"网关探活成功（{options.Gateway.BaseUrl}，版本 {health.Value?.Version}），" +
                    "指标经 /v1/moa/execute 真实采集。";
                Log(options, scrubber, "网关探活成功，进入真实采集");
            }
            else
            {
                mode = "dry-run";
                gatewayUnavailableReason =
                    $"网关探活失败: {scrubber.Scrub(health.Error ?? "未知原因")}；" +
                    "降级为 PENDING 报告，不伪造数值。";
                modeReason = $"配置了网关凭据但 {gatewayUnavailableReason}";
                Log(options, scrubber, gatewayUnavailableReason);
            }
        }

        try
        {
            var context = new EvalRunContext
            {
                Scrubber = scrubber,
                Gateway = string.Equals(mode, "gateway", StringComparison.Ordinal) ? client : null,
                GatewayUnavailableReason = gatewayUnavailableReason,
                CancellationToken = cancellationToken,
            };
            IEvalMetricRunner[] runners =
            [
                new Metrics.MultiTurnHallucinationRunner(),
                new Metrics.CheckpointPassRateRunner(),
                new Metrics.UnitCostCompletionRunner(),
            ];
            var metrics = new List<EvalMetricResult>();
            foreach (var runner in runners)
            {
                var dataset = datasets.FirstOrDefault(d =>
                    string.Equals(d.Metric, runner.MetricKey, StringComparison.Ordinal));
                var result = await runner.EvaluateAsync(dataset, context);
                metrics.Add(result);
                Log(options, scrubber, $"指标 {runner.MetricKey}: 状态={result.Status} 值={result.Value}");
            }

            var markdown = BaselineReportRenderer.Render(
                metrics, datasets, mode, modeReason, resolvedOut, DateTimeOffset.UtcNow);
            markdown = scrubber.Scrub(markdown); // 出口统一脱敏（防御纵深）
            var directory = Path.GetDirectoryName(resolvedOut);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(resolvedOut, markdown, cancellationToken);
            Log(options, scrubber, $"报告已写入: {resolvedOut}");
            return new BaselineRunResult
            {
                ReportPath = resolvedOut,
                Mode = mode,
                Metrics = metrics,
            };
        }
        finally
        {
            client?.Dispose();
        }
    }

    private static void Log(BaselineRunOptions options, SensitiveScrubber scrubber, string message)
    {
        if (options.Log is not null)
        {
            options.Log.WriteLine(scrubber.Scrub("[eval] " + message));
        }
    }
}

/// <summary>Markdown 报告渲染（三指标段结构固定完整；值位置在 dry-run 为 PENDING）。</summary>
internal static class BaselineReportRenderer
{
    public static string Render(
        IReadOnlyList<EvalMetricResult> metrics,
        IReadOnlyList<EvalFixtureDataset> datasets,
        string mode,
        string modeReason,
        string resolvedOutPath,
        DateTimeOffset generatedAtUtc)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# AeroCode Eval · 三指标基线报告（fixture v0）");
        builder.AppendLine();
        builder.AppendLine($"- 生成时间 (UTC): {generatedAtUtc:yyyy-MM-dd'T'HH:mm:ss'Z'}");
        builder.AppendLine($"- 运行模式: **{mode}** — {modeReason}");
        builder.AppendLine($"- 输出策略: fail-closed（只允许写 eval/reports/ 或显式授权目录）；本次报告路径: {resolvedOutPath}");
        builder.AppendLine("- 网关凭据来源: 环境变量 AEROCODE_EVAL_GATEWAY_KEY（只读入内存，不落盘、不进报告）");
        builder.AppendLine();
        builder.AppendLine("## 数据集清单");
        builder.AppendLine();
        builder.AppendLine("| dataset_id | metric（口径标识） | version | 任务数 |");
        builder.AppendLine("|---|---|---|---|");
        foreach (var dataset in datasets)
        {
            builder.AppendLine(
                $"| {dataset.DatasetId} | {dataset.Metric} | {dataset.Version} | {dataset.Tasks.Count} |");
        }

        builder.AppendLine();
        for (var i = 0; i < metrics.Count; i++)
        {
            var metric = metrics[i];
            builder.AppendLine($"## {i + 1}. {metric.DisplayName} ({metric.MetricKey})");
            builder.AppendLine();
            builder.AppendLine($"- 口径: {metric.MetricDefinition}");
            builder.AppendLine($"- 判据: {metric.CriteriaSummary}");
            builder.AppendLine($"- 值: **{metric.Value}**" + (metric.Note is null ? string.Empty : $" — {metric.Note}"));
            if (metric.Tasks.Count == 0)
            {
                builder.AppendLine("- 任务明细: （无任务）");
                builder.AppendLine();
                continue;
            }

            var dataset = datasets.FirstOrDefault(d =>
                string.Equals(d.Metric, metric.MetricKey, StringComparison.Ordinal));
            builder.AppendLine();
            builder.AppendLine("| id | 判据 | 状态 | 结果/说明 |");
            builder.AppendLine("|---|---|---|---|");
            AppendTaskRows(builder, metric, dataset?.Tasks ?? []);
            builder.AppendLine();
        }

        builder.AppendLine("## 附录");
        builder.AppendLine();
        builder.AppendLine(
            "- 评判器声明: v0 全部为确定性关键词启发式判据（冻结于 eval/datasets/*.json 的 criteria 字段），不含 LLM 评审。");
        builder.AppendLine(
            "- 安全声明: 本报告与运行日志经 SensitiveScrubber 统一脱敏（sk-* 形态密钥、Bearer 令牌、" +
            "AEROCODE_EVAL_* / MOA_GATEWAY_KEY 凭据值 → [REDACTED]）；网关凭据只从环境变量读取。");
        builder.AppendLine(
            "- 采集模式说明: 无网关凭据 → dry-run（PENDING）；网关探活失败 → PENDING + 脱敏原因；探活成功 → 经 /v1/moa/execute 真实采集。");
        var wiredKeys = metrics.Select(m => m.MetricKey).ToHashSet(StringComparer.Ordinal);
        var unwired = datasets
            .Where(d => !wiredKeys.Contains(d.Metric))
            .Select(d => d.DatasetId)
            .ToList();
        if (unwired.Count > 0)
        {
            builder.AppendLine($"- 未接入 runner 的数据集: {string.Join("、", unwired)}");
        }

        return builder.ToString();
    }

    private static void AppendTaskRows(StringBuilder builder, EvalMetricResult metric, IReadOnlyList<EvalFixtureTask> tasks)
    {
        foreach (var outcome in metric.Tasks)
        {
            var criteria = tasks.FirstOrDefault(t => string.Equals(t.Id, outcome.TaskId, StringComparison.Ordinal))?.Criteria ?? string.Empty;
            var result = outcome.Value is null ? outcome.Detail ?? string.Empty : $"{outcome.Value} — {outcome.Detail ?? string.Empty}";
            builder.AppendLine($"| {outcome.TaskId} | {SingleLine(criteria)} | {outcome.Status} | {SingleLine(result)} |");
        }
    }

    private static string SingleLine(string text) =>
        text.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();
}
