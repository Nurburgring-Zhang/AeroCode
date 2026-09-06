// Copyright (c) AeroCode
// A5 双编排原语钉子：MoaOptions 默认值 = AgentsAsTool（= 现行为，保基线）、
// PrimitiveSelectionPolicy 形态判定（扇出→AgentsAsTool / 顺序链与上下文重→Handoff /
// 显式配置优先）、FromPlan 形态推导、JsonMoaOptionsStore 往返与旧 JSON 兼容、
// DecomposeStrategy 接线（Handoff 串接可见的上下文移交；默认路径不变）。
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AeroAgent.Conversation.Models;
using AeroAgent.Conversation.Orchestration;
using AeroAgent.Moa.Profiles;
using AeroAgent.Moa.Strategies;
using Xunit;
using PlanStep = AeroCode.Harness.Planner.PlanStep;

namespace AeroCode.Tests.MoaTests;

/// <summary>原语枚举 / 形态推导 / 选择策略 / 选项序列化的纯函数钉子。</summary>
public sealed class OrchestrationPrimitiveTests
{
    // ---------- MoaOptions 默认值（R2 缝合 #20：null = 未显式配置 = 形态自动判定） ----------

    [Fact]
    public void MoaOptions_DefaultPrimitive_IsNull_AutoDetect()
    {
        // 可空收口后默认 null（未显式配置 → DecomposeStrategy 走形态自动判定）；
        // 旧非空枚举无法区分「默认」与「显式选 AgentsAsTool」的 F-M2 根因由此消除。
        Assert.Null(new MoaOptions().OrchestrationPrimitive);
    }

    // ---------- PrimitiveTaskShape.FromPlan 形态推导 ----------

    [Fact]
    public void FromPlan_ParallelSteps_Width2Depth1()
    {
        var shape = PrimitiveTaskShape.FromPlan(new[]
        {
            new PlanStep { Id = "s1", Title = "调研" },
            new PlanStep { Id = "s2", Title = "成文" },
        });

        Assert.Equal(2, shape.FanOutWidth); // 同层两步互相独立（可并行）
        Assert.Equal(1, shape.ChainDepth);
    }

    [Fact]
    public void FromPlan_PureChain_Width1DepthN()
    {
        var shape = PrimitiveTaskShape.FromPlan(new[]
        {
            new PlanStep { Id = "s1", Title = "甲" },
            new PlanStep { Id = "s2", Title = "乙", DependsOn = new[] { "s1" } },
            new PlanStep { Id = "s3", Title = "丙", DependsOn = new[] { "s2" } },
        });

        Assert.Equal(1, shape.FanOutWidth);
        Assert.Equal(3, shape.ChainDepth);
    }

    [Fact]
    public void FromPlan_MixedDag_WidthReflectsWidestLevel()
    {
        var shape = PrimitiveTaskShape.FromPlan(new[]
        {
            new PlanStep { Id = "s1", Title = "根" },
            new PlanStep { Id = "s2", Title = "左", DependsOn = new[] { "s1" } },
            new PlanStep { Id = "s3", Title = "右", DependsOn = new[] { "s1" } },
        });

        Assert.Equal(2, shape.FanOutWidth); // s2/s3 同层可并行
        Assert.Equal(2, shape.ChainDepth);
    }

    [Fact]
    public void FromPlan_EmptyPlan_ZeroShape()
    {
        var shape = PrimitiveTaskShape.FromPlan(Array.Empty<PlanStep>());
        Assert.Equal(0, shape.FanOutWidth);
        Assert.Equal(0, shape.ChainDepth);
    }

    // ---------- PrimitiveSelectionPolicy 判定 ----------

    [Fact]
    public void Policy_FanOutOrParallelShape_SelectsAgentsAsTool()
    {
        Assert.Equal(OrchestrationPrimitive.AgentsAsTool,
            PrimitiveSelectionPolicy.Select(new PrimitiveTaskShape(2, 1)).Primitive);
        Assert.Equal(OrchestrationPrimitive.AgentsAsTool,
            PrimitiveSelectionPolicy.Select(new PrimitiveTaskShape(1, 1)).Primitive); // 平凡单步
    }

    [Fact]
    public void Policy_PureChainShape_SelectsHandoff()
    {
        var decision = PrimitiveSelectionPolicy.Select(new PrimitiveTaskShape(1, 2));
        Assert.Equal(OrchestrationPrimitive.Handoff, decision.Primitive);
        Assert.False(string.IsNullOrWhiteSpace(decision.Rationale)); // 判定依据可读留痕
    }

    [Fact]
    public void Policy_ContextHeavy_SelectsHandoff()
    {
        Assert.Equal(OrchestrationPrimitive.Handoff,
            PrimitiveSelectionPolicy.Select(new PrimitiveTaskShape(3, 1, ContextHeavy: true)).Primitive);
    }

