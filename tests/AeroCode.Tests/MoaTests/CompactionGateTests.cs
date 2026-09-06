// Copyright (c) AeroCode
// G2-4 压缩路径真实单测（Reviewer-H P1-1 零覆盖修复）：WorkerRunner.CompactIfOverflowing /
// DropBrokenToolPairsAtHead / EstimateTokens 的行为钉子——未超阈值原样返回、超阈值压缩后
// 请求序列 tool 配对自洽、破对头部清理、压缩失败继续未压缩、边界（空/仅 system），
// 以及压缩器装配后的工具循环端到端（第二轮回灌序列配对完整）。
// 全部真实：真实 Compactor + 真实 EventBus + 真实 SQLite 会话库 + 可编程 provider（仓库既有约定）。
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AeroAgent.Conversation.Models;
using AeroAgent.Conversation.Orchestration;
using AeroAgent.Moa.Assignment;
using AeroAgent.Moa.Curation;
using AeroAgent.Moa.Profiles;
using AeroAgent.Moa.Strategies;
using AeroAgent.Moa.Tools;
using AeroCode.AI.Models;
using AeroCode.Harness.Compaction;
using AeroCode.Harness.EventBus;
using AeroCode.Harness.Permission;
using Xunit;
using AiChatMessage = AeroCode.AI.Models.ChatMessage;
using ChatMessage = AeroAgent.Conversation.Models.ChatMessage;

namespace AeroCode.Tests.MoaTests;

/// <summary>
/// 压缩门测试：桌面端默认启用溢出压缩（Settings.Compaction.ThresholdTokens=24000），
/// 该路径此前零测试覆盖。压缩若拆散 assistant tool_calls 与 tool 应答对，
/// 发给 provider 的请求序列非法——以下用例钉住"压缩后必配对自洽"不变量。
/// </summary>
public sealed class CompactionGateTests : MoaTestBase
{
    private static AiChatMessage User(string content) => new() { Role = "user", Content = content };

    private static AiChatMessage System(string content) => new() { Role = "system", Content = content };

    private static AiChatMessage AssistantToolCall(string callId, string tool = "get_note", string args = "{}") =>
        new()
        {
            Role = "assistant",
            Content = string.Empty,
            ToolCalls = new List<ToolCall>
            {
                new() { Id = callId, Type = "function", FunctionName = tool, ArgumentsJson = args },
            },
        };

    private static AiChatMessage ToolResult(string callId, string content = "RESULT") =>
        new() { Role = "tool", Content = content, ToolCallId = callId, Name = "get_note" };

    /// <summary>大内容制造溢出（4 字符 ≈ 1 token 口径）。</summary>
    private static string Huge(int tokens) => new('x', tokens * 4);

    private WorkerRunner NewGateRunner(Compactor? compactor, CompactionGateOptions? options) =>
        new(Sessions, Catalog, compactor: compactor, compaction: options);

    // ---------- CompactIfOverflowing：门控与阈值行为 ----------

    [Fact]
    public void Gate_Disabled_NoCompactor_ReturnsOriginalInstance()
    {
        var conversation = new List<AiChatMessage> { User(Huge(6000)) };
        // 未装配压缩器（与批次 A 行为一致）
        var runner = NewGateRunner(compactor: null, options: new CompactionGateOptions { ThresholdTokens = 1000 });
        Assert.Same(conversation, runner.CompactIfOverflowing(conversation));
    }

    [Fact]
    public void Gate_Disabled_ZeroThreshold_ReturnsOriginalInstance()
    {
        var conversation = new List<AiChatMessage> { User(Huge(6000)) };
        // 压缩器在但阈值 0（CompactionGateOptions.Disabled）= 关闭溢出检测
        var runner = NewGateRunner(
            new Compactor(new EventBus(), CompactionStrategy.TruncateOldest, triggerThresholdPercent: 1),
            new CompactionGateOptions { ThresholdTokens = 0 });
        Assert.Same(conversation, runner.CompactIfOverflowing(conversation));
    }

