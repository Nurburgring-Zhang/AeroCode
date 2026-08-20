// TaskAnalyzer + StrategySelector tests — deterministic heuristic path (no LLM registry
// injected, so AutonomyLlmClient.IsAvailable == false and the honest [DEGRADED]
// heuristic path is what runs — exactly what these tests target).
using AeroAgent.Autonomy.Analysis;
using AeroAgent.Autonomy.Llm;
using AeroAgent.Conversation.Models;
using Xunit;

namespace AeroCode.Tests.Autonomy;

public class TaskAnalyzerTests
{
    private static TaskAnalyzer Analyzer() => new(new AutonomyLlmClient(registry: null));

    [Fact]
    public async Task CodeTask_ClassifiedAsCode()
    {
        var analysis = await Analyzer().AnalyzeAsync("请帮我重构这个函数的代码并修复 bug，补充单元测试");
        Assert.Equal(TaskType.Code, analysis.Type);
        Assert.InRange(analysis.Complexity, 1, 5);
        Assert.NotEmpty(analysis.Rationale);
    }

    [Fact]
    public async Task ResearchTask_ClassifiedAsResearch()
    {
        var analysis = await Analyzer().AnalyzeAsync("调研主流向量数据库的使用现状，查文献并在全网搜集最新资料");
        Assert.Equal(TaskType.Research, analysis.Type);
    }

    [Fact]
    public async Task AnalysisTask_ClassifiedAsAnalysis()
    {
        var analysis = await Analyzer().AnalyzeAsync("对比评估这两套架构方案的优劣并给出结论");
        Assert.Equal(TaskType.Analysis, analysis.Type);
    }

    [Fact]
    public async Task CreativeTask_ClassifiedAsCreative()
    {
        var analysis = await Analyzer().AnalyzeAsync("写一篇关于春天的散文文案");
        Assert.Equal(TaskType.Creative, analysis.Type);
    }

    [Fact]
    public async Task OpsTask_ClassifiedAsOps()
    {
        var analysis = await Analyzer().AnalyzeAsync("部署服务到生产环境并配置监控");
        Assert.Equal(TaskType.Ops, analysis.Type);
    }

    [Fact]
    public async Task Complexity_IncreasesWithStructuralSignals()
    {
        var simple = await Analyzer().AnalyzeAsync("改个文案");
        var complexText =
            "完成以下多阶段工作：\n" +
            "1. 重构核心模块代码并补充测试\n" +
            "2. 调研竞品方案\n" +
            "3. 撰写技术报告\n" +
            "4. 部署上线并配置监控\n" +
            "要求分阶段验收，然后复盘，以及补全遗漏。";
        var complex = await Analyzer().AnalyzeAsync(complexText);
        Assert.True(complex.Complexity > simple.Complexity,
            $"complex={complex.Complexity} should exceed simple={simple.Complexity}");
    }

    [Fact]
    public async Task Capabilities_ArePopulatedWithReasons()
    {
        var analysis = await Analyzer().AnalyzeAsync("搜索全网资料调研这个主题，然后写报告");
        Assert.NotEmpty(analysis.Capabilities);
        Assert.All(analysis.Capabilities, c => Assert.False(string.IsNullOrWhiteSpace(c.Reason)));
    }

    [Fact]
    public async Task EmptyText_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Analyzer().AnalyzeAsync("   "));
    }

    [Fact]
    public async Task Source_IsHeuristic_WhenNoLlmConfigured()
    {
        var analysis = await Analyzer().AnalyzeAsync("写一个排序算法");
        Assert.Equal(AeroAgent.Autonomy.Common.AnalysisSource.Heuristic, analysis.Source);
    }
}

public class StrategySelectorTests
{
    private static TaskAnalysis Analysis(TaskType type, int complexity) =>
        new() { Type = type, Complexity = complexity };

    private readonly StrategySelector _selector = new();

    [Fact]
    public void Composite_AlwaysDecompose()
    {
        var d = _selector.Select(Analysis(TaskType.Composite, 2));
        Assert.Equal(OrchestrationStrategy.Decompose, d.Strategy);
        Assert.Contains("Decompose", d.Rationale);
    }

    [Fact]
    public void HighComplexity_Decompose()
    {
        var d = _selector.Select(Analysis(TaskType.Code, StrategySelector.DecomposeComplexityThreshold));
        Assert.Equal(OrchestrationStrategy.Decompose, d.Strategy);
    }

    [Fact]
    public void Creative_Ensemble()
    {
        Assert.Equal(OrchestrationStrategy.Ensemble, _selector.Select(Analysis(TaskType.Creative, 2)).Strategy);
    }

    [Fact]
    public void Research_Router()
    {
        Assert.Equal(OrchestrationStrategy.Router, _selector.Select(Analysis(TaskType.Research, 2)).Strategy);
    }

    [Fact]
    public void Analysis_Pipeline()
    {
        Assert.Equal(OrchestrationStrategy.Pipeline, _selector.Select(Analysis(TaskType.Analysis, 2)).Strategy);
    }

    [Fact]
    public void SimpleCode_Single()
    {
        Assert.Equal(OrchestrationStrategy.Single, _selector.Select(Analysis(TaskType.Code, 2)).Strategy);
    }

    [Fact]
    public void SimpleOps_Single()
    {
        Assert.Equal(OrchestrationStrategy.Single, _selector.Select(Analysis(TaskType.Ops, 1)).Strategy);
    }

    [Fact]
    public void NullAnalysis_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => _selector.Select(null!));
    }
}
