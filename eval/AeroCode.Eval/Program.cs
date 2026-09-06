using System.Text;

namespace AeroCode.Eval;

/// <summary>
/// AeroCode.Eval CLI。用法：
/// <c>dotnet run --project eval/AeroCode.Eval -- &lt;子命令&gt; [选项]</c>（在仓库根执行）。
/// 子命令：
/// <c>baseline</c> 产出三指标基线报告；<c>after</c> 复用同一采集管线产出对照报告（默认 eval/reports/after.md，
/// 供 compare 使用，绝不写 baseline.md）；<c>compare</c> 比对两份报告并给出 CI 阻断语义的退出码。
/// 退出码：0 = 成功/compare 无回退；2 = 用法错误；3 = 输出路径 fail-closed 拒绝；4 = fixture 校验失败；
/// 5 = compare 判定已实测指标回退（CI 阻断）；6 = compare 报告解析失败（fail-closed，不默认通过）；
/// 1 = 其他运行错误。所有错误出口经 SensitiveScrubber 脱敏。
/// </summary>
public static class Program
{
    private const string UsageText = """
        AeroCode.Eval — C1 三指标评测集 v0 + C3 compare/CI 阻断

        用法: dotnet run --project eval/AeroCode.Eval -- <子命令> [选项]

        子命令:
          baseline    产出三指标基线报告（Markdown）。默认 --out eval/reports/baseline.md
          after       复用同一采集管线产出对照报告。默认 --out eval/reports/after.md
          compare     比对两份报告：任一已实测指标 after 比 before 回退 → 退出码非 0（CI 阻断）

        选项:
          --out <path>       baseline/after 报告输出路径（fail-closed：只允许写 eval/reports/ 下，越界即拒）
          --before <path>    compare 的基线报告路径
          --after <path>     compare 的对照报告路径

        compare 退出码:
          0 = 无回退（PENDING 口径不参与判定；两份都 PENDING → 通过）
          5 = 已实测指标回退（CI 阻断）
          6 = 报告解析失败/字段缺失（fail-closed，不默认通过）

        网关模式:
          设置 AEROCODE_EVAL_GATEWAY_URL / AEROCODE_EVAL_GATEWAY_KEY（凭据只从环境变量读取）→ 经
          MoaGatewayClient 真实采集；未设置 → dry-run，报告结构完整、指标值以 PENDING 占位。
        """;

    public static int Main(string[] args)
    {
        return Run(args, Console.Out, Console.Error);
    }