    [Fact]
    public void Gate_BelowThreshold_ReturnsOriginalInstance()
    {
        var conversation = new List<AiChatMessage> { User("hello"), User("world") }; // ~3 tokens
        var runner = NewGateRunner(
            new Compactor(new EventBus(), CompactionStrategy.TruncateOldest, triggerThresholdPercent: 1),
            new CompactionGateOptions { ThresholdTokens = 100_000 });
        Assert.Same(conversation, runner.CompactIfOverflowing(conversation));
        Assert.Equal(2, conversation.Count);
    }

    [Fact]
    public void Gate_CompactionThrows_ContinuesUncompacted()
    {
        // Compactor 事件总线为 null → Compact 在发布事件时抛 → 门捕获后按未压缩继续（[DEGRADED] 兜底）
        var conversation = new List<AiChatMessage> { User(Huge(6000)) };
        var runner = NewGateRunner(
            new Compactor(null!, CompactionStrategy.TruncateOldest, triggerThresholdPercent: 1),
            new CompactionGateOptions { ThresholdTokens = 1000 });
        var result = runner.CompactIfOverflowing(conversation);
        Assert.Same(conversation, result); // 不抛出、不丢消息
        Assert.Single(result);
    }

    // ---------- CompactIfOverflowing：压缩后配对自洽 ----------

    [Fact]
    public void Gate_AboveThreshold_CompactedSequence_KeepsToolPairingIntact()
    {
        var conversation = new List<AiChatMessage>
        {
            System("sys"),
            User(Huge(5000)),
            AssistantToolCall("call-1"),
            ToolResult("call-1"),
        };
        var runner = NewGateRunner(
            new Compactor(new EventBus(), CompactionStrategy.SlidingWindow, keepRecentMessages: 2),
            new CompactionGateOptions { ThresholdTokens = 1000 });

        var result = runner.CompactIfOverflowing(conversation);

        // SlidingWindow 保留首条 system + 最近 2 条 → [system, assistant(tc), tool]
        Assert.Equal(3, result.Count);
        Assert.Equal("system", result[0].Role);
        Assert.NotNull(result[1].ToolCalls);
        Assert.Equal("call-1", result[1].ToolCalls![0].Id);
        Assert.Equal("tool", result[2].Role);
        Assert.Equal("call-1", result[2].ToolCallId);
        // 序列整体自洽：头部修复为幂等（不产生进一步丢弃）
        Assert.Equal(result.Count, WorkerRunner.DropBrokenToolPairsAtHead(result).Count);
    }

    [Fact]
    public void Gate_AboveThreshold_BrokenPairAtHead_IsDropped()
    {
        // 压缩窗口切进配对中间：tool 应答被保留、assistant 携带方被丢弃 → 头部修复删孤儿
        var conversation = new List<AiChatMessage>
        {
            User(Huge(5000)),
            AssistantToolCall("call-1"),
            ToolResult("call-1"),
            User("second question"),
        };
        var runner = NewGateRunner(
            new Compactor(new EventBus(), CompactionStrategy.SlidingWindow, keepRecentMessages: 2),
            new CompactionGateOptions { ThresholdTokens = 1000 });

        var result = runner.CompactIfOverflowing(conversation);

        var survivor = Assert.Single(result);
        Assert.Equal("user", survivor.Role);
        Assert.Equal("second question", survivor.Content);
    }

    [Fact]
    public void Gate_Boundary_OnlySystemMessages_NoDrop()
    {
        var conversation = new List<AiChatMessage> { System(Huge(5000)), System("second") };
        var runner = NewGateRunner(
            new Compactor(new EventBus(), CompactionStrategy.SlidingWindow, keepRecentMessages: 1),
            new CompactionGateOptions { ThresholdTokens = 1000 });

        var result = runner.CompactIfOverflowing(conversation);

        // SlidingWindow 保留首条 system（截断策略也不丢 system）——配对自洽
        Assert.Contains(result, m => m.Role == "system");
        Assert.Equal(result.Count, WorkerRunner.DropBrokenToolPairsAtHead(result).Count);
    }

    [Fact]
    public void Gate_Boundary_EmptyConversation_ReturnsEmpty()
    {
        var conversation = new List<AiChatMessage>();
        var runner = NewGateRunner(
            new Compactor(new EventBus(), CompactionStrategy.TruncateOldest, triggerThresholdPercent: 1),
            new CompactionGateOptions { ThresholdTokens = 1 });

        // 空 → 估算 0 < 阈值 → 原样返回，绝不进入压缩/修复分支
        Assert.Same(conversation, runner.CompactIfOverflowing(conversation));
    }

