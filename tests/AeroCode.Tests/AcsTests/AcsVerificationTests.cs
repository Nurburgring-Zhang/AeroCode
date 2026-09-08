// Copyright (c) AeroCode
// ACS-2 验证引擎测试：排序/扫描/每步审/工件谱系/双盲审计/图工程扩展。
using AeroCode.Harness.Acs;
using AeroCode.Harness.Graph;
using Xunit;

namespace AeroCode.Tests.AcsTests;

public sealed class VerifyRankerTests
{
    private static IReadOnlyList<AcsCriterion> Criteria() => new[]
    {
        new AcsCriterion("correctness", 0.4),
        new AcsCriterion("risk", 0.3),
        new AcsCriterion("cost", 0.2),
        new AcsCriterion("clarity", 0.1),
        new AcsCriterion("testability", 0.0), // 凑满 5 项，权重 0
    };

    private static List<AcsScore> Scores() => new()
    {
        new("A", "correctness", 18), new("A", "risk", 16), new("A", "cost", 14), new("A", "clarity", 15), new("A", "testability", 10),
        new("B", "correctness", 12), new("B", "risk", 10), new("B", "cost", 18), new("B", "clarity", 12), new("B", "testability", 10),
    };

    [Fact]
    public void Rank_OrdersByWeightedScore()
    {
        var ranker = new VerifyRanker();
        var r = ranker.Rank(Scores(), Criteria());
        Assert.Equal("A", r.Ranking[0].CandidateId); // A 加权分更高
        Assert.True(r.Ranking[0].WeightedScore > r.Ranking[1].WeightedScore);
    }

    [Fact]
    public void Rank_TooFewCriteria_Throws()
    {
        var ranker = new VerifyRanker();
        var few = new[] { new AcsCriterion("a", 0.5), new AcsCriterion("b", 0.5) };
        Assert.Throws<ArgumentException>(() => ranker.Rank(Scores(), few));
    }

    [Fact]
    public void Rank_WeightsNotNormalized_Throws()
    {
        var ranker = new VerifyRanker();
        var bad = new[]
        {
            new AcsCriterion("a", 0.5), new AcsCriterion("b", 0.5),
            new AcsCriterion("c", 0.5), new AcsCriterion("d", 0.5), new AcsCriterion("e", 0.5),
        };
        Assert.Throws<ArgumentException>(() => ranker.Rank(Scores(), bad));
    }

    [Fact]
    public void Rank_ScoreOutOfRange_Throws()
    {
        var ranker = new VerifyRanker();
        var scores = Scores();
        scores.Add(new("C", "correctness", 25)); // 超 20
        Assert.Throws<ArgumentException>(() => ranker.Rank(scores, Criteria()));
    }

    [Fact]
    public void RankWithPivots_ProducesRanking()
    {
        var ranker = new VerifyRanker();
        var r = ranker.RankWithPivots(Scores(), Criteria(), new[] { "A", "B" });
        Assert.Equal(2, r.Ranking.Count);
        Assert.Contains("pivot", r.Method);
    }
}

public sealed class LeakScannerTests
{
    private readonly LeakScanner _scanner = new();

    [Theory]
    [InlineData("my key is sk-ABCDEFGHIJKLMNOPQRSTUVWXYZ123456 ok")]
    [InlineData("token ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZ1234567890 here")]
    [InlineData("aws AKIAIOSFODNN7EXAMPLE key")]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----\nMIIE...")]
    public void DetectsLeaks(string text)
    {
        var findings = _scanner.Scan(text);
        Assert.NotEmpty(findings);
        Assert.All(findings, f => Assert.True(f.Excerpt.Length <= 12)); // 摘录截断不回显完整密钥
    }

    [Fact]
    public void CleanText_NoFindings()
    {
        Assert.True(_scanner.IsClean("just a normal sentence about code"));
        Assert.True(_scanner.IsClean(""));
        Assert.True(_scanner.IsClean(null));
    }