    /// <summary>可测试入口：返回退出码，stdout/stderr 可注入。</summary>
    public static int Run(string[] args, TextWriter? stdout, TextWriter? stderr)
    {
        var standardOut = stdout ?? TextWriter.Null;
        var standardError = stderr ?? TextWriter.Null;

        if (args.Length == 0)
        {
            standardError.WriteLine(UsageText);
            return 2;
        }

        var subcommand = args[0];
        switch (subcommand)
        {
            case "-h":
            case "--help":
                standardOut.WriteLine(UsageText);
                return 0;
            case "baseline":
            case "after":
            case "compare":
                break;
            default:
                standardError.WriteLine($"[eval] 未知子命令: {args[0]}");
                standardError.WriteLine(UsageText);
                return 2;
        }

        string? outArgument = null;
        string? beforeArgument = null;
        string? afterArgument = null;
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--out" when subcommand != "compare":
                    if (i + 1 >= args.Length)
                    {
                        standardError.WriteLine("[eval] --out 缺少值");
                        return 2;
                    }

                    outArgument = args[++i];
                    break;
                case "--before" when subcommand == "compare":
                    if (i + 1 >= args.Length)
                    {
                        standardError.WriteLine("[eval] --before 缺少值");
                        return 2;
                    }

                    beforeArgument = args[++i];
                    break;
                case "--after" when subcommand == "compare":
                    if (i + 1 >= args.Length)
                    {
                        standardError.WriteLine("[eval] --after 缺少值");
                        return 2;
                    }

                    afterArgument = args[++i];
                    break;
                case "-h":
                case "--help":
                    standardOut.WriteLine(UsageText);
                    return 0;
                default:
                    standardError.WriteLine($"[eval] 未知参数: {args[i]}");
                    standardError.WriteLine(UsageText);
                    return 2;
            }
        }

        // 仓库根：从当前工作目录上溯找 AeroCode.sln（与契约一致：在仓库根执行 dotnet run）。
        var repoRoot = EvalPaths.FindRepoRoot(Environment.CurrentDirectory) ?? Environment.CurrentDirectory;
        var scrubber = new SensitiveScrubber(EvalSecrets.CollectCandidateSecrets());
        try
        {
            if (string.Equals(subcommand, "compare", StringComparison.Ordinal))
            {
                return RunCompare(beforeArgument, afterArgument, scrubber, standardOut, standardError);
            }

            var runner = new BaselineRunner();
            var result = runner.RunAsync(new BaselineRunOptions
            {
                RepoRoot = repoRoot,
                OutPath = outArgument ?? (string.Equals(subcommand, "after", StringComparison.Ordinal)
                    ? EvalPaths.DefaultAfterReportPath(repoRoot)
                    : EvalPaths.DefaultReportPath(repoRoot)),
                // R2 修复 MED-1：after 子命令 fail-closed 拒绝写入保留名 baseline.md（防 --out 覆盖基线报告）；
                // baseline 子命令保持 false（生成基线是合法操作，默认行为不变）。
                DenyBaselineOverwrite = string.Equals(subcommand, "after", StringComparison.Ordinal),
                Gateway = EvalGatewaySettings.FromEnvironment(),
                Log = new ScrubbedTextWriter(standardOut, scrubber),
            }).GetAwaiter().GetResult();
            standardOut.WriteLine($"[eval] 完成: mode={result.Mode} 报告={result.ReportPath}");
            return 0;
        }
        catch (EvalPathPolicyException ex)
        {
            standardError.WriteLine(scrubber.Scrub("[eval] fail-closed 拒绝: " + ex.Message));
            return 3;
        }
        catch (EvalFixtureException ex)
        {
            standardError.WriteLine(scrubber.Scrub("[eval] fixture 校验失败: " + ex.Message));
            return 4;
        }
        catch (Exception ex)
        {
            standardError.WriteLine(scrubber.ScrubException(new Exception("[eval] 运行失败", ex)));
            return 1;
        }
    }

    /// <summary>compare 子命令：退出码 0=通过 / 5=回退阻断 / 6=解析失败 fail-closed。</summary>
    private static int RunCompare(
        string? beforeArgument,
        string? afterArgument,
        SensitiveScrubber scrubber,
        TextWriter standardOut,
        TextWriter standardError)
    {
        if (beforeArgument is null || afterArgument is null)
        {
            standardError.WriteLine("[eval] compare 需要 --before <path> 与 --after <path> 两个报告路径");
            return 2;
        }

        var result = new CompareRunner().Compare(new CompareRunOptions
        {
            BeforePath = beforeArgument,
            AfterPath = afterArgument,
            Log = new ScrubbedTextWriter(standardOut, scrubber),
        });

        foreach (var comparison in result.Comparisons)
        {
            var before = Describe(comparison.Before);
            var after = Describe(comparison.After);
            var delta = comparison.RegressionDelta is { } d
                ? $"（回退幅度 {d.ToString("0.##########", System.Globalization.CultureInfo.InvariantCulture)}）"
                : string.Empty;
            standardOut.WriteLine(scrubber.Scrub(
                $"[eval] compare {comparison.MetricKey}: {comparison.Outcome}{delta} before={before} after={after}"));
        }

        foreach (var error in result.Errors)
        {
            standardError.WriteLine(scrubber.Scrub("[eval] fail-closed: " + error));
        }

        if (result.Errors.Count > 0)
        {
            return 6; // 解析失败/字段缺失：fail-closed，绝不默认通过。
        }

        return result.Passed ? 0 : 5; // 5 = 已实测指标回退（CI 阻断）。
    }

    private static string Describe(EvalReportMetricValue? value) => value switch
    {
        null => "（缺失）",
        { IsPending: true } => EvalStatus.Pending,
        { Value: { } number } => number.ToString("0.##########", System.Globalization.CultureInfo.InvariantCulture),
        _ => "（不可解析）",
    };

    /// <summary>把每行输出过一遍 Scrubber 的 TextWriter 适配（compare/采集进度日志共用）。</summary>
    private sealed class ScrubbedTextWriter : TextWriter
    {
        private readonly TextWriter _inner;
        private readonly SensitiveScrubber _scrubber;

        public ScrubbedTextWriter(TextWriter inner, SensitiveScrubber scrubber)
        {
            _inner = inner;
            _scrubber = scrubber;
        }

        public override Encoding Encoding => _inner.Encoding;

        public override void Write(char value) => _inner.Write(_scrubber.Scrub(value.ToString()));

        public override void Write(string? value) => _inner.Write(_scrubber.Scrub(value));

        public override void WriteLine(string? value) => _inner.WriteLine(_scrubber.Scrub(value));
    }
}
