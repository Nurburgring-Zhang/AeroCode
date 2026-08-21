// RsiEngine tests: mutation, real held-out evaluation, gate apply/rollback semantics,
// L1 correction rules, L2 memory promotion, creative-tier approval gate.
// Includes ACCEPTANCE E2E ② (one full RSI self-tuning round, both gate outcomes).
using AeroAgent.Autonomy.Analysis;
using AeroAgent.Autonomy.Data;
using AeroAgent.Autonomy.Learning;
using AeroAgent.Autonomy.Retrospective;
using AeroAgent.Conversation.Models;
using AeroCode.Skills.AutoCreate;
using AeroCode.Skills.Registry;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AeroCode.Tests.Autonomy;

/// <summary>Test double: approval authority that always approves (hand-written ISkillApproval).</summary>
internal sealed class AllowAllSkillApproval : ISkillApproval
{
    public int ApproveCalls { get; private set; }
    public Task<bool> ApproveAsync(AutoCreateCandidate candidate, CancellationToken ct = default)
    {
        ApproveCalls++;
        return Task.FromResult(true);
    }
}

public sealed class LearningRsiTests : IDisposable
{
    private readonly LearningEnv _env = new();
    private readonly LearningDbContext _rsiDb;
    private readonly RsiEngine _rsi;

    public LearningRsiTests()
    {
        _rsiDb = _env.NewLearningDb();
        _rsi = new RsiEngine(_rsiDb, _env.LearningPaths, _env.Experiences);
    }

    public void Dispose()
    {
        _rsi.Dispose();
        _rsiDb.Dispose();
        _env.Dispose();
    }

    private static RsiHeldOutSample ClarifySample(double ambiguity, bool helped) => new()
    {
        Type = TaskType.Code,
        Complexity = 2,
        AmbiguityScore = ambiguity,
        ClarificationHelped = helped,
        Provenance = "test-constructed",
    };

    private static RsiHeldOutSample StrategySample(int complexity, OrchestrationStrategy label) => new()
    {
        Type = TaskType.Code,
        Complexity = complexity,
        AmbiguityScore = 0.0,
        SuccessfulStrategy = label,
        Provenance = "test-constructed",
    };

    // ============ 变异与评估 ============

    [Fact]
    public void GenerateCandidates_DefaultBaseline_ProducesEightMutations()
    {
        var candidates = RsiEngine.GenerateCandidates(new RsiParameterSet());

        Assert.Equal(8, candidates.Count);
        Assert.Equal(
            new[] { 0.35, 0.4, 0.5, 0.55 },
            candidates.Where(c => c.Label.StartsWith("clarification", StringComparison.Ordinal))
                .Select(c => c.ClarificationThreshold).OrderBy(v => v).ToArray());
        Assert.Equal(
            new[] { 3.0, 3.5, 4.5, 5.0 },
            candidates.Where(c => c.Label.StartsWith("decompose", StringComparison.Ordinal))
                .Select(c => c.DecomposeComplexityThreshold).OrderBy(v => v).ToArray());
        // 单维变异：每个候选只改一个维度。
        Assert.All(candidates, c =>
            Assert.True(
                (c.ClarificationThreshold == 0.45) != (c.DecomposeComplexityThreshold == 4.0)));
    }

    [Fact]
    public void GenerateCandidates_NearCeiling_ClampsAndDeduplicates()
    {
        var candidates = RsiEngine.GenerateCandidates(new RsiParameterSet { ClarificationThreshold = 0.97 });

        var clarifValues = candidates
            .Where(c => c.Label.StartsWith("clarification", StringComparison.Ordinal))
            .Select(c => c.ClarificationThreshold).OrderBy(v => v).ToArray();
        // +0.05 与 +0.1 都被钳制到 0.95 → 去重后只剩一个；-0.05→0.92，-0.1→0.87。
        Assert.Equal(new[] { 0.87, 0.92, 0.95 }, clarifValues);
        Assert.All(candidates, c =>
        {
            Assert.InRange(c.ClarificationThreshold, RsiEngine.ClarificationThresholdMin, RsiEngine.ClarificationThresholdMax);
            Assert.InRange(c.DecomposeComplexityThreshold, RsiEngine.DecomposeThresholdMin, RsiEngine.DecomposeThresholdMax);
        });
    }