    [Fact]
    public void ExcerptDoesNotEchoFullKey()
    {
        var findings = _scanner.Scan("sk-ABCDEFGHIJKLMNOPQRSTUVWXYZ123456");
        var f = Assert.Single(findings);
        Assert.DoesNotContain("ABCDEFGHIJKLMNOPQRSTUVWXYZ123456", f.Excerpt);
    }
}

public sealed class RealityScannerTests
{
    private readonly RealityScanner _scanner = new();

    [Fact]
    public void DetectsVagueAcceptancePhrase()
    {
        var findings = _scanner.ScanVaguePhrases("验收标准：应该可以工作", "acceptance");
        Assert.NotEmpty(findings);
    }

    [Fact]
    public void AllowMarker_ExemptsLine()
    {
        var findings = _scanner.ScanVaguePhrases("应该可以工作 ACS-ALLOW", "acceptance");
        Assert.Empty(findings);
    }

    [Fact]
    public void CleanAcceptance_NoFindings()
    {
        var findings = _scanner.ScanVaguePhrases("验收：运行 build.sh 退出码 0 且产物存在", "acceptance");
        Assert.Empty(findings);
    }

    [Fact]
    public void AcceptanceBinding_BoundArtifact_Passes()
    {
        var findings = _scanner.CheckAcceptanceBinding(
            "验收：build.sh 退出码 0", new[] { "build.sh" });
        Assert.Empty(findings);
    }

    [Fact]
    public void AcceptanceBinding_Unbound_Fails()
    {
        var findings = _scanner.CheckAcceptanceBinding(
            "验收：构建成功", new[] { "build.sh" });
        Assert.NotEmpty(findings);
        Assert.Contains(findings, f => f.Kind == "acceptance-unbound");
    }

    [Fact]
    public void AcceptanceBinding_NoArtifacts_Fails()
    {
        var findings = _scanner.CheckAcceptanceBinding("验收：构建成功", Array.Empty<string>());
        Assert.NotEmpty(findings);
    }
}

public sealed class PerStepReviewTests
{
    private readonly PerStepReview _review = new();

    private static AcsStepReviewRecord ValidRecord(int round = 1) => new(
        Builder: "builder-1",
        Verifier: "verifier-2",
        Verdict: AcsReviewVerdict.Pass,
        IssuesFound: "",
        IssuesClosed: "",
        SelfCheck: "自查：产出已验证",
        Recheck: null,
        Round: round);

    [Fact]
    public void ValidRecord_T2_Accepted()
    {
        var r = _review.Validate(ValidRecord(), "T2");
        Assert.True(r.Accepted);
    }

    [Fact]
    public void SameBuilderVerifier_Rejected()
    {
        var rec = ValidRecord() with { Verifier = "builder-1" };
        var r = _review.Validate(rec, "T2");
        Assert.False(r.Accepted);
        Assert.Contains(r.Problems, p => p.Contains("同一署名"));
    }

    [Fact]
    public void VerdictFail_Rejected()
    {
        var rec = ValidRecord() with { Verdict = AcsReviewVerdict.Fail };
        var r = _review.Validate(rec, "T2");
        Assert.False(r.Accepted);
        Assert.Contains(r.Problems, p => p.Contains("verdict=fail"));
    }

    [Fact]
    public void IssuesFoundWithoutClosed_Rejected()
    {
        var rec = ValidRecord() with
        {
            IssuesFound = "发现一个真实问题",
            IssuesClosed = "",
            Recheck = null,
        };
        var r = _review.Validate(rec, "T2");
        Assert.False(r.Accepted);
        Assert.Contains(r.Problems, p => p.Contains("等量闭环"));
        Assert.Contains(r.Problems, p => p.Contains("recheck"));
    }

    [Fact]
    public void IssuesClosedWithRecheck_Accepted()
    {
        var rec = ValidRecord() with
        {
            Verdict = AcsReviewVerdict.PassWithFixes,
            IssuesFound = "发现一个真实问题",
            IssuesClosed = "已修复并复验",
            Recheck = "复验通过",
        };
        var r = _review.Validate(rec, "T2");
        Assert.True(r.Accepted);
    }