    // ---------- DropBrokenToolPairsAtHead：头部修复语义 ----------

    [Fact]
    public void Repair_IntactSequence_ReturnsAllMessages()
    {
        var messages = new List<AiChatMessage>
        {
            User("q"),
            AssistantToolCall("a"),
            ToolResult("a"),
            AssistantToolCall("b"),
            ToolResult("b"),
            User("done"),
        };

        var repaired = WorkerRunner.DropBrokenToolPairsAtHead(messages);

        Assert.Equal(messages.Count, repaired.Count);
    }

    [Fact]
    public void Repair_OrphanToolResponseAtHead_IsDropped()
    {
        var messages = new List<AiChatMessage>
        {
            ToolResult("orphan"),           // 携带方被压缩丢弃
            AssistantToolCall("kept"),
            ToolResult("kept"),
        };

        var repaired = WorkerRunner.DropBrokenToolPairsAtHead(messages);

        Assert.Equal(2, repaired.Count);
        Assert.Equal("kept", Assert.Single(repaired[0].ToolCalls!).Id);
        Assert.Equal("kept", repaired[1].ToolCallId);
    }

    [Fact]
    public void Repair_UnansweredToolCallAtHead_IsDropped_KeepsValidTail()
    {
        var messages = new List<AiChatMessage>
        {
            AssistantToolCall("lost"),      // 应答被压缩丢弃
            AssistantToolCall("answered"),
            ToolResult("answered"),
        };

        var repaired = WorkerRunner.DropBrokenToolPairsAtHead(messages);

        Assert.Equal(2, repaired.Count);
        Assert.Equal("answered", Assert.Single(repaired[0].ToolCalls!).Id);
        Assert.Equal("answered", repaired[1].ToolCallId);
    }

    [Fact]
    public void Repair_AllBroken_StillKeepsLastMessage()
    {
        var messages = new List<AiChatMessage> { ToolResult("orphan") };

        var repaired = WorkerRunner.DropBrokenToolPairsAtHead(messages);

        var survivor = Assert.Single(repaired); // 兜底：最新上下文不能全丢
        Assert.Equal("orphan", survivor.ToolCallId);
    }

    // ---------- EstimateTokens ----------

    [Fact]
    public void EstimateTokens_CountsContent_AndToolCallArgs()
    {
        var messages = new List<AiChatMessage>
        {
            User(new string('a', 400)),                         // 100 tokens
            new()
            {
                Role = "assistant",
                Content = new string('b', 80),                  // 20 tokens
                ToolCalls = new List<ToolCall>
                {
                    // 函数名 9 字符 + 参数 31 字符 = 40 字符 → 10 tokens
                    new() { Id = "c1", FunctionName = "run_shell", ArgumentsJson = new string('c', 31) },
                },
            },
            ToolResult("c1", new string('d', 120)),             // 30 tokens
        };

        Assert.Equal(160, WorkerRunner.EstimateTokens(messages));
    }

    // ---------- 端到端：压缩器装配进真实工具循环 ----------