    [Fact]
    public void Evaluate_ComputesRealAccuracy_OverLabeledDimensionsOnly()
    {
        var baseline = new RsiParameterSet(); // clarif 0.45
        var samples = new[]
        {
            ClarifySample(0.5, helped: true),   // 0.5 ≥ 0.45 → triggered=true ✓
            ClarifySample(0.4, helped: false),  // 0.4 < 0.45 → false ✓
            ClarifySample(0.4, helped: true),   // predicted false ✗
            StrategySample(4, OrchestrationStrategy.Decompose), // 4 ≥ 4 ✓
            StrategySample(3, OrchestrationStrategy.Single),    // 3 < 4 ✓
        };

        Assert.Equal(0.8, _rsi.EvaluateParameters(baseline, samples), 5);
    }

    [Fact]
    public void Evaluate_EmptyOrUnlabeledSamples_Throws_Honestly()
    {
        var baseline = new RsiParameterSet();
        Assert.Throws<ArgumentException>(() => _rsi.EvaluateParameters(baseline, Array.Empty<RsiHeldOutSample>()));
        Assert.Throws<ArgumentException>(() => _rsi.EvaluateParameters(baseline, new[]
        {
            new RsiHeldOutSample { Type = TaskType.Code, Complexity = 2, AmbiguityScore = 0.5 },
        }));
    }

    [Fact]
    public async Task LoadActiveParameters_DefaultsWhenNoFile()
    {
        var active = await _rsi.LoadActiveParametersAsync();
        Assert.Equal(4.0, active.DecomposeComplexityThreshold);
        Assert.Equal(0.45, active.ClarificationThreshold);
        Assert.False(File.Exists(_env.LearningPaths.ActiveParameterFile));
    }

    // ============ 验收 E2E ②：过 gate 生效 + 快照可回退 ============

    [Fact]
    public async Task AcceptanceE2E_RsiRound_CandidatePassesGate_Applied_SnapshotRollback()
    {
        // 构造 held-out 集：歧义 0.47 的样本被标注"澄清无帮助"——
        // 基线 0.45 会误触发澄清（5 条全错），阈值 0.5 的候选全部纠正。
        var samples = new List<RsiHeldOutSample>();
        for (var i = 0; i < 5; i++) samples.Add(ClarifySample(0.47, helped: false));
        for (var i = 0; i < 5; i++) samples.Add(ClarifySample(0.6, helped: true));
        for (var i = 0; i < 5; i++) samples.Add(ClarifySample(0.3, helped: false));

        var round = await _rsi.RunRoundAsync(samples, new RsiRoundOptions { MinAccuracyFloor = 0.6 });

        // 基线真实评估：10/15。
        Assert.Equal(10.0 / 15.0, round.BaselineAccuracy, 5);
        Assert.True(round.Applied, round.Reason);
        Assert.NotNull(round.AppliedCandidate);
        Assert.Equal(0.5, round.AppliedCandidate!.Parameters.ClarificationThreshold, 5);
        Assert.True(round.AppliedCandidate.Accuracy > round.BaselineAccuracy);

        // 参数真实变更：当前参数文件落盘且内容是新参数。
        Assert.True(File.Exists(_env.LearningPaths.ActiveParameterFile));
        var active = await _rsi.LoadActiveParametersAsync();
        Assert.Equal(0.5, active.ClarificationThreshold, 5);
        Assert.Equal(4.0, active.DecomposeComplexityThreshold, 5);

        // 旧参数快照真实落盘 JSON，内容是旧参数（回退的来源）。
        Assert.NotNull(round.SnapshotPath);
        Assert.True(File.Exists(round.SnapshotPath));
        var snapshot = System.Text.Json.JsonSerializer.Deserialize<RsiEngine.RsiSnapshotFile>(
            File.ReadAllText(round.SnapshotPath!));
        Assert.NotNull(snapshot);
        Assert.Equal(0.45, snapshot!.Parameters.ClarificationThreshold, 5);

        // 全程留痕：rsi-log.md 含轮次、逐候选评估与决策。
        Assert.True(File.Exists(round.LogPath));
        var log = await File.ReadAllTextAsync(round.LogPath);
        Assert.Contains(round.RoundId, log);
        Assert.Contains("PASS", log);
        Assert.Contains("决策: 应用候选", log);
        Assert.Contains("基线", log);

        // 回退真实恢复旧参数。
        var rollback = await _rsi.RollbackAsync();
        Assert.True(rollback.Success, rollback.Reason);
        Assert.Equal(0.45, rollback.RestoredParameters!.ClarificationThreshold, 5);
        var afterRollback = await _rsi.LoadActiveParametersAsync();
        Assert.Equal(0.45, afterRollback.ClarificationThreshold, 5);
        Assert.Contains("RSI 回退", await File.ReadAllTextAsync(round.LogPath));
    }

