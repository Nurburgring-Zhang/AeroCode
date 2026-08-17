using System;
using System.Linq;
using System.Threading.Tasks;
using AeroAgent.Conversation.Models;
using AeroAgent.Conversation.Orchestration;
using AeroAgent.Moa.Profiles;
using AeroAgent.Moa.Strategies;
using Xunit;

namespace AeroCode.Tests.MoaTests;

/// <summary>分工策略：planner 拆 DAG → 并行 worker → synthesizer 聚合，降级如实。</summary>
public sealed class DecomposeStrategyTests : MoaTestBase
{
    private const string TwoStepPlan = """
        {"goal":"g","steps":[
          {"id":"s1","title":"调研","description":"收集资料","dependsOn":[],"kind":"analysis"},
          {"id":"s2","title":"成文","description":"写成文章","dependsOn":["s1"],"kind":"writing"}
        ]}
        """;

    private DecomposeStrategy MakeStrategy() =>
        new(Sessions, Runner, Resolver, Assigner,
            new AeroAgent.Moa.Planning.TaskPlanner(Runner),
            new AeroAgent.Moa.Aggregation.Synthesizer(Runner),
            Options);

    /// <summary>标准四方阵容：planner / analyst / writer / synth。</summary>
    private (AeroCode.Tests.ConversationTests.ScriptedProvider Planner,
             AeroCode.Tests.ConversationTests.ScriptedProvider Analyst,
             AeroCode.Tests.ConversationTests.ScriptedProvider Writer,
             AeroCode.Tests.ConversationTests.ScriptedProvider Synth) SetupSquad()
    {
        var planner = AddProvider("planner");
        SetProfile("planner", new[] { ModelStrength.Planning });
        planner.NonStreamContent = TwoStepPlan;

        var analyst = AddProvider("analyst");
        SetProfile("analyst", new[] { ModelStrength.Analysis });
        analyst.NonStreamContent = "ALPHA-RESULT";

        var writer = AddProvider("writer");
        SetProfile("writer", new[] { ModelStrength.Writing });
        writer.NonStreamContent = "FINAL-DRAFT";

        var synth = AddProvider("synth");
        SetProfile("synth", new[] { ModelStrength.General });
        synth.Deltas = new[] { "综合", "结论" };

        return (planner, analyst, writer, synth);
    }

    [Fact]
    public async Task Decompose_TwoStepDag_DependentReceivesUpstreamResult()
    {
        var squad = SetupSquad();
        var facade = MakeFacade(MakeStrategy());
        var session = await NewSessionAsync(OrchestrationStrategy.Decompose);

        var events = await CollectAsync(facade.SendAsync(session.Id, "调研并写一篇文章"));

        var messages = (await Sessions.GetMessagesAsync(session.Id)).Value!;

        // planner + 2 worker + synthesizer = 4 条助手消息
        var plannerMsg = messages.Single(m => m.OrchestrationRole == StrategyRole.Planner);
        Assert.Equal("planner", plannerMsg.ProviderId);
        Assert.Equal(TwoStepPlan, plannerMsg.Content); // 规划原文留痕

        var s1 = messages.Single(m => m.Label == "调研");
        Assert.Equal("analyst", s1.ProviderId); // 按强项分配
        Assert.Equal(StrategyRole.Worker, s1.OrchestrationRole);
        Assert.Equal(plannerMsg.Id, s1.ParentMessageId);
        Assert.Equal("ALPHA-RESULT", s1.Content);

        var s2 = messages.Single(m => m.Label == "成文");
        Assert.Equal("writer", s2.ProviderId);
        Assert.Equal(plannerMsg.Id, s2.ParentMessageId);

        // 依赖传递：writer 收到的提示词里包含 s1 的产出
        Assert.NotNull(squad.Writer.LastRequestMessages);
        var writerPrompt = squad.Writer.LastRequestMessages!.Last().Content;
        Assert.Contains("ALPHA-RESULT", writerPrompt);
        Assert.Contains("调研并写一篇文章", writerPrompt);

        var synth = messages.Single(m => m.OrchestrationRole == StrategyRole.Synthesizer);
        Assert.Equal("synth", synth.ProviderId);
        Assert.Equal("综合结论", synth.Content); // 流式聚合
        Assert.Equal(plannerMsg.Id, synth.ParentMessageId);
        Assert.Equal(MessageStatus.Completed, synth.Status);

        var turn = events.OfType<TurnCompletedEvent>().Single();
        Assert.Equal(OrchestrationStrategy.Decompose, turn.Strategy);
        Assert.Equal(4, turn.TotalMessages);

        // 合成器收到的输入包含两个子任务结果
        var synthRequest = squad.Synth.LastRequestMessages!.Single(m => m.Role == "user").Content;
        Assert.Contains("ALPHA-RESULT", synthRequest);
        Assert.Contains("FINAL-DRAFT", synthRequest);
    }

