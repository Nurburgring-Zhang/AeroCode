// capability 矩阵 v1 生成器测试：离线声明式基线（绝不伪造 Supported）+ runtime-probe 三态聚合（R3-δ）
// + 落盘（R3-δ 起写临时目录——eval/reports 现有 v1 基线文件内容不得被测试重写）。
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AeroCode.AI.Capabilities;
using AeroCode.AI.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AeroCode.Tests.Ai;

public class CapabilityMatrixTests
{
    /// <summary>从测试输出目录向上找到仓库根（含 AeroCode.sln 的目录）。</summary>
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AeroCode.sln")))
        {
            dir = dir.Parent;
        }
        if (dir is null) throw new InvalidOperationException("repo root (AeroCode.sln) not found from test base directory");
        return dir.FullName;
    }

    /// <summary>R3-δ 三态矩阵测试替身：按 (providerId, capability) 脚本化返回并记录调用。</summary>
    internal sealed class ScriptedProbe : IVendorCapabilityProbe
    {
        private readonly Func<string, VendorCapability, VendorCapabilityState> _resolver;

        public ScriptedProbe(Func<string, VendorCapability, VendorCapabilityState> resolver) => _resolver = resolver;

        /// <summary>每次探测调用前触发的钩子（取消时序测试用）。</summary>
        public Action? ResolverHook { get; set; }

        public System.Collections.Generic.List<(string ProviderId, VendorCapability Capability, CancellationToken Ct)> Calls { get; } = new();

        public Task<VendorCapabilityState> ProbeAsync(string providerId, VendorCapability capability, CancellationToken ct = default)
        {
            ResolverHook?.Invoke();
            Calls.Add((providerId, capability, ct));
            return Task.FromResult(_resolver(providerId, capability));
        }

        /// <summary>与真实 VendorCapabilityProbe 同源语义：G×StructuredOutputs 有文档化降级自述；其余 null。</summary>
        public string? DescribeDowngrade(string providerId, VendorCapability capability) =>
            providerId.Equals("G", StringComparison.OrdinalIgnoreCase) && capability == VendorCapability.StructuredOutputs
                ? "documented: gemini lacks strict json_schema response_format; degradation path = prompt-JSON + local validation"
                : null;
    }

    [Fact]
    public void BuildBaseline_NeverFabricatesSupported()
    {
        var matrix = CapabilityMatrixGenerator.BuildBaseline();
        Assert.Equal(CapabilityMatrixGenerator.OfflineMode, matrix.Mode);
        Assert.Equal(12, matrix.Cells.Count); // 3 provider × 4 capability
        Assert.All(matrix.Cells, c => Assert.NotEqual(nameof(VendorCapabilityState.Supported), c.State));
        // G × StructuredOutputs = 文档化降级（prompt-JSON + 校验）。
        var gSo = Assert.Single(matrix.Cells, c => c.ProviderId == "G" && c.Capability == "StructuredOutputs");
        Assert.Equal(nameof(VendorCapabilityState.Downgraded), gSo.State);
        Assert.Equal("documented-degradation", gSo.Method);
        // R3-δ：Downgraded 必带 documented reason。
        Assert.False(string.IsNullOrWhiteSpace(gSo.Reason));
        // 其余全部 Missing（离线声明式基线），且无证据（fail-closed，绝不伪造 evidence）。
        var missings = matrix.Cells.Where(c => !ReferenceEquals(c, gSo)).ToList();
        Assert.All(missings, c =>
        {
            Assert.Equal(nameof(VendorCapabilityState.Missing), c.State);
            Assert.Equal("offline-baseline", c.Method);
            Assert.Null(c.Evidence);
            Assert.Null(c.Reason);
        });
        // 时间戳可解析。
        Assert.True(DateTimeOffset.TryParse(matrix.GeneratedAtUtc, out _));
    }

    [Fact]
    public void BuildBaseline_CustomProviderIds_ExpandFullGrid()
    {
        var matrix = CapabilityMatrixGenerator.BuildBaseline(new[] { "deepseek", "claude" });
        Assert.Equal(8, matrix.Cells.Count);
        Assert.All(matrix.Cells, c => Assert.NotEqual(nameof(VendorCapabilityState.Supported), c.State));
    }

    [Fact]
    public void Render_OfflineBaseline_JsonAndMarkdownContainDeclarations()
    {
        var matrix = CapabilityMatrixGenerator.BuildBaseline();
        var json = CapabilityMatrixGenerator.ToJson(matrix);
        var md = CapabilityMatrixGenerator.ToMarkdown(matrix);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(CapabilityMatrixGenerator.OfflineMode, doc.RootElement.GetProperty("mode").GetString());
        Assert.Equal(12, doc.RootElement.GetProperty("cells").GetArrayLength());

        // 绝不伪造 Supported：没有任何 cell 的 state 为 Supported。
        Assert.All(matrix.Cells, c => Assert.NotEqual(nameof(VendorCapabilityState.Supported), c.State));
        using var mdDoc = JsonDocument.Parse(CapabilityMatrixGenerator.ToJson(matrix));
        Assert.All(mdDoc.RootElement.GetProperty("cells").EnumerateArray(),
            cell => Assert.NotEqual("Supported", cell.GetProperty("state").GetString()));

        Assert.Contains("GeneratedAtUtc", md);
        Assert.Contains(CapabilityMatrixGenerator.OfflineMode, md);
        Assert.Contains("| ProviderId |", md);
        Assert.Contains("| A |", md);
        Assert.Contains("| G |", md);
        Assert.Contains("| O |", md);
        Assert.Contains("Downgraded (documented-degradation)", md);
        // 表格单元格不出现 Supported 状态渲染（图例文字除外）。
        Assert.DoesNotContain("Supported (", md);
        // R3-δ：离线基线的 Downgraded documented reason 落 md 证据附录。
        Assert.Contains("reason →", md);
    }

    [Fact]
    public async Task BuildWithProbe_UnknownProvider_AllMissingRuntimeProbe()
    {
        var probe = new VendorCapabilityProbe(_ => null, logger: NullLogger.Instance);
        var matrix = await CapabilityMatrixGenerator.BuildWithProbeAsync(probe, generatedAtUtc: DateTimeOffset.UnixEpoch);
        Assert.Equal(CapabilityMatrixGenerator.LiveMode, matrix.Mode);
        Assert.Equal(12, matrix.Cells.Count);
        Assert.All(matrix.Cells, c =>
        {
            // probe 失败一律 Missing（fail-closed），矩阵不做任何升级，且无 evidence/reason。
            Assert.Equal(nameof(VendorCapabilityState.Missing), c.State);
            Assert.Equal("runtime-probe", c.Method);
            Assert.Null(c.Evidence);
            Assert.Null(c.Reason);
        });
        using var doc = JsonDocument.Parse(CapabilityMatrixGenerator.ToJson(matrix));
        Assert.All(doc.RootElement.GetProperty("cells").EnumerateArray(),
            cell => Assert.Equal("Missing", cell.GetProperty("state").GetString()));
    }

    // ---------------- R3-δ：三态判定 + 证据纪律 ----------------

    [Fact]
    public async Task BuildWithProbe_TriState_SupportedCarriesEvidence_DowngradedCarriesReason_MissingCarriesNothing()
    {
        var probe = new ScriptedProbe((id, cap) => (id, cap) switch
        {
            ("O", VendorCapability.EffortTiers) => VendorCapabilityState.Supported,
            ("G", VendorCapability.StructuredOutputs) => VendorCapabilityState.Downgraded,
            _ => VendorCapabilityState.Missing,
        });
        var generatedAt = DateTimeOffset.Parse("2026-02-07T10:00:00Z");

        var matrix = await CapabilityMatrixGenerator.BuildWithProbeAsync(
            probe, generatedAtUtc: generatedAt, probeId: "scripted-probe#1");

        Assert.Equal(CapabilityMatrixGenerator.LiveMode, matrix.Mode);
        Assert.Equal(12, matrix.Cells.Count);

        // Supported：带 evidence = probe 标识 + 矩阵时间戳（可解析）。
        var supported = Assert.Single(matrix.Cells, c => c.State == nameof(VendorCapabilityState.Supported));
        Assert.Equal("O", supported.ProviderId);
        Assert.Equal("EffortTiers", supported.Capability);
        Assert.NotNull(supported.Evidence);
        Assert.Contains("probe=scripted-probe#1", supported.Evidence);
        Assert.Contains($"at={generatedAt:O}", supported.Evidence);
        Assert.Null(supported.Reason);

        // Downgraded：带 documented reason（来自 probe 自述）。
        var downgraded = Assert.Single(matrix.Cells, c => c.State == nameof(VendorCapabilityState.Downgraded));
        Assert.Equal("G", downgraded.ProviderId);
        Assert.Equal("StructuredOutputs", downgraded.Capability);
        Assert.False(string.IsNullOrWhiteSpace(downgraded.Reason));
        Assert.Null(downgraded.Evidence);

        // Missing：无证据（fail-closed）。
        Assert.Equal(10, matrix.Cells.Count(c => c.State == nameof(VendorCapabilityState.Missing)));
        Assert.All(matrix.Cells.Where(c => c.State == nameof(VendorCapabilityState.Missing)), c =>
        {
            Assert.Null(c.Evidence);
            Assert.Null(c.Reason);
        });

        // JSON 逐字段：evidence/reason 只在对应格出现。
        using var doc = JsonDocument.Parse(CapabilityMatrixGenerator.ToJson(matrix));
        var cells = doc.RootElement.GetProperty("cells");
        Assert.Equal(12, cells.GetArrayLength());
        var jsonSupported = cells.EnumerateArray().Single(c => c.GetProperty("state").GetString() == "Supported");
        Assert.Contains("scripted-probe#1", jsonSupported.GetProperty("evidence").GetString());
        var jsonMissing = cells.EnumerateArray().First(c => c.GetProperty("state").GetString() == "Missing");
        Assert.False(jsonMissing.TryGetProperty("evidence", out _));

        // md 证据附录：evidence 与 reason 逐行落盘。
        var md = CapabilityMatrixGenerator.ToMarkdown(matrix);
        Assert.Contains("## Evidence / documented reasons", md);
        Assert.Contains("evidence → probe=scripted-probe#1", md);
        Assert.Contains("reason →", md);
    }

    [Fact]
    public async Task BuildWithProbe_ProbeIdDefaultsToProbeTypeName()
    {
        var probe = new ScriptedProbe((_, _) => VendorCapabilityState.Supported);
        var matrix = await CapabilityMatrixGenerator.BuildWithProbeAsync(probe, generatedAtUtc: DateTimeOffset.UnixEpoch);
        Assert.All(matrix.Cells, c =>
        {
            Assert.Equal(nameof(VendorCapabilityState.Supported), c.State);
            Assert.Contains($"probe={nameof(ScriptedProbe)}", c.Evidence);
        });
    }

    [Fact]
    public async Task BuildWithProbe_CancellationToken_ReachesProbe_AndStopsGrid()
    {
        using var cts = new CancellationTokenSource();
        var probe = new ScriptedProbe((_, _) => VendorCapabilityState.Supported);
        // 第 3 次调用时取消：令牌必须透传到 probe，且矩阵构建立即中止。
        var call = 0;
        probe.ResolverHook = () =>
        {
            if (Interlocked.Increment(ref call) == 3) cts.Cancel();
        };

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            CapabilityMatrixGenerator.BuildWithProbeAsync(probe, ct: cts.Token));

        Assert.True(probe.Calls.Count >= 3);
        Assert.All(probe.Calls, c => Assert.Equal(cts.Token, c.Ct));
    }

    [Fact]
    public void Write_OfflineBaseline_ProducesExactlyTheTwoReportFiles_InTempDirectory()
    {
        // R3-δ：落盘测试改写临时目录——eval/reports/ 现有 v1 基线文件内容不得被测试重写
        //（离线语义与 v1 兼容，产物文件名与结构不变）。
        var reportsDir = Path.Combine(Path.GetTempPath(), $"capmatrix_{Guid.NewGuid():N}");
        try
        {
            var matrix = CapabilityMatrixGenerator.BuildBaseline(generatedAtUtc: DateTimeOffset.UtcNow);
            CapabilityMatrixGenerator.Write(matrix, reportsDir);

            var jsonPath = Path.Combine(reportsDir, "capability-matrix-v1.json");
            var mdPath = Path.Combine(reportsDir, "capability-matrix-v1.md");
            Assert.True(File.Exists(jsonPath), $"{jsonPath} should exist");
            Assert.True(File.Exists(mdPath), $"{mdPath} should exist");
            Assert.Equal(2, Directory.GetFiles(reportsDir).Length);

            var json = File.ReadAllText(jsonPath);
            using var doc = JsonDocument.Parse(json);
            Assert.Equal(CapabilityMatrixGenerator.OfflineMode, doc.RootElement.GetProperty("mode").GetString());
            // 落盘产物绝不伪造 Supported：逐 cell 断言 state。
            Assert.All(doc.RootElement.GetProperty("cells").EnumerateArray(),
                cell => Assert.NotEqual("Supported", cell.GetProperty("state").GetString()));
            var md = File.ReadAllText(mdPath);
            Assert.DoesNotContain("Supported (", md);
            // v1 结构延续：文件名 / 表头 / 图例逐项保留。
            Assert.Contains("| ProviderId |", md);
            Assert.Contains("State legend:", md);
            // R4 δ-5：EffortTiers 行判定依据补注（配置/模型 id，非运行时验证）。
            Assert.Contains("EffortTiers note:", md);
            Assert.Contains("not runtime-verified", md);
        }
        finally
        {
            if (Directory.Exists(reportsDir))
            {
                Directory.Delete(reportsDir, recursive: true);
            }
        }
    }

    [Fact]
    public void RepoBaseline_StillOfflineMode_NeverFabricated()
    {
        // 契约 D-MATRIX：eval/reports 现有 v1 基线保持离线声明（本波次不重写其内容）；
        // 防御性断言：仓库内基线文件的 mode 必须仍是 offline-declarative-baseline。
        var jsonPath = Path.Combine(FindRepoRoot(), "eval", "reports", "capability-matrix-v1.json");
        Assert.True(File.Exists(jsonPath), $"{jsonPath} should exist");
        using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
        Assert.Equal(CapabilityMatrixGenerator.OfflineMode, doc.RootElement.GetProperty("mode").GetString());
        Assert.All(doc.RootElement.GetProperty("cells").EnumerateArray(),
            cell => Assert.NotEqual("Supported", cell.GetProperty("state").GetString()));
    }
}