    // ============ 验收 E2E ②：不过 gate 回退（参数不变 + 原因留痕） ============

    [Fact]
    public async Task AcceptanceE2E_RsiRound_NoCandidatePassesGate_ParamsUnchanged_ReasonLogged()
    {
        // 基线已是该样本集的最优：0.44 标注"不需要澄清"、0.46 标注"需要澄清"，
        // 任何阈值移动都会至少错一条；策略标注同样只有 4.0 全对（严格模式下平手也不算过）。
        var samples = new[]
        {
            ClarifySample(0.44, helped: false),
            ClarifySample(0.46, helped: true),
            StrategySample(3, OrchestrationStrategy.Single),
            StrategySample(4, OrchestrationStrategy.Decompose),
            StrategySample(5, OrchestrationStrategy.Decompose),
        };

        var round = await _rsi.RunRoundAsync(samples, new RsiRoundOptions { RequireStrictImprovement = true });

        Assert.Equal(1.0, round.BaselineAccuracy, 5);
        Assert.False(round.Applied);
        Assert.NotEmpty(round.Candidates);
        Assert.All(round.Candidates, c => Assert.False(c.PassedGate));
        Assert.Contains("未过 gate", round.Reason);
        Assert.Null(round.SnapshotPath);

        // 参数不变：当前参数文件根本没有被写出（基线是默认参数）。
        Assert.False(File.Exists(_env.LearningPaths.ActiveParameterFile));
        var active = await _rsi.LoadActiveParametersAsync();
        Assert.Equal(0.45, active.ClarificationThreshold, 5);
        Assert.Equal(4.0, active.DecomposeComplexityThreshold, 5);

        // 原因留痕：每个候选的评估与拒绝原因都在 rsi-log.md。
        var log = await File.ReadAllTextAsync(round.LogPath);
        Assert.Contains(round.RoundId, log);
        Assert.Contains("FAIL", log);
        Assert.Contains("决策: 拒绝", log);
        Assert.Equal(round.Candidates.Count, log.Split('\n').Count(l => l.Contains("候选 ")));
    }

    [Fact]
    public async Task Gate_TiePasses_UnderLiteralNotLowerThanSemantics()
    {
        // 与上一用例相同的样本集，但用默认 gate（"不低于当前"字面语义）：
        // decompose 3.5 候选在整数复杂度上与 4.0 行为等价 → 准确率平手 → 过 gate。
        var samples = new[]
        {
            ClarifySample(0.44, helped: false),
            ClarifySample(0.46, helped: true),
            StrategySample(3, OrchestrationStrategy.Single),
            StrategySample(4, OrchestrationStrategy.Decompose),
            StrategySample(5, OrchestrationStrategy.Decompose),
        };

        var round = await _rsi.RunRoundAsync(samples);

        Assert.True(round.Applied, round.Reason);
        Assert.Equal(3.5, round.AppliedCandidate!.Parameters.DecomposeComplexityThreshold, 5);
        Assert.Equal(0.45, round.AppliedCandidate.Parameters.ClarificationThreshold, 5);
    }

