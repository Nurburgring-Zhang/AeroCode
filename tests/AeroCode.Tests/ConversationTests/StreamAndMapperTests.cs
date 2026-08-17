using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AeroAgent.Conversation.Models;
using AeroAgent.Conversation.Orchestration;
using AeroCode.AI.Models;
using Xunit;
using ChatMessage = AeroAgent.Conversation.Models.ChatMessage;

namespace AeroCode.Tests.ConversationTests;

public class StreamAggregatorTests
{
    private static async IAsyncEnumerable<ChatChunk> Chunks(params string?[] deltas)
    {
        foreach (var d in deltas)
        {
            await Task.Yield();
            yield return new ChatChunk { DeltaContent = d };
        }
    }

    [Fact]
    public async Task Consume_AccumulatesContent_AndCallsDeltas()
    {
        var seen = new List<string>();
        var result = await StreamAggregator.ConsumeAsync(
            Chunks("你好", "，", "世界"),
            delta => seen.Add(delta),
            null,
            CancellationToken.None);

        Assert.Equal("你好，世界", result.Content);
        Assert.Equal(new[] { "你好", "，", "世界" }, seen);
        Assert.Null(result.ReasoningContent);
        Assert.True(result.ElapsedMs >= 0);
    }

    [Fact]
    public async Task Consume_AccumulatesReasoning()
    {
        async IAsyncEnumerable<ChatChunk> Stream()
        {
            await Task.Yield();
            yield return new ChatChunk { DeltaReasoning = "思考1" };
            yield return new ChatChunk { DeltaContent = "答案" };
            yield return new ChatChunk { DeltaReasoning = "思考2", FinishReason = "stop" };
        }

        var reasoning = new List<string>();
        var result = await StreamAggregator.ConsumeAsync(Stream(), null, reasoning.Add, CancellationToken.None);

        Assert.Equal("答案", result.Content);
        Assert.Equal("思考1思考2", result.ReasoningContent);
        Assert.Equal("stop", result.FinishReason);
        Assert.Equal(2, reasoning.Count);
    }

    [Fact]
    public async Task Consume_EmptyStream_ReturnsEmpty()
    {
        var result = await StreamAggregator.ConsumeAsync(
            Chunks(), null, null, CancellationToken.None);
        Assert.Equal(string.Empty, result.Content);
        Assert.Null(result.ReasoningContent);
    }
}

public class HistoryMapperTests
{
    [Fact]
    public void MapsRoles_AndSkipsEmptyAndFailed()
    {
        var history = new List<ChatMessage>
        {
            new() { Role = ChatRole.User, Content = "问题", Status = MessageStatus.Completed },
            new() { Role = ChatRole.Assistant, Content = "回答", Status = MessageStatus.Completed },
            new() { Role = ChatRole.Assistant, Content = "失败的", Status = MessageStatus.Failed },
            new() { Role = ChatRole.Assistant, Content = "", Status = MessageStatus.Completed },
            new() { Role = ChatRole.System, Content = "系统提示", Status = MessageStatus.Completed },
            new() { Role = ChatRole.Assistant, Content = "被取消", Status = MessageStatus.Cancelled },
        };

        var mapped = HistoryMapper.ToProviderMessages(history);

        Assert.Equal(3, mapped.Count);
        Assert.Equal("user", mapped[0].Role);
        Assert.Equal("问题", mapped[0].Content);
        Assert.Equal("assistant", mapped[1].Role);
        Assert.Equal("system", mapped[2].Role);
    }

    [Fact]
    public void DegradedMessages_StillEnterContext()
    {
        // 降级消息有真实产出，应保留在上下文。
        var history = new List<ChatMessage>
        {
            new() { Role = ChatRole.Assistant, Content = "部分结果", Status = MessageStatus.Degraded },
        };
        var mapped = HistoryMapper.ToProviderMessages(history);
        Assert.Single(mapped);
    }

    [Fact]
    public void Intermediates_FilteredOut_FinalAndLegacyKept()
    {
        // P1-1 回归：编排中间产物（IsFinal==false）绝不回灌模型上下文；
        // 最终答复（true）与早期数据（null）保留。
        var history = new List<ChatMessage>
        {
            new() { Role = ChatRole.User, Content = "第一问", Status = MessageStatus.Completed },
            new() { Role = ChatRole.Assistant, Content = "路由分类JSON", Status = MessageStatus.Completed, IsFinal = false },
            new() { Role = ChatRole.Assistant, Content = "候选A", Status = MessageStatus.Completed, IsFinal = false },
            new() { Role = ChatRole.Assistant, Content = "规划JSON", Status = MessageStatus.Completed, IsFinal = false },
            new() { Role = ChatRole.Assistant, Content = "最终答复", Status = MessageStatus.Completed, IsFinal = true },
            new() { Role = ChatRole.Assistant, Content = "旧版本消息", Status = MessageStatus.Completed, IsFinal = null },
        };

        var mapped = HistoryMapper.ToProviderMessages(history);

        Assert.Equal(3, mapped.Count);
        Assert.Equal("第一问", mapped[0].Content);
        Assert.Equal("最终答复", mapped[1].Content);
        Assert.Equal("旧版本消息", mapped[2].Content);
        Assert.DoesNotContain(mapped, m => m.Content.Contains("JSON") || m.Content == "候选A");
    }

