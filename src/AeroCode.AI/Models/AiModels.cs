using System.Collections.Generic;

namespace AeroCode.AI.Models;

/// <summary>
/// 单条消息。多轮对话 = 多条 Message 拼接。
/// role: system / user / assistant / tool
/// </summary>
public sealed class ChatMessage
{
    public string Role { get; init; } = "user";
    public string Content { get; init; } = string.Empty;
    public string? Name { get; init; } // for tool role
    public string? ToolCallId { get; init; }
    public IReadOnlyList<ToolCall>? ToolCalls { get; init; }
    public string? ReasoningContent { get; init; } // DeepSeek thinking output
}

public sealed class ToolCall
{
    public string Id { get; init; } = string.Empty;
    public string Type { get; init; } = "function";
    public string FunctionName { get; init; } = string.Empty;
    public string ArgumentsJson { get; init; } = "{}";
}

public sealed class ToolDefinition
{
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    /// <summary>JSON Schema 描述参数。序列化时直接使用,不二次解析。</summary>
    public string ParametersJsonSchema { get; init; } = "{\"type\":\"object\"}";
}

public sealed class ChatRequest
{
    public string Model { get; init; } = string.Empty;
    public IReadOnlyList<ChatMessage> Messages { get; init; } = new List<ChatMessage>();
    public IReadOnlyList<ToolDefinition>? Tools { get; init; }
    public double? Temperature { get; init; }
    public int? MaxTokens { get; init; }
    public bool Stream { get; init; }
    /// <summary>是否启用 thinking/reasoning (DeepSeek V4 强制要求)</summary>
    public bool EnableThinking { get; init; } = true;
    /// <summary>thinking 强度: low / medium / high (DeepSeek V4: high/max)</summary>
    public string? ThinkingEffort { get; init; } = "high";
}

public sealed class ChatResponse
{
    public string Id { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
    public string? ReasoningContent { get; init; }
    public IReadOnlyList<ToolCall> ToolCalls { get; init; } = new List<ToolCall>();
    public string FinishReason { get; init; } = "stop";
    public UsageInfo? Usage { get; init; }
}

public sealed class UsageInfo
{
    public int PromptTokens { get; init; }
    public int CompletionTokens { get; init; }
    public int TotalTokens { get; init; }
    public int? CachedTokens { get; init; }
    public int? ReasoningTokens { get; init; }
}

/// <summary>
/// 流式响应片段。Provider 在累积 reasoning_content / content / tool_calls 增量。
/// </summary>
public sealed class ChatChunk
{
    public string Id { get; init; } = string.Empty;
    public string? DeltaContent { get; init; }
    public string? DeltaReasoning { get; init; }
    public IReadOnlyList<ToolCall> DeltaToolCalls { get; init; } = new List<ToolCall>();
    public string? FinishReason { get; init; }
}