    [Fact]
    public async Task Gate_AccuracyFloor_RejectsCandidateThatBeatsBaselineButBelowFloor()
    {
        // 带噪标注：基线 0.4，最优候选 0.5——优于基线但低于下限 0.9 → 必须拒绝。
        var samples = new List<RsiHeldOutSample>
        {
            ClarifySample(0.5, helped: true), ClarifySample(0.5, helped: true),
            ClarifySample(0.5, helped: false), ClarifySample(0.5, helped: false), ClarifySample(0.5, helped: false),
            ClarifySample(0.3, helped: false), ClarifySample(0.3, helped: false),
            ClarifySample(0.3, helped: true), ClarifySample(0.3, helped: true), ClarifySample(0.3, helped: true),
        };

        var round = await _rsi.RunRoundAsync(samples, new RsiRoundOptions { MinAccuracyFloor = 0.9 });

        Assert.Equal(0.4, round.BaselineAccuracy, 5);
        Assert.False(round.Applied);
        Assert.Contains("下限", round.Reason + string.Join(" ", round.Candidates.Select(c => c.Note)));
        var best = round.Candidates.Max(c => c.Accuracy);
        Assert.Equal(0.5, best, 5);
        Assert.All(round.Candidates, c => Assert.False(c.PassedGate));
        Assert.False(File.Exists(_env.LearningPaths.ActiveParameterFile));
    }

    [Fact]
    public async Task Rollback_WithoutAnySnapshot_FailsHonestly()
    {
        var result = await _rsi.RollbackAsync();
        Assert.False(result.Success);
        Assert.Contains("快照", result.Reason);
    }

    // ============ L1 输出修正 + L2 记忆积累 ============

    private static RetrospectiveRecord RealRetrospectiveWithGaps()
    {
        // 用真实复盘引擎产缺口：一个只有分析产物的任务记录，缺澄清/钢人/计划/执行/校验。
        var record = new MissionRecord
        {
            TaskText = "用于 L1 测试的失败任务",
            AnalysisJson = "{\"Type\":0,\"Complexity\":2}",
            Outcome = AeroAgent.Autonomy.Mission.MissionOutcome.Failed,
        };
        var retro = new RetrospectiveEngine().Evaluate(record, null);
        Assert.NotEmpty(retro.Gaps); // 前置断言：真实缺口存在
        return retro;
    }

    [Fact]
    public async Task L1_RecordCorrections_FromRealRetrospectiveGaps_PersistsRules()
    {
        var retro = RealRetrospectiveWithGaps();

        var rules = await _rsi.RecordCorrectionsAsync(retro.MissionId, retro.Gaps);

        Assert.Equal(retro.Gaps.Count, rules.Count);
        Assert.All(rules, r =>
        {
            Assert.Contains("若再次出现缺口", r.RuleText);
            Assert.False(r.Promoted);
        });
        Assert.Contains(rules, r => r.RuleText.Contains(retro.Gaps[0].Description));

        // 落库真实性：全新上下文可读。
        using var fresh = _env.NewLearningDb();
        Assert.Equal(retro.Gaps.Count, await fresh.CorrectionRules.CountAsync());
    }

    [Fact]
    public async Task L1_EmptyGaps_NoRulesFabricated()
    {
        var rules = await _rsi.RecordCorrectionsAsync("m-clean", Array.Empty<GapItem>());
        Assert.Empty(rules);
    }

