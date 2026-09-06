// Copyright (c) AeroCode
// F-M2 接线钉子：DecomposeStrategy 在「未显式配置」（MoaOptions 保持基线默认 AgentsAsTool 标记值）
// 时走 PrimitiveSelectionPolicy 形态自动判定——纯顺序链计划自动选 Handoff（顺序控制权转移路径可达，
// R1 审查 F-M2：修复前 configured 恒非空 → 自动判定分支不可达）；显式配置仍优先（对并行计划强制 Handoff
// 的上下文移交照旧）。默认并行计划仍是 AgentsAsTool 基线（由 OrchestrationPrimitiveDecomposeTests 钉）。
using System;
using System.Linq;
using System.Threading.Tasks;
using AeroAgent.Conversation.Models;
using AeroAgent.Conversation.Orchestration;
using AeroAgent.Moa.Profiles;
using AeroAgent.Moa.Strategies;
using Xunit;

namespace AeroCode.Tests.MoaTests;

public sealed class OrchestrationPrimitiveAutoDetectTests : MoaTestBase
{
    private const string ChainPlan = """
        {"goal":"g","steps":[
          {"id":"s1","title":"调研","description":"收集资料","dependsOn":[],"kind":"analysis"},
          {"id":"s2","title":"成文","description":"基于调研写成文章","dependsOn":["s1"],"kind":"writing"},
          {"id":"s3","title":"终校","description":"基于成文做最终校订","dependsOn":["s2"],"kind":"writing"}
        ]}
        """;

    private const string ParallelPlan = """
        {"goal":"g","steps":[
          {"id":"s1","title":"调研","description":"收集资料","dependsOn":[],"kind":"analysis"},
          {"id":"s2","title":"成文","description":"写成文章","dependsOn":[],"kind":"writing"}
        ]}
        """;

    private (AeroCode.Tests.ConversationTests.ScriptedProvider Planner,
             AeroCode.Tests.ConversationTests.ScriptedProvider Analyst,
             AeroCode.Tests.ConversationTests.ScriptedProvider Writer,
             AeroCode.Tests.ConversationTests.ScriptedProvider Synth) SetupSquad()
    {
        var planner = AddProvider("planner");
        SetProfile("planner", new[] { ModelStrength.Planning });
        planner.NonStreamContent = ChainPlan;

        var analyst = AddProvider("analyst");
        SetProfile("analyst", new[] { ModelStrength.Analysis });
        analyst.NonStreamContent = "RESEARCH-DONE";

        var writer = AddProvider("writer");
        SetProfile("writer", new[] { ModelStrength.Writing });
        writer.NonStreamContent = "DRAFT-READY";

        var synth = AddProvider("synth");
        SetProfile("synth", new[] { ModelStrength.General });
        synth.Deltas = new[] { "综合", "结论" };

        return (planner, analyst, writer, synth);
    }

    private DecomposeStrategy MakeStrategy() =>
        new(Sessions, Runner, Resolver, Assigner,
            new AeroAgent.Moa.Planning.TaskPlanner(Runner),
            new AeroAgent.Moa.Aggregation.Synthesizer(Runner),
            Options);

    private static string LastPrompt(AeroCode.Tests.ConversationTests.ScriptedProvider provider) =>
        provider.LastRequestMessages!.Last().Content;

    [Fact]
    public async Task Decompose_Unconfigured_PureChainPlan_AutoSelectsHandoffAndCompletes()
    {
        // 默认（未显式配置）+ 纯顺序链 → 自动判定 Handoff：链式执行真实完成，
        // 每级收到前一级产出（顺序控制权转移 + 上下文移交），整轮照常合成。
        Assert.Null(Options.OrchestrationPrimitive); // 前置：默认 null = 未显式配置（R2 缝合 #20 可空收口）
        var squad = SetupSquad();

        var facade = MakeFacade(MakeStrategy());
        var session = await NewSessionAsync(OrchestrationStrategy.Decompose);
        var events = await CollectAsync(facade.SendAsync(session.Id, "调研并成文并终校"));

        Assert.DoesNotContain(events, e => e is MessageFailedEvent);
        // s3（writer 最后一次调用）收到 s2 的产出：链式顺序控制权转移 + 上下文移交真实生效。
        Assert.Contains("DRAFT-READY", LastPrompt(squad.Writer), StringComparison.Ordinal);
        var messages = (await Sessions.GetMessagesAsync(session.Id)).Value!;
        var synthMsg = messages.Single(m => m.OrchestrationRole == StrategyRole.Synthesizer);
        Assert.Equal(MessageStatus.Completed, synthMsg.Status);
    }

    [Fact]
    public async Task Decompose_ExplicitHandoff_StillWinsOnParallelPlan()
    {
        // 显式配置优先：并行计划 + 显式 Handoff → 串接移交照旧生效（与自动判定无关的显式路径）。
        Options.OrchestrationPrimitive = OrchestrationPrimitive.Handoff;
        var squad = SetupSquad();
        squad.Planner.NonStreamContent = ParallelPlan;

        var facade = MakeFacade(MakeStrategy());
        var session = await NewSessionAsync(OrchestrationStrategy.Decompose);
        var events = await CollectAsync(facade.SendAsync(session.Id, "调研并写一篇文章"));

        Assert.DoesNotContain(events, e => e is MessageFailedEvent);
        Assert.Contains("RESEARCH-DONE", LastPrompt(squad.Writer), StringComparison.Ordinal); // 无依赖步骤也被串接移交
        var messages = (await Sessions.GetMessagesAsync(session.Id)).Value!;
        var synthMsg = messages.Single(m => m.OrchestrationRole == StrategyRole.Synthesizer);
        Assert.Equal(MessageStatus.Completed, synthMsg.Status);
    }

    [Fact]
    public async Task Decompose_Unconfigured_ParallelPlan_StaysBaselineAgentsAsTool()
    {
        // 默认（未显式配置）+ 扇出形态 → 自动判定 AgentsAsTool = 基线：无依赖不回灌彼此产出。
        var squad = SetupSquad();
        squad.Planner.NonStreamContent = ParallelPlan;

        var facade = MakeFacade(MakeStrategy());
        var session = await NewSessionAsync(OrchestrationStrategy.Decompose);
        var events = await CollectAsync(facade.SendAsync(session.Id, "调研并写一篇文章"));

        Assert.DoesNotContain(events, e => e is MessageFailedEvent);
        Assert.DoesNotContain("RESEARCH-DONE", LastPrompt(squad.Writer), StringComparison.Ordinal);
        var messages = (await Sessions.GetMessagesAsync(session.Id)).Value!;
        var synthMsg = messages.Single(m => m.OrchestrationRole == StrategyRole.Synthesizer);
        Assert.Equal(MessageStatus.Completed, synthMsg.Status);
    }
}