    private const string ParallelPlan = """
        {"goal":"g","steps":[
          {"id":"s1","title":"调研","description":"收集资料","dependsOn":[],"kind":"analysis"},
          {"id":"s2","title":"成文","description":"写成文章","dependsOn":[],"kind":"writing"}
        ]}
        """;

    [Fact]
    public async Task Decompose_ParallelPartialFailure_SynthDegradedAndInformed()
    {
        var squad = SetupSquad();
        squad.Planner.NonStreamContent = ParallelPlan; // 两个互不依赖的子任务
        squad.Analyst.ThrowDuringChat = new InvalidOperationException("analyst 熔断");

        var facade = MakeFacade(MakeStrategy());
        var session = await NewSessionAsync(OrchestrationStrategy.Decompose);

        var events = await CollectAsync(facade.SendAsync(session.Id, "调研并写一篇文章"));

        var messages = (await Sessions.GetMessagesAsync(session.Id)).Value!;

        // s1 如实失败，失败前无产出
        var s1 = messages.Single(m => m.Label == "调研");
        Assert.Equal(MessageStatus.Failed, s1.Status);
        Assert.Contains("analyst 熔断", s1.Error);
        Assert.Equal(string.Empty, s1.Content);

        // s2 与 s1 无依赖，照常成功
        var s2 = messages.Single(m => m.Label == "成文");
        Assert.Equal(MessageStatus.Completed, s2.Status);
        Assert.Equal("FINAL-DRAFT", s2.Content);

        // 部分成功 → 合成照常进行，最终消息标记 Degraded
        var synth = messages.Single(m => m.OrchestrationRole == StrategyRole.Synthesizer);
        Assert.Equal(MessageStatus.Degraded, synth.Status);
        Assert.Equal("综合结论", synth.Content);

        // 合成输入如实包含失败信息与成功子任务的结果
        var synthPrompt = squad.Synth.LastRequestMessages!.Single(m => m.Role == "user").Content;
        Assert.Contains("【失败】", synthPrompt);
        Assert.Contains("analyst 熔断", synthPrompt);
        Assert.Contains("FINAL-DRAFT", synthPrompt);

        Assert.IsType<TurnCompletedEvent>(events[^1]);
    }

    [Fact]
    public async Task Decompose_UpstreamFails_DownstreamSkipped_HonestOverallFailure()
    {
        var squad = SetupSquad(); // TwoStepPlan：s2 依赖 s1
        squad.Analyst.ThrowDuringChat = new InvalidOperationException("analyst 熔断");

        var facade = MakeFacade(MakeStrategy());
        var session = await NewSessionAsync(OrchestrationStrategy.Decompose);

        var events = await CollectAsync(facade.SendAsync(session.Id, "调研并写一篇文章"));

        var messages = (await Sessions.GetMessagesAsync(session.Id)).Value!;

        // s1 如实失败
        var s1 = messages.Single(m => m.Label == "调研");
        Assert.Equal(MessageStatus.Failed, s1.Status);
        Assert.Contains("analyst 熔断", s1.Error);
        Assert.Equal(string.Empty, s1.Content);

        // s2 因上游失败被跳过 → 不创建消息、不发起模型调用（上游守卫）
        Assert.DoesNotContain(messages, m => m.Label == "成文");
        Assert.Null(squad.Writer.LastRequestMessages);

        // 无任何成功子任务 → 整轮如实报失败，不合成
        Assert.Contains(events, e => e is MessageFailedEvent f
            && f.Error.Contains("所有子任务均失败")
            && f.Error.Contains("上游子任务"));
        Assert.DoesNotContain(messages, m => m.OrchestrationRole == StrategyRole.Synthesizer);
        Assert.IsType<TurnCompletedEvent>(events[^1]);
    }