    [Fact]
    public void T3_RequiresMinRounds()
    {
        var r = _review.Validate(ValidRecord(round: 1), "T3");
        Assert.False(r.Accepted);
        Assert.Contains(r.Problems, p => p.Contains("T3"));

        var ok = _review.Validate(ValidRecord(round: 2), "T3");
        Assert.True(ok.Accepted);
    }

    [Fact]
    public void T0_NotApplicable_Accepted()
    {
        var r = _review.Validate(ValidRecord(), "T0");
        Assert.True(r.Accepted); // T0 不在 apply_tiers，直接放行
    }
}

public sealed class ArtifactRegistryTests
{
    [Fact]
    public void ValidLineage_Accepted()
    {
        var reg = new ArtifactRegistry();
        var (a1, e1) = reg.Register(new AcsArtifact("a1", "设计文档", "root", null, DateTime.UtcNow));
        Assert.True(a1);
        var (a2, e2) = reg.Register(new AcsArtifact("a2", "设计文档v2", "root", "a1", DateTime.UtcNow));
        Assert.True(a2);
        var lineage = reg.ValidateLineage();
        Assert.True(lineage.Valid);
    }

    [Fact]
    public void DuplicateId_Rejected()
    {
        var reg = new ArtifactRegistry();
        reg.Register(new AcsArtifact("a1", "文档", "root", null, DateTime.UtcNow));
        var (ok, err) = reg.Register(new AcsArtifact("a1", "文档2", "root", null, DateTime.UtcNow));
        Assert.False(ok);
        Assert.Contains("重复", err);
    }

    [Fact]
    public void MissingProducedBy_Rejected()
    {
        var reg = new ArtifactRegistry();
        var (ok, err) = reg.Register(new AcsArtifact("a1", "文档", "", null, DateTime.UtcNow));
        Assert.False(ok);
        Assert.Contains("produced_by", err);
    }

    [Fact]
    public void UnregisteredProducedBy_Rejected()
    {
        var reg = new ArtifactRegistry();
        var (ok, err) = reg.Register(new AcsArtifact("a1", "文档", "ghost-node", null, DateTime.UtcNow));
        Assert.False(ok);
        Assert.Contains("断链", err);
    }

    [Fact]
    public void UnregisteredSupersedes_Rejected()
    {
        var reg = new ArtifactRegistry();
        reg.Register(new AcsArtifact("a1", "文档", "root", null, DateTime.UtcNow));
        var (ok, err) = reg.Register(new AcsArtifact("a2", "文档2", "root", "ghost", DateTime.UtcNow));
        Assert.False(ok);
        Assert.Contains("断链", err);
    }
}

public sealed class AcsDoubleBlindAuditTests
{
    private readonly AcsDoubleBlindAudit _audit = new();

    [Fact]
    public void TwoIndependentReviewers_Compliant()
    {
        var records = new[]
        {
            new AcsReviewRecord("reviewer-1", "builder-x", "pass", null),
            new AcsReviewRecord("reviewer-2", "builder-x", "pass", null),
        };
        var r = _audit.Audit(records);
        Assert.True(r.Compliant);
    }

    [Fact]
    public void SingleReviewer_NonCompliant()
    {
        var records = new[] { new AcsReviewRecord("reviewer-1", "builder-x", "pass", null) };
        var r = _audit.Audit(records);
        Assert.False(r.Compliant);
        Assert.Contains(r.Problems, p => p.Contains("双盲"));
    }

    [Fact]
    public void BuilderSelfReview_NonCompliant()
    {
        var records = new[]
        {
            new AcsReviewRecord("builder-x", "builder-x", "pass", null),
            new AcsReviewRecord("reviewer-2", "builder-x", "pass", null),
        };
        var r = _audit.Audit(records);
        Assert.False(r.Compliant);
        Assert.Contains(r.Problems, p => p.Contains("自审"));
    }