    [Fact]
    public void Policy_ExplicitConfig_OverridesShape()
    {
        // 显式配置优先：扇出形态配置 Handoff → Handoff；纯链配置 AgentsAsTool → AgentsAsTool。
        Assert.Equal(OrchestrationPrimitive.Handoff,
            PrimitiveSelectionPolicy.Select(new PrimitiveTaskShape(2, 1), OrchestrationPrimitive.Handoff).Primitive);
        Assert.Equal(OrchestrationPrimitive.AgentsAsTool,
            PrimitiveSelectionPolicy.Select(new PrimitiveTaskShape(1, 3), OrchestrationPrimitive.AgentsAsTool).Primitive);
    }

    // ---------- JsonMoaOptionsStore 往返 + 旧 JSON 兼容 ----------

    [Fact]
    public async Task JsonMoaOptionsStore_RoundTripsPrimitive()
    {
        var path = Path.Combine(Path.GetTempPath(), $"moaopts_{Guid.NewGuid():N}.json");
        try
        {
            var store = new JsonMoaOptionsStore(path);
            var options = new MoaOptions { OrchestrationPrimitive = OrchestrationPrimitive.Handoff };
            await store.SaveAsync(options);

            // 枚举按字符串落盘（与 DefaultStrategy 同口径）
            Assert.Contains("Handoff", await File.ReadAllTextAsync(path));

            var loaded = await store.LoadAsync();
            Assert.Equal(OrchestrationPrimitive.Handoff, loaded.OrchestrationPrimitive);
            Assert.True(loaded.ToolsEnabled); // 其余字段不受影响
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task JsonMoaOptionsStore_LegacyJsonWithoutField_DefaultsToNullAutoDetect()
    {
        // 旧 moaoptions.json（无该字段）→ 默认 null（未显式配置 = 形态自动判定，向后兼容）。
        var path = Path.Combine(Path.GetTempPath(), $"moaopts_{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(path,
                """{"DefaultStrategy":"Single","EnsembleSize":2,"ToolsEnabled":true}""");

            var loaded = await new JsonMoaOptionsStore(path).LoadAsync();
            Assert.Null(loaded.OrchestrationPrimitive);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}

/// <summary>DecomposeStrategy 原语接线钉子：Handoff 串接产生可见的上下文移交；默认路径与基线一致。</summary>
public sealed class OrchestrationPrimitiveDecomposeTests : MoaTestBase
{
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
        planner.NonStreamContent = ParallelPlan;

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

    private DecomposeStrategy MakeStrategy() =>
        new(Sessions, Runner, Resolver, Assigner,
            new AeroAgent.Moa.Planning.TaskPlanner(Runner),
            new AeroAgent.Moa.Aggregation.Synthesizer(Runner),
            Options);

    private static string LastPrompt(AeroCode.Tests.ConversationTests.ScriptedProvider provider) =>
        provider.LastRequestMessages!.Last().Content;

    [Fact]
    public async Task Decompose_HandoffConfigured_TransfersControlWithUpstreamResult()
    {
        // Handoff：planner 顺序串接成链（原计划 s1/s2 无依赖）→ writer（s2）收到 s1 的产出
        // （顺序控制权转移随上下文移交），整轮照常完成合成。
        Options.OrchestrationPrimitive = OrchestrationPrimitive.Handoff;
        var squad = SetupSquad();

        var facade = MakeFacade(MakeStrategy());
        var session = await NewSessionAsync(OrchestrationStrategy.Decompose);

        var events = await CollectAsync(facade.SendAsync(session.Id, "调研并写一篇文章"));

        Assert.DoesNotContain(events, e => e is MessageFailedEvent);
        var writerPrompt = LastPrompt(squad.Writer);
        Assert.Contains("ALPHA-RESULT", writerPrompt); // 串接生效：s2 拿到 s1 的结果
        Assert.Contains("调研并写一篇文章", writerPrompt);

        var messages = (await Sessions.GetMessagesAsync(session.Id)).Value!;
        var synthMsg = messages.Single(m => m.OrchestrationRole == StrategyRole.Synthesizer);
        Assert.Equal(MessageStatus.Completed, synthMsg.Status);
        Assert.Equal("综合结论", synthMsg.Content);
    }

    [Fact]
    public async Task Decompose_DefaultAgentsAsTool_KeepsBaselineParallelBehavior()
    {
        // 默认（AgentsAsTool）= 现行为：无依赖子任务不回灌彼此产出，整轮照常完成。
        var squad = SetupSquad();

        var facade = MakeFacade(MakeStrategy());
        var session = await NewSessionAsync(OrchestrationStrategy.Decompose);

        var events = await CollectAsync(facade.SendAsync(session.Id, "调研并写一篇文章"));

        Assert.DoesNotContain(events, e => e is MessageFailedEvent);
        Assert.NotNull(squad.Writer.LastRequestMessages);
        Assert.DoesNotContain("ALPHA-RESULT", LastPrompt(squad.Writer)); // 基线：无依赖 → 不移交 s1 产出

        var messages = (await Sessions.GetMessagesAsync(session.Id)).Value!;
        var synthMsg = messages.Single(m => m.OrchestrationRole == StrategyRole.Synthesizer);
        Assert.Equal(MessageStatus.Completed, synthMsg.Status);
    }
}