    [Fact]
    public async Task Decompose_AllSubtasksFail_ReportsFailure_NoSynth()
    {
        var squad = SetupSquad();
        squad.Analyst.ThrowDuringChat = new InvalidOperationException("a-down");
        squad.Writer.ThrowDuringChat = new InvalidOperationException("w-down");

        var facade = MakeFacade(MakeStrategy());
        var session = await NewSessionAsync(OrchestrationStrategy.Decompose);

        var events = await CollectAsync(facade.SendAsync(session.Id, "调研并写一篇文章"));

        // 整轮如实报失败，不合成
        Assert.Contains(events, e => e is MessageFailedEvent f
            && f.Error.Contains("所有子任务均失败")
            && f.Error.Contains("a-down"));
        var messages = (await Sessions.GetMessagesAsync(session.Id)).Value!;
        Assert.DoesNotContain(messages, m => m.OrchestrationRole == StrategyRole.Synthesizer);
    }

    [Fact]
    public async Task Decompose_PlannerOutputMalformed_FallsBackToSingleStep()
    {
        var squad = SetupSquad();
        squad.Planner.NonStreamContent = "我不会输出 JSON，只能这样回答了。";

        // 单步兜底：kind 缺失 → general 强项 → synth 画像（general）执行子任务
        squad.Synth.NonStreamContent = "单步直达结果";
        squad.Synth.Deltas = new[] { "最终", "答复" };

        var facade = MakeFacade(MakeStrategy());
        var session = await NewSessionAsync(OrchestrationStrategy.Decompose);

        var events = await CollectAsync(facade.SendAsync(session.Id, "做一件事"));

        var messages = (await Sessions.GetMessagesAsync(session.Id)).Value!;
        // planner 输出不可解析 → ParsePlan 兜底单步 → 仍有 worker + synth
        Assert.Contains(messages, m => m.OrchestrationRole == StrategyRole.Worker);
        var synth = messages.Single(m => m.OrchestrationRole == StrategyRole.Synthesizer);
        Assert.Equal(MessageStatus.Completed, synth.Status);
    }

    [Fact]
    public async Task Decompose_BudgetExceeded_AbortsHonestly()
    {
        var squad = SetupSquad();
        // planner 画像计价，usage 实报 → 一次调用即爆掉极小预算
        SetProfile("planner", new[] { ModelStrength.Planning }, costPerMIn: 10, costPerMOut: 10);
        squad.Planner.NonStreamUsage = new AeroCode.AI.Models.UsageInfo
        {
            PromptTokens = 1000,
            CompletionTokens = 1000,
            TotalTokens = 2000,
        };
        Options.MaxUsdPerTurn = 1e-9;

        var facade = MakeFacade(MakeStrategy());
        var session = await NewSessionAsync(OrchestrationStrategy.Decompose);

        var events = await CollectAsync(facade.SendAsync(session.Id, "调研并写一篇文章"));

        Assert.Contains(events, e => e is MessageFailedEvent f && f.Error.Contains("budget exceeded"));

        // worker 消息未被创建（预算拦截在持久化之前）
        var messages = (await Sessions.GetMessagesAsync(session.Id)).Value!;
        Assert.Contains(messages, m => m.OrchestrationRole == StrategyRole.Planner);
        Assert.DoesNotContain(messages, m => m.OrchestrationRole == StrategyRole.Worker);

        // planner 的成本如实落库：1000/1M*10 * 2 = $0.02
        var plannerMsg = messages.Single(m => m.OrchestrationRole == StrategyRole.Planner);
        Assert.Equal(0.02, plannerMsg.CostUsd, 6);
    }

    private const string DuplicateIdPlan = """
        {"goal":"g","steps":[
          {"id":"s1","title":"甲","description":"第一份活","dependsOn":[],"kind":"analysis"},
          {"id":"s1","title":"乙","description":"第二份活","dependsOn":[],"kind":"writing"}
        ]}
        """;

