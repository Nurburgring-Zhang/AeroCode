using System;
using System.Linq;
using System.Threading.Tasks;
using AeroAgent.Conversation.Models;
using AeroAgent.Conversation.Orchestration;
using AeroAgent.Moa.Aggregation;
using AeroAgent.Moa.Profiles;
using AeroAgent.Moa.Strategies;
using Xunit;

namespace AeroCode.Tests.MoaTests;

/// <summary>集成策略：N 模型并行作答 → judge 裁决合成；部分/全部失败诚实降级。</summary>
public sealed class EnsembleStrategyTests : MoaTestBase
{
    private EnsembleStrategy MakeStrategy() =>
        new(Sessions, Runner, Resolver, Assigner, new Synthesizer(Runner), Options);

    /// <summary>标准三方阵容：两个 general 候选 + 一个 review 裁决者。</summary>
    private (AeroCode.Tests.ConversationTests.ScriptedProvider Alpha,
             AeroCode.Tests.ConversationTests.ScriptedProvider Beta,
             AeroCode.Tests.ConversationTests.ScriptedProvider Judge) SetupTrio()
    {
        var alpha = AddProvider("alpha");
        SetProfile("alpha", new[] { ModelStrength.General });
        alpha.NonStreamContent = "ANSWER-A";

        var beta = AddProvider("beta");
        SetProfile("beta", new[] { ModelStrength.General });
        beta.NonStreamContent = "ANSWER-B";

        var judge = AddProvider("judge");
        SetProfile("judge", new[] { ModelStrength.Review });
        judge.Deltas = new[] { "裁决", "结论" };

        return (alpha, beta, judge);
    }

    [Fact]
    public async Task Ensemble_TwoCandidates_JudgeSynthesizes()
    {
        var trio = SetupTrio();
        var facade = MakeFacade(MakeStrategy());
        var session = await NewSessionAsync(OrchestrationStrategy.Ensemble);

        var events = await CollectAsync(facade.SendAsync(session.Id, "哪一个是正确的？"));

        var messages = (await Sessions.GetMessagesAsync(session.Id)).Value!;

        // 两个候选作答：按强项排名（general +100），judge 画像无 general → 不入候选
        var candidates = messages
            .Where(m => m.OrchestrationRole == StrategyRole.Worker)
            .OrderBy(m => m.Label)
            .ToList();
        Assert.Equal(2, candidates.Count);
        Assert.Equal("候选 A", candidates[0].Label);
        Assert.Equal("alpha", candidates[0].ProviderId); // 平分时按 providerId 确定性排序
        Assert.Equal("ANSWER-A", candidates[0].Content);
        Assert.Equal(MessageStatus.Completed, candidates[0].Status);
        Assert.Equal("候选 B", candidates[1].Label);
        Assert.Equal("beta", candidates[1].ProviderId);
        Assert.Equal("ANSWER-B", candidates[1].Content);

        // judge 裁决：流式输出，面向用户的最终答复
        var judgeMsg = messages.Single(m => m.OrchestrationRole == StrategyRole.Judge);
        Assert.Equal("judge", judgeMsg.ProviderId);
        Assert.Equal("裁决合成", judgeMsg.Label);
        Assert.Equal("裁决结论", judgeMsg.Content);
        Assert.Equal(MessageStatus.Completed, judgeMsg.Status);

        // judge 收到的提示词包含两份候选答案与原始问题
        var judgePrompt = trio.Judge.LastRequestMessages!.Single(m => m.Role == "user").Content;
        Assert.Contains("ANSWER-A", judgePrompt);
        Assert.Contains("ANSWER-B", judgePrompt);
        Assert.Contains("哪一个是正确的？", judgePrompt);

        var turn = events.OfType<TurnCompletedEvent>().Single();
        Assert.Equal(OrchestrationStrategy.Ensemble, turn.Strategy);
        Assert.Equal(3, turn.TotalMessages);
    }

    [Fact]
    public async Task Ensemble_SingleCandidate_FailsHonestly()
    {
        var solo = AddProvider("solo");
        SetProfile("solo", new[] { ModelStrength.General });
        solo.NonStreamContent = "孤本答案";

        var facade = MakeFacade(MakeStrategy());
        var session = await NewSessionAsync(OrchestrationStrategy.Ensemble);

        var events = await CollectAsync(facade.SendAsync(session.Id, "来点集成"));

        Assert.Contains(events, e => e is MessageFailedEvent f
            && f.Error.Contains("至少 2 个已配置模型"));
        Assert.IsType<TurnCompletedEvent>(events[^1]);

        // 没有创建任何助手消息
        var messages = (await Sessions.GetMessagesAsync(session.Id)).Value!;
        Assert.DoesNotContain(messages, m => m.Role == ChatRole.Assistant);
    }

