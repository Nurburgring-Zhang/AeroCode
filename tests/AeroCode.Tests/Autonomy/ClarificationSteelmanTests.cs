// ClarificationGate + SteelmanProtocol tests — deterministic heuristic paths
// (no LLM registry → honest [DEGRADED] heuristic behavior is what's under test).
using AeroAgent.Autonomy.Clarification;
using AeroAgent.Autonomy.Llm;
using AeroAgent.Autonomy.Steelman;
using Xunit;

namespace AeroCode.Tests.Autonomy;

public class ClarificationGateTests
{
    private static ClarificationGate Gate() => new(new AutonomyLlmClient(registry: null));

    [Fact]
    public async Task HighlyAmbiguousText_RequiresClarification_WithTargetedQuestions()
    {
        var result = await Gate().EvaluateAsync("处理一下那个东西");

        Assert.True(result.RequiresClarification);
        Assert.True(result.AmbiguityScore >= ClarificationGate.DefaultThreshold);
        Assert.InRange(result.Questions.Count, 1, ClarificationGate.MaxQuestions);
        Assert.All(result.Questions, q =>
        {
            Assert.False(string.IsNullOrWhiteSpace(q.Dimension));
            Assert.False(string.IsNullOrWhiteSpace(q.Question));
        });
    }

    [Fact]
    public async Task Questions_AreOrderedByDimensionScoreDescending()
    {
        var result = await Gate().EvaluateAsync("处理一下那个东西");
        var scores = result.Questions
            .Select(q => result.DimensionScores[q.Dimension])
            .ToList();
        Assert.Equal(scores.OrderByDescending(s => s).ToList(), scores);
    }

    [Fact]
    public async Task FullySpecifiedText_PassesThrough_NoQuestions()
    {
        var text = "实现用户模块的登录功能，以全部单元测试通过作为验收标准，仅限修改 src/auth 目录内的文件，周五前完成交付";
        var result = await Gate().EvaluateAsync(text);

        Assert.False(result.RequiresClarification);
        Assert.Empty(result.Questions);
        Assert.True(result.AmbiguityScore < ClarificationGate.DefaultThreshold);
    }

    [Fact]
    public async Task Score_IsDeterministic_AndWithinUnitInterval()
    {
        var gate = Gate();
        var a = await gate.EvaluateAsync("优化一下系统");
        var b = await gate.EvaluateAsync("优化一下系统");

        Assert.Equal(a.AmbiguityScore, b.AmbiguityScore);
        Assert.InRange(a.AmbiguityScore, 0.0, 1.0);
    }

    [Fact]
    public async Task CustomThreshold_ChangesTriggerBehavior()
    {
        var gate = Gate();
        var text = "优化一下系统"; // moderately ambiguous
        var lowBar = await gate.EvaluateAsync(text, threshold: 0.01);
        var highBar = await gate.EvaluateAsync(text, threshold: 0.99);

        Assert.True(lowBar.RequiresClarification);
        Assert.False(highBar.RequiresClarification);
    }

    [Fact]
    public async Task HistoryDependentText_RaisesContextAmbiguity()
    {
        var withHistory = await Gate().EvaluateAsync("还是按上次那个方案继续做，完成验收即可");
        Assert.True(withHistory.DimensionScores[AmbiguityDimension.Context] >= 0.5);
    }

    [Fact]
    public async Task EmptyText_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Gate().EvaluateAsync("  "));
    }
}

public class SteelmanProtocolTests
{
    private static SteelmanProtocol Protocol() => new(new AutonomyLlmClient(registry: null));

    private sealed class ScriptedResponder : ISteelmanResponder
    {
        private readonly string _answer;
        public string? ReceivedQuestion { get; private set; }
        public ScriptedResponder(string answer) => _answer = answer;
        public Task<string?> AnswerAsync(string taskText, string keyQuestion, CancellationToken ct)
        {
            ReceivedQuestion = keyQuestion;
            return Task.FromResult<string?>(_answer);
        }
    }

    [Fact]
    public async Task HeuristicRecord_AllFiveFieldsNonEmpty()
    {
        var record = await Protocol().RunAsync("给订单模块增加退款功能", SteelmanMode.AutoApprove, responder: null);

        Assert.False(string.IsNullOrWhiteSpace(record.Restatement));
        Assert.False(string.IsNullOrWhiteSpace(record.ProArgument));
        Assert.False(string.IsNullOrWhiteSpace(record.ConArgument));
        Assert.False(string.IsNullOrWhiteSpace(record.Divergence));
        Assert.False(string.IsNullOrWhiteSpace(record.OneKeyQuestion));
    }

    [Fact]
    public async Task HeuristicFields_AreGroundedInTaskText_NotGenericFiller()
    {
        var record = await Protocol().RunAsync("给订单模块增加退款功能", SteelmanMode.AutoApprove, responder: null);
        // The subject extracted from the real task text must appear in the restatement.
        Assert.Contains("给订单模块增加退款功能"[..8], record.Restatement);
    }

    [Fact]
    public async Task AutoApprove_RecordsAssumptionsWithReasons()
    {
        var record = await Protocol().RunAsync("整理会议纪要", SteelmanMode.AutoApprove, responder: null);

        Assert.Equal(SteelmanMode.AutoApprove, record.Mode);
        Assert.NotEmpty(record.Assumptions);
        Assert.All(record.Assumptions, a => Assert.Contains("理由", a));
        Assert.False(record.DegradedToAutoApprove);
    }

    [Fact]
    public async Task Interactive_WithResponder_RecordsAnswer()
    {
        var responder = new ScriptedResponder("以退款到账为完成标准");
        var record = await Protocol().RunAsync(
            "给订单模块增加退款功能", SteelmanMode.Interactive, responder);

        Assert.Equal(SteelmanMode.Interactive, record.Mode);
        Assert.Equal("以退款到账为完成标准", record.KeyQuestionAnswer);
        Assert.NotNull(responder.ReceivedQuestion);
        Assert.False(record.DegradedToAutoApprove);
    }

    [Fact]
    public async Task Interactive_WithoutResponder_DegradesToAutoApprove_Honestly()
    {
        var record = await Protocol().RunAsync("给订单模块增加退款功能", SteelmanMode.Interactive, responder: null);

        Assert.Equal(SteelmanMode.AutoApprove, record.Mode);
        Assert.True(record.DegradedToAutoApprove);
        Assert.NotEmpty(record.Assumptions);
    }

    [Fact]
    public async Task EmptyTaskText_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => Protocol().RunAsync("", SteelmanMode.AutoApprove, null));
    }

    [Fact]
    public void ExtractSubject_StripsPolitenessPrefixes()
    {
        Assert.Equal("修复登录接口", SteelmanProtocol.ExtractSubject("请修复登录接口"));
    }

    [Fact]
    public void DetectMissingDimensions_FlagsAbsentCriteriaAndScope()
    {
        var missing = SteelmanProtocol.DetectMissingDimensions("做一个功能");
        Assert.Contains("验收标准", missing);
        Assert.Contains("范围约束", missing);

        var none = SteelmanProtocol.DetectMissingDimensions(
            "在范围内实现登录，验收标准为测试通过，期限是周五");
        Assert.DoesNotContain("验收标准", none);
        Assert.DoesNotContain("范围约束", none);
        Assert.DoesNotContain("期限与优先级", none);
    }

    [Fact]
    public void BuildKeyQuestion_TargetsTheFirstMissingDimension()
    {
        var q = SteelmanProtocol.BuildKeyQuestion(new System.Collections.Generic.List<string> { "范围约束" });
        Assert.Contains("范围", q);
    }
}