    [Fact]
    public async Task ToolLoop_WithCompactor_SecondTurnRequest_IsPairingIntact()
    {
        var provider = AddProvider("compactor-loop");
        provider.ResponseQueue.Enqueue(new ChatResponse
        {
            Id = "resp-tc",
            ToolCalls = new List<ToolCall>
            {
                new() { Id = "call-1", Type = "function", FunctionName = "get_note", ArgumentsJson = "{}" },
            },
            FinishReason = "tool_calls",
        });
        provider.ResponseQueue.Enqueue(new ChatResponse { Id = "resp-final", Content = "done", FinishReason = "stop" });

        var box = new ScriptedToolbox("notes", new ToolDefinition { Name = "get_note", Description = "d" });
        box.SetResult("get_note", ToolInvokeResult.Ok("NOTE_BODY"));
        var registry = new ToolboxRegistry();
        registry.Register(box);
        var router = new ToolRouter(registry, PermissionPolicy.CreateDefault(new EventBus()),
            new ScriptedBroker(PermissionDecision.Allow));

        var runner = new WorkerRunner(
            Sessions, Catalog, tools: router,
            compactor: new Compactor(new EventBus(), CompactionStrategy.SlidingWindow, keepRecentMessages: 2),
            compaction: new CompactionGateOptions { ThresholdTokens = 1000 });

        var profile = SetProfile("compactor-loop", new[] { ModelStrength.General });
        var session = await NewSessionAsync(OrchestrationStrategy.Single);
        var ctx = new OrchestrationContext
        {
            Session = session,
            History = Array.Empty<ChatMessage>(),
            UserMessageId = "msg-user",
            Providers = Registry,
        };
        var assignment = new ModelAssignment("compactor-loop", string.Empty, profile);

        // 大 prompt：第 1 轮即溢出；第 2 轮上下文含 [user(huge), assistant(tc), tool]
        var outcome = await runner.RunAsync(
            ctx, assignment, StrategyRole.Worker, parentMessageId: null, label: null,
            new List<AiChatMessage> { User(Huge(5000)) },
            stream: false, isFinal: true, sink: null, budget: null, CancellationToken.None);

        Assert.True(outcome.Succeeded);
        Assert.Equal("done", outcome.Content);

        // 第二轮回灌序列：压缩窗口保留最近 2 条 → [assistant(tool_calls), tool]，配对完整
        var reFed = provider.LastRequestMessages!;
        Assert.Equal(2, reFed.Count);
        Assert.Equal("assistant", reFed[0].Role);
        Assert.Equal("call-1", Assert.Single(reFed[0].ToolCalls!).Id);
        Assert.Equal("tool", reFed[1].Role);
        Assert.Equal("call-1", reFed[1].ToolCallId);
        Assert.Equal("NOTE_BODY", reFed[1].Content);
    }

    // ---------- F-M3/F-M4（R1 审查修复）：C-CURATE 启用分支的 prefix-aware 压缩 ----------

    /// <summary>策展已接线的 runner（水位调高 = 策展器本身不触发，只启用 prefix-aware 压缩分支）。</summary>
    private WorkerRunner NewCuratorWiredRunner(Compactor compactor, CompactionGateOptions options, ToolRouter? router = null)
    {
        var runner = new WorkerRunner(Sessions, Catalog, tools: router, compactor: compactor, compaction: options);
        runner.Curator = new ContextCurator(new CurationOptions { WatermarkThresholdTokens = 1_000_000 });
        return runner;
    }

    [Fact]
    public void PrefixAware_CuratorWired_FrozenPrefixNotTruncated()
    {
        // 冻结前缀本身就超出压缩预算：prefix-aware 重载拒绝压缩（宁可不压也不动前缀）→ 原样返回。
        var systemMessage = System(Huge(2000));
        var conversation = new List<AiChatMessage>
        {
            systemMessage,
            User("question"),
            AssistantToolCall("call-1"),
            ToolResult("call-1", Huge(3000)),
        };
        var runner = NewCuratorWiredRunner(
            new Compactor(new EventBus(), CompactionStrategy.TruncateOldest, triggerThresholdPercent: 1),
            new CompactionGateOptions { ThresholdTokens = 1000 });

        var result = runner.CompactIfOverflowing(conversation, frozenPrefixCount: 1);

        Assert.Same(conversation, result);
        Assert.Equal(4, result.Count);
        Assert.Same(systemMessage, result[0]);
    }

    [Fact]
    public void PrefixAware_CuratorWired_TruncatesOnlyDialogueZone()
    {
        var systemMessage = System("stable system prompt");
        var conversation = new List<AiChatMessage>
        {
            systemMessage,
            User(Huge(5000)),
            AssistantToolCall("call-1"),
            ToolResult("call-1", Huge(3000)),
        };
        var runner = NewCuratorWiredRunner(
            new Compactor(new EventBus(), CompactionStrategy.SlidingWindow, keepRecentMessages: 2),
            new CompactionGateOptions { ThresholdTokens = 1000 });

        var result = runner.CompactIfOverflowing(conversation, frozenPrefixCount: 1);

        // 冻结前缀逐字保留（同实例）；压缩只作用于对话区；tool 配对完整。
        Assert.Same(systemMessage, result[0]);
        Assert.Equal(3, result.Count);
        Assert.Equal("assistant", result[1].Role);
        Assert.Equal("call-1", Assert.Single(result[1].ToolCalls!).Id);
        Assert.Equal("tool", result[2].Role);
        Assert.Equal("call-1", result[2].ToolCallId);
    }