    [Fact]
    public async Task L2_PromoteCorrections_CreatesPendingMethodExperiences_Idempotent()
    {
        var retro = RealRetrospectiveWithGaps();
        var rules = await _rsi.RecordCorrectionsAsync(retro.MissionId, retro.Gaps);

        var promoted = await _rsi.PromoteCorrectionsAsync();
        Assert.Equal(rules.Count, promoted);

        var methods = await _env.Experiences.GetByKindAsync(ExperienceKind.Method, 50);
        Assert.Equal(rules.Count, methods.Count);
        Assert.All(methods, m =>
        {
            Assert.StartsWith("correction:", m.SourceKey);
            Assert.Equal(ExperienceStatus.Pending, m.Status); // 生效分离语义保持
            Assert.Contains("修正规则", m.Title);
        });

        // 幂等：第二次沉淀为 0。
        Assert.Equal(0, await _rsi.PromoteCorrectionsAsync());
    }

    [Fact]
    public async Task CompositeTier_L1PlusL2_InOneCall()
    {
        var retro = RealRetrospectiveWithGaps();

        var result = await _rsi.RunCompositeTierAsync(retro.MissionId, retro.Gaps);

        Assert.Equal(retro.Gaps.Count, result.CorrectionRulesRecorded);
        Assert.Equal(retro.Gaps.Count, result.MethodsPromoted);
    }

    // ============ 创造档审批门 ============

    private static AutoCreateCandidate SkillCandidate(string id) => new()
    {
        SuggestedId = id,
        SuggestedName = id.Replace('/', '-'),
        SuggestedDescription = "Auto-created by RSI creative tier.",
        SuggestedBody = "# Steps\n1. do the thing\n",
        ToolCallCount = 6,
        Succeeded = true,
    };

    [Fact]
    public async Task CreativeTier_DefaultApproval_IsDeny_NoSkillCreated()
    {
        var registry = new SkillRegistry();
        var creator = new SkillCreator(registry, Path.Combine(_env.Root, "user-skills"));
        using var rsiDb = _env.NewLearningDb();
        using var rsi = new RsiEngine(rsiDb, _env.LearningPaths, _env.Experiences, creator);

        var result = await rsi.ProposeSkillAsync(SkillCandidate("user/auto-export"));

        Assert.False(result.Approved);
        Assert.False(result.Created);
        Assert.Null(result.Skill);
        Assert.False(File.Exists(Path.Combine(_env.Root, "user-skills", "skills", "user", "auto-export", "SKILL.md")));
        var log = await File.ReadAllTextAsync(_env.LearningPaths.RsiLogFile);
        Assert.Contains("创造档", log);
        Assert.Contains("拒绝", log);
    }

    [Fact]
    public async Task CreativeTier_ExplicitApproval_CreatesRealSkillFile()
    {
        var registry = new SkillRegistry();
        var creator = new SkillCreator(registry, Path.Combine(_env.Root, "user-skills"));
        using var rsiDb = _env.NewLearningDb();
        using var rsi = new RsiEngine(rsiDb, _env.LearningPaths, _env.Experiences, creator);
        var approver = new AllowAllSkillApproval();

        var result = await rsi.ProposeSkillAsync(SkillCandidate("user/auto-export"), approver);

        Assert.Equal(1, approver.ApproveCalls);
        Assert.True(result.Approved);
        Assert.True(result.Created, result.Reason);
        Assert.NotNull(result.Skill);
        var skillFile = Path.Combine(_env.Root, "user-skills", "skills", "user", "auto-export", "SKILL.md");
        Assert.True(File.Exists(skillFile));
        Assert.Contains("Auto-created by RSI creative tier.", File.ReadAllText(skillFile));
        Assert.NotNull(registry.Get("user/auto-export"));
        var log = await File.ReadAllTextAsync(_env.LearningPaths.RsiLogFile);
        Assert.Contains("批准并创建成功", log);
    }

    [Fact]
    public async Task CreativeTier_WithoutSkillCreator_HonestlyUnavailable()
    {
        var result = await _rsi.ProposeSkillAsync(SkillCandidate("user/no-creator"));
        Assert.False(result.Approved);
        Assert.False(result.Created);
        Assert.Contains("SkillCreator", result.Reason);
    }
}