    [Fact]
    public async Task Decompose_PlanWithDuplicateIds_FailsBeforeAnyWorker()
    {
        var squad = SetupSquad();
        squad.Planner.NonStreamContent = DuplicateIdPlan;

        var facade = MakeFacade(MakeStrategy());
        var session = await NewSessionAsync(OrchestrationStrategy.Decompose);

        var events = await CollectAsync(facade.SendAsync(session.Id, "做两件事"));

        // 预校验如实报失败：可读错误 + 轮级失败事件，不抛裸异常
        Assert.Contains(events, e => e is MessageFailedEvent f
            && f.MessageId == string.Empty
            && f.Error.Contains("重复步骤 Id")
            && f.Error.Contains("'s1'"));

        // planner 留痕，但没有任何 worker/synth 被执行或落库
        var messages = (await Sessions.GetMessagesAsync(session.Id)).Value!;
        Assert.Contains(messages, m => m.OrchestrationRole == StrategyRole.Planner);
        Assert.DoesNotContain(messages, m => m.OrchestrationRole == StrategyRole.Worker);
        Assert.DoesNotContain(messages, m => m.OrchestrationRole == StrategyRole.Synthesizer);
        Assert.Null(squad.Analyst.LastRequestMessages);
        Assert.Null(squad.Writer.LastRequestMessages);
        Assert.IsType<TurnCompletedEvent>(events[^1]);
    }

    private const string UnknownDepPlan = """
        {"goal":"g","steps":[
          {"id":"s1","title":"调研","description":"收集资料","dependsOn":[],"kind":"analysis"},
          {"id":"s2","title":"成文","description":"写成文章","dependsOn":["missing"],"kind":"writing"}
        ]}
        """;

    [Fact]
    public async Task Decompose_PlanWithUnknownDependency_FailsBeforeAnyWorker()
    {
        var squad = SetupSquad();
        squad.Planner.NonStreamContent = UnknownDepPlan;

        var facade = MakeFacade(MakeStrategy());
        var session = await NewSessionAsync(OrchestrationStrategy.Decompose);

        var events = await CollectAsync(facade.SendAsync(session.Id, "调研并写一篇文章"));

        // 依赖缺失如实报失败：指出哪个步骤依赖了不存在的谁
        Assert.Contains(events, e => e is MessageFailedEvent f
            && f.MessageId == string.Empty
            && f.Error.Contains("'s2'")
            && f.Error.Contains("依赖了不存在的步骤")
            && f.Error.Contains("'missing'"));

        var messages = (await Sessions.GetMessagesAsync(session.Id)).Value!;
        Assert.DoesNotContain(messages, m => m.OrchestrationRole == StrategyRole.Worker);
        Assert.Null(squad.Analyst.LastRequestMessages);
        Assert.Null(squad.Writer.LastRequestMessages);
        Assert.IsType<TurnCompletedEvent>(events[^1]);
    }

    private const string EmptyIdPlan = """
        {"goal":"g","steps":[
          {"id":"","title":"甲","description":"一份活","dependsOn":[],"kind":"analysis"}
        ]}
        """;

    [Fact]
    public async Task Decompose_PlanWithEmptyId_FailsBeforeAnyWorker()
    {
        var squad = SetupSquad();
        squad.Planner.NonStreamContent = EmptyIdPlan;

        var facade = MakeFacade(MakeStrategy());
        var session = await NewSessionAsync(OrchestrationStrategy.Decompose);

        var events = await CollectAsync(facade.SendAsync(session.Id, "做一件事"));

        Assert.Contains(events, e => e is MessageFailedEvent f
            && f.MessageId == string.Empty
            && f.Error.Contains("空步骤 Id"));

        var messages = (await Sessions.GetMessagesAsync(session.Id)).Value!;
        Assert.DoesNotContain(messages, m => m.OrchestrationRole == StrategyRole.Worker);
        Assert.Null(squad.Analyst.LastRequestMessages);
        Assert.IsType<TurnCompletedEvent>(events[^1]);
    }
}