    [Fact]
    public async Task Ensemble_CandidateFails_JudgeInformedAndFinalDegraded()
    {
        var trio = SetupTrio();
        trio.Beta.ThrowDuringChat = new InvalidOperationException("beta 上游超时");

        var facade = MakeFacade(MakeStrategy());
        var session = await NewSessionAsync(OrchestrationStrategy.Ensemble);

        var events = await CollectAsync(facade.SendAsync(session.Id, "哪一个是正确的？"));

        var messages = (await Sessions.GetMessagesAsync(session.Id)).Value!;

        // 失败候选如实落库
        var betaMsg = messages.Single(m => m.Label == "候选 B");
        Assert.Equal(MessageStatus.Failed, betaMsg.Status);
        Assert.Contains("beta 上游超时", betaMsg.Error);
        Assert.Equal(string.Empty, betaMsg.Content);

        // 成功候选照常
        var alphaMsg = messages.Single(m => m.Label == "候选 A");
        Assert.Equal(MessageStatus.Completed, alphaMsg.Status);

        // judge 照常裁决，但最终消息标注 Degraded（部分降级诚实标记）
        var judgeMsg = messages.Single(m => m.OrchestrationRole == StrategyRole.Judge);
        Assert.Equal(MessageStatus.Degraded, judgeMsg.Status);
        Assert.Equal("裁决结论", judgeMsg.Content);

        // judge 提示词如实包含失败信息
        var judgePrompt = trio.Judge.LastRequestMessages!.Single(m => m.Role == "user").Content;
        Assert.Contains("【该候选失败】", judgePrompt);
        Assert.Contains("beta 上游超时", judgePrompt);

        // 失败候选的终态事件也流出来了（runner 报的）
        Assert.Contains(events, e => e is MessageFailedEvent f
            && f.MessageId == betaMsg.Id && f.Error.Contains("beta 上游超时"));
        Assert.IsType<TurnCompletedEvent>(events[^1]);
    }

    [Fact]
    public async Task Ensemble_AllCandidatesFail_NoJudge_ReportsFailure()
    {
        var trio = SetupTrio();
        trio.Alpha.ThrowDuringChat = new InvalidOperationException("alpha-down");
        trio.Beta.ThrowDuringChat = new InvalidOperationException("beta-down");

        var facade = MakeFacade(MakeStrategy());
        var session = await NewSessionAsync(OrchestrationStrategy.Ensemble);

        var events = await CollectAsync(facade.SendAsync(session.Id, "哪一个是正确的？"));

        // 整轮如实报失败，且列出两个候选的失败原因
        var failure = events.OfType<MessageFailedEvent>()
            .Single(f => f.Error.Contains("所有候选模型均失败"));
        Assert.Contains("alpha-down", failure.Error);
        Assert.Contains("beta-down", failure.Error);

        // 没有 judge 消息（不做无米之炊）
        var messages = (await Sessions.GetMessagesAsync(session.Id)).Value!;
        Assert.DoesNotContain(messages, m => m.OrchestrationRole == StrategyRole.Judge);
        Assert.Equal(2, messages.Count(m => m.Role == ChatRole.Assistant)); // 只有两条失败候选
        Assert.All(messages.Where(m => m.Role == ChatRole.Assistant),
            m => Assert.Equal(MessageStatus.Failed, m.Status));
    }

    [Fact]
    public async Task Ensemble_ExplicitJudgeBinding_OverridesAutoAssignment()
    {
        var trio = SetupTrio();
        // 另加一个 review 强项模型：若按画像自动分配会与 judgePin 竞争
        var judgeAuto = AddProvider("judge-auto");
        SetProfile("judge-auto", new[] { ModelStrength.Review });
        judgeAuto.Deltas = new[] { "不该", "是我" };

        // 显式绑定到只有 general 画像的 judgePin
        var judgePin = AddProvider("judge-pin");
        SetProfile("judge-pin", new[] { ModelStrength.General });
        judgePin.Deltas = new[] { "钦定", "裁决" };
        Options.Judge = new ModelBinding("judge-pin", null);

        var facade = MakeFacade(MakeStrategy());
        var session = await NewSessionAsync(OrchestrationStrategy.Ensemble);

        await CollectAsync(facade.SendAsync(session.Id, "哪一个是正确的？"));

        var messages = (await Sessions.GetMessagesAsync(session.Id)).Value!;
        var judgeMsg = messages.Single(m => m.OrchestrationRole == StrategyRole.Judge);
        Assert.Equal("judge-pin", judgeMsg.ProviderId); // 绑定优先于画像分配
        Assert.Equal("钦定裁决", judgeMsg.Content);
        Assert.DoesNotContain(messages, m => m.ProviderId == "judge-auto");
    }

    [Fact]
    public async Task Ensemble_EnsembleSizeThree_UsesThreeCandidates()
    {
        var trio = SetupTrio();
        var gamma = AddProvider("gamma");
        SetProfile("gamma", new[] { ModelStrength.General });
        gamma.NonStreamContent = "ANSWER-C";
        Options.EnsembleSize = 3;

        var facade = MakeFacade(MakeStrategy());
        var session = await NewSessionAsync(OrchestrationStrategy.Ensemble);

        await CollectAsync(facade.SendAsync(session.Id, "三方会审"));

        var messages = (await Sessions.GetMessagesAsync(session.Id)).Value!;
        var candidates = messages.Where(m => m.OrchestrationRole == StrategyRole.Worker).ToList();
        Assert.Equal(3, candidates.Count);
        Assert.Contains(candidates, m => m.Label == "候选 C" && m.ProviderId == "gamma");

        // judge 提示词包含三份答案
        var judgePrompt = trio.Judge.LastRequestMessages!.Single(m => m.Role == "user").Content;
        Assert.Contains("ANSWER-A", judgePrompt);
        Assert.Contains("ANSWER-B", judgePrompt);
        Assert.Contains("ANSWER-C", judgePrompt);
    }
}