    [Fact]
    public void Legacy_CuratorNotWired_PrefixArgumentHasNoEffect()
    {
        // 未启用分支（Curator null）：带不带冻结边界都必须与基线路径逐条一致（禁动 legacy）。
        var conversation = new List<AiChatMessage>
        {
            System(Huge(2000)),
            User(Huge(5000)),
            AssistantToolCall("call-1"),
            ToolResult("call-1", Huge(3000)),
        };
        var runner = new WorkerRunner(
            Sessions, Catalog,
            compactor: new Compactor(new EventBus(), CompactionStrategy.TruncateOldest, triggerThresholdPercent: 1),
            compaction: new CompactionGateOptions { ThresholdTokens = 1000 });

        var legacy = runner.CompactIfOverflowing(conversation);
        var withBoundary = runner.CompactIfOverflowing(conversation, frozenPrefixCount: 1);

        Assert.Equal(legacy.Select(m => m.Role), withBoundary.Select(m => m.Role));
        Assert.Equal(legacy.Select(m => m.Content), withBoundary.Select(m => m.Content));
        // 基线事实：legacy 头部修复可能丢弃 system 稳定段（既有语义不动，边界漂移由启用分支修复）。
        Assert.DoesNotContain(withBoundary, m => m.Role == "system");
    }

    [Fact]
    public async Task ToolLoop_CuratorWired_SecondTurnRequest_KeepsFrozenPrefix()
    {
        var provider = AddProvider("curator-loop");
        provider.ResponseQueue.Enqueue(new ChatResponse
        {
            Id = "resp-tc",
            ToolCalls = new List<ToolCall>
            {
                new() { Id = "call-1", Type = "function", FunctionName = "get_note", ArgumentsJson = "{}" },
            },
            FinishReason = "tool_calls",
        });
        provider.ResponseQueue.Enqueue(new ChatResponse { Id = "resp-final", Content = "done", FinishReason = "stop" });

        var box = new ScriptedToolbox("notes", new ToolDefinition { Name = "get_note", Description = "d" });
        box.SetResult("get_note", ToolInvokeResult.Ok("NOTE_BODY"));
        var registry = new ToolboxRegistry();
        registry.Register(box);
        var router = new ToolRouter(registry, PermissionPolicy.CreateDefault(new EventBus()),
            new ScriptedBroker(PermissionDecision.Allow));

        // 必须接线工具路由，否则 WorkerRunner 走单轮路径、CompactIfOverflowing 不在工具循环中执行。
        var runner = NewCuratorWiredRunner(
            new Compactor(new EventBus(), CompactionStrategy.TruncateOldest, triggerThresholdPercent: 1),
            new CompactionGateOptions { ThresholdTokens = 1000 }, router);

        var profile = SetProfile("curator-loop", new[] { ModelStrength.General });
        var session = await NewSessionAsync(OrchestrationStrategy.Single);
        var ctx = new OrchestrationContext
        {
            Session = session,
            History = Array.Empty<ChatMessage>(),
            UserMessageId = "msg-user",
            Providers = Registry,
        };
        var assignment = new ModelAssignment("curator-loop", string.Empty, profile);

        var outcome = await runner.RunAsync(
            ctx, assignment, StrategyRole.Worker, parentMessageId: null, label: null,
            new List<AiChatMessage> { System("stable system"), User(Huge(5000)) },
            stream: false, isFinal: true, sink: null, budget: null, CancellationToken.None);

        Assert.True(outcome.Succeeded);

        // 每轮重算的冻结边界生效：第 2 轮请求仍以冻结 system 前缀开头，tool 配对完整。
        var reFed = provider.LastRequestMessages!;
        Assert.Equal(3, reFed.Count);
        Assert.Equal("system", reFed[0].Role);
        Assert.Equal("stable system", reFed[0].Content);
        Assert.Equal("assistant", reFed[1].Role);
        Assert.Equal("call-1", Assert.Single(reFed[1].ToolCalls!).Id);
        Assert.Equal("tool", reFed[2].Role);
        Assert.Equal("call-1", reFed[2].ToolCallId);
    }
}