    [Fact]
    public void VerdictConflictWithoutArbitration_NonCompliant()
    {
        var records = new[]
        {
            new AcsReviewRecord("reviewer-1", "builder-x", "pass", null),
            new AcsReviewRecord("reviewer-2", "builder-x", "fail", null),
        };
        var r = _audit.Audit(records);
        Assert.False(r.Compliant);
        Assert.Contains(r.Problems, p => p.Contains("仲裁"));
    }

    [Fact]
    public void VerdictConflictWithArbitration_Compliant()
    {
        var records = new[]
        {
            new AcsReviewRecord("reviewer-1", "builder-x", "pass", "仲裁：采纳 reviewer-2 意见"),
            new AcsReviewRecord("reviewer-2", "builder-x", "fail", "仲裁：采纳 reviewer-2 意见"),
        };
        var r = _audit.Audit(records);
        Assert.True(r.Compliant);
    }
}

public sealed class TaskGraphAcsTests
{
    [Fact]
    public void ValidContract_Builds()
    {
        var g = new TaskGraphBuilder()
            .Add("n1", "节点1", _ => Task.FromResult("ok"),
                contract: new NodeContract(new[] { "in" }, new[] { "out" }, "验收标准"))
            .Build();
        Assert.Single(g.Nodes);
    }

    [Fact]
    public void IncompleteContract_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => new TaskGraphBuilder()
            .Add("n1", "节点1", _ => Task.FromResult("ok"),
                contract: new NodeContract(Array.Empty<string>(), new[] { "out" }, "验收"))
            .Build());
    }

    [Fact]
    public void CmdEdgeGateWithoutCommand_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => new TaskGraphBuilder()
            .Add("n1", "节点1", _ => Task.FromResult("ok"),
                edgeGate: new EdgeGate("cmd", "", 0))
            .Build());
    }

    [Fact]
    public void MinSuccessBarrierOutOfRange_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => new TaskGraphBuilder()
            .Add("a", "上游", _ => Task.FromResult("ok"))
            .Add("b", "汇聚", _ => Task.FromResult("ok"),
                dependsOn: new[] { "a" },
                barrier: new BarrierConfig(BarrierPolicy.MinSuccess, 5)) // MinCount 5 > 上游数 1
            .Build());
    }

    [Fact]
    public async Task AnySuccessBarrier_RunsWhenOneSucceeds()
    {
        var g = new TaskGraphBuilder()
            .Add("ok", "成功上游", _ => Task.FromResult("ok"))
            .Add("fail", "失败上游", _ => throw new InvalidOperationException("boom"))
            .Add("join", "汇聚", _ => Task.FromResult("joined"),
                dependsOn: new[] { "ok", "fail" },
                barrier: new BarrierConfig(BarrierPolicy.AnySuccess, 1))
            .Build();

        var result = await g.ExecuteAsync(continueOnError: true);
        Assert.Equal(TaskState.Succeeded, g.Nodes["join"].State);
    }

    [Fact]
    public async Task AllDoneBarrier_RunsWhenAllComplete_RegardlessOfFailure()
    {
        var g = new TaskGraphBuilder()
            .Add("ok", "成功上游", _ => Task.FromResult("ok"))
            .Add("fail", "失败上游", _ => throw new InvalidOperationException("boom"))
            .Add("join", "汇聚", _ => Task.FromResult("joined"),
                dependsOn: new[] { "ok", "fail" },
                barrier: new BarrierConfig(BarrierPolicy.AllDone, 0))
            .Build();

        await g.ExecuteAsync(continueOnError: true);
        Assert.Equal(TaskState.Succeeded, g.Nodes["join"].State);
    }

    [Fact]
    public void ModelTier_Registered()
    {
        var g = new TaskGraphBuilder()
            .Add("n1", "判断节点", _ => Task.FromResult("ok"), modelTier: "strong")
            .Build();
        Assert.Equal("strong", g.Nodes["n1"].ModelTier);
    }
}