    [Fact]
    public void IntermediatesFlag_OnNonAssistantRoles_NotFiltered()
    {
        // IsFinal 语义只约束助手消息；用户消息永远进上下文。
        var history = new List<ChatMessage>
        {
            new() { Role = ChatRole.User, Content = "用户消息", Status = MessageStatus.Completed, IsFinal = false },
        };

        var mapped = HistoryMapper.ToProviderMessages(history);
        Assert.Single(mapped);
        Assert.Equal("用户消息", mapped[0].Content);
    }

    private static string ToolCallsJson(string callId, string toolName, string argsJson = "{}")
        => JsonSerializer.Serialize(new List<ToolCall>
        {
            new() { Id = callId, Type = "function", FunctionName = toolName, ArgumentsJson = argsJson },
        });

    [Fact]
    public void ToolSequence_PairedTurnAndResult_BothReplayed()
    {
        // 工具循环产物：助手 tool_calls 轮虽然 IsFinal==false 且正文为空，
        // 但必须与紧随的 tool 结果一起回灌，否则严格 API 直接报消息序列错误。
        var history = new List<ChatMessage>
        {
            new() { Role = ChatRole.User, Content = "读笔记", Status = MessageStatus.Completed },
            new()
            {
                Role = ChatRole.Assistant,
                Content = string.Empty,
                ToolCallsJson = ToolCallsJson("call-1", "get_note", "{\"id\":\"n1\"}"),
                IsFinal = false,
                Status = MessageStatus.Completed,
            },
            new()
            {
                Role = ChatRole.Tool,
                Content = "笔记正文",
                Name = "get_note",
                ToolCallId = "call-1",
                IsFinal = false,
                Status = MessageStatus.Completed,
            },
            new() { Role = ChatRole.Assistant, Content = "最终答复", IsFinal = true, Status = MessageStatus.Completed },
        };

        var mapped = HistoryMapper.ToProviderMessages(history);

        Assert.Equal(4, mapped.Count);
        Assert.Equal("user", mapped[0].Role);

        Assert.Equal("assistant", mapped[1].Role);
        var call = Assert.Single(mapped[1].ToolCalls!);
        Assert.Equal("call-1", call.Id);
        Assert.Equal("get_note", call.FunctionName);
        Assert.Equal("{\"id\":\"n1\"}", call.ArgumentsJson);

        Assert.Equal("tool", mapped[2].Role);
        Assert.Equal("笔记正文", mapped[2].Content);
        Assert.Equal("get_note", mapped[2].Name);
        Assert.Equal("call-1", mapped[2].ToolCallId);

        Assert.Equal("最终答复", mapped[3].Content);
    }

    [Fact]
    public void OrphanToolResult_Dropped()
    {
        // 没有对应助手 tool_calls 轮的 tool 结果是孤儿（脏数据/被过滤），
        // 必须丢弃——严格角色交替 API 不允许凭空出现 tool 消息。
        var history = new List<ChatMessage>
        {
            new() { Role = ChatRole.User, Content = "问题", Status = MessageStatus.Completed },
            new()
            {
                Role = ChatRole.Tool,
                Content = "孤儿结果",
                Name = "get_note",
                ToolCallId = "call-missing",
                IsFinal = false,
                Status = MessageStatus.Completed,
            },
        };

        var mapped = HistoryMapper.ToProviderMessages(history);

        var only = Assert.Single(mapped);
        Assert.Equal("user", only.Role);
    }

    [Fact]
    public void CorruptedToolCallsJson_HonestDegradation()
    {
        // 损坏的 ToolCallsJson：助手轮正文为空则整轮丢弃，其孤儿 tool 结果一并丢弃。
        var history = new List<ChatMessage>
        {
            new()
            {
                Role = ChatRole.Assistant,
                Content = string.Empty,
                ToolCallsJson = "{这不是JSON",
                IsFinal = false,
                Status = MessageStatus.Completed,
            },
            new()
            {
                Role = ChatRole.Tool,
                Content = "结果",
                Name = "get_note",
                ToolCallId = "call-x",
                IsFinal = false,
                Status = MessageStatus.Completed,
            },
            new() { Role = ChatRole.User, Content = "下一问", Status = MessageStatus.Completed },
        };

        var mapped = HistoryMapper.ToProviderMessages(history);

        var only = Assert.Single(mapped);
        Assert.Equal("下一问", only.Content);
    }

    [Fact]
    public void FailedToolResult_Skipped_ButTurnKept()
    {
        // 失败的 tool 结果内容不完整不进上下文；助手轮本身保留。
        var history = new List<ChatMessage>
        {
            new()
            {
                Role = ChatRole.Assistant,
                Content = string.Empty,
                ToolCallsJson = ToolCallsJson("call-f", "get_note"),
                IsFinal = false,
                Status = MessageStatus.Completed,
            },
            new()
            {
                Role = ChatRole.Tool,
                Content = "半截输出",
                Name = "get_note",
                ToolCallId = "call-f",
                IsFinal = false,
                Status = MessageStatus.Failed,
            },
        };

        var mapped = HistoryMapper.ToProviderMessages(history);

        var only = Assert.Single(mapped);
        Assert.Equal("assistant", only.Role);
        Assert.NotNull(only.ToolCalls);
    }
}
