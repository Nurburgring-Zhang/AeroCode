// Copyright (c) AeroCode
// TodoToolbox — 会话 Todo 工具域（批次 B G1）。真实读写 todo_items 表（经 ITodoStore），
// 按 SessionId 会话隔离。当前会话 Id 由注入的访问器提供（工具箱常驻注册中心单例，
// 会话级绑定经访问器在调用时解析，避免每会话重复注册）。
using System;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AeroAgent.Conversation.Services;
using AeroCode.AI.Models;

namespace AeroAgent.Moa.Tools;

/// <summary>
/// todo_* 工具域：todo_add / todo_list / todo_update / todo_delete 四个真实工具。
/// 所有参数在域内自行解析与校验；域内失败以 <see cref="ToolInvokeResult.Fail"/>
/// 如实返回，永不抛业务异常（ToolboxRegistry 契约）。
/// </summary>
public sealed class TodoToolbox : IWorkerToolbox
{
    private readonly ITodoStore _store;
    private readonly Func<string> _currentSessionId;
    private readonly IReadOnlyList<ToolDefinition> _definitions;

    /// <summary>
    /// 构造。<paramref name="currentSessionId"/> 返回当前活跃会话 Id（空串/空白 =
    /// 无活跃会话，调用如实失败）；每次调用时求值，支持会话切换。
    /// </summary>
    public TodoToolbox(ITodoStore store, Func<string> currentSessionId)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _currentSessionId = currentSessionId ?? throw new ArgumentNullException(nameof(currentSessionId));
        _definitions = new List<ToolDefinition>
        {
            new()
            {
                Name = "todo_add",
                Description = "Add an item to the current session's task list (persistent, session-scoped). " +
                              "Args: {\"content\": string (required)}.",
                ParametersJsonSchema = """{"type":"object","properties":{"content":{"type":"string"}},"required":["content"]}""",
            },
            new()
            {
                Name = "todo_list",
                Description = "List all items in the current session's task list with completion state (persistent). Args: {}.",
                ParametersJsonSchema = """{"type":"object"}""",
            },
            new()
            {
                Name = "todo_update",
                Description = "Update a task list item: mark done/undone and/or change its content. " +
                              "Args: {\"id\": string (required), \"completed\": bool (optional), \"content\": string (optional)}.",
                ParametersJsonSchema = """{"type":"object","properties":{"id":{"type":"string"},"completed":{"type":"boolean"},"content":{"type":"string"}},"required":["id"]}""",
            },
            new()
            {
                Name = "todo_delete",
                Description = "Delete one item from the current session's task list. " +
                              "Args: {\"id\": string (required)}.",
                ParametersJsonSchema = """{"type":"object","properties":{"id":{"type":"string"}},"required":["id"]}""",
            },
        };
    }

    /// <inheritdoc/>
    public string Domain => "todo";

    /// <inheritdoc/>
    public IReadOnlyList<ToolDefinition> Definitions => _definitions;

    /// <inheritdoc/>
    public async Task<ToolInvokeResult> InvokeAsync(string toolName, string argumentsJson, CancellationToken ct)
    {
        try
        {
            using var doc = string.IsNullOrWhiteSpace(argumentsJson)
                ? JsonDocument.Parse("{}")
                : JsonDocument.Parse(argumentsJson);
            var args = doc.RootElement.ValueKind == JsonValueKind.Object
                ? doc.RootElement
                : throw new ArgumentException("arguments must be a JSON object");

            var sessionId = _currentSessionId();
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return ToolInvokeResult.Fail($"Tool '{toolName}' requires an active session, but no session is bound");
            }

            return toolName switch
            {
                "todo_add" => await AddAsync(args, sessionId, ct).ConfigureAwait(false),
                "todo_list" => await ListAsync(sessionId, ct).ConfigureAwait(false),
                "todo_update" => await UpdateAsync(args, ct).ConfigureAwait(false),
                "todo_delete" => await DeleteAsync(args, ct).ConfigureAwait(false),
                _ => ToolInvokeResult.Fail($"Unknown todo tool '{toolName}'"),
            };
        }
        catch (JsonException ex)
        {
            return ToolInvokeResult.Fail($"Invalid arguments JSON: {ex.Message}");
        }
        catch (ArgumentException ex)
        {
            return ToolInvokeResult.Fail(ex.Message);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ToolInvokeResult.Fail($"todo tool '{toolName}' failed: {ex.Message}");
        }
    }

    private async Task<ToolInvokeResult> AddAsync(JsonElement args, string sessionId, CancellationToken ct)
    {
        var content = RequireString(args, "content");
        if (content is null)
        {
            return ToolInvokeResult.Fail("todo_add requires a non-empty 'content' string argument");
        }

        var added = await _store.AddAsync(sessionId, content, ct: ct).ConfigureAwait(false);
        if (!added.IsSuccess)
        {
            return ToolInvokeResult.Fail(added.Error ?? "todo_add failed");
        }

        var item = added.Value!;
        return ToolInvokeResult.Ok(
            $"Added todo [{item.Id}] (position {item.Position}): {item.Content}");
    }

    private async Task<ToolInvokeResult> ListAsync(string sessionId, CancellationToken ct)
    {
        var listed = await _store.ListAsync(sessionId, ct).ConfigureAwait(false);
        if (!listed.IsSuccess)
        {
            return ToolInvokeResult.Fail(listed.Error ?? "todo_list failed");
        }

        var items = listed.Value!;
        if (items.Count == 0)
        {
            return ToolInvokeResult.Ok("Task list is empty.");
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Task list ({items.Count}):");
        foreach (var item in items)
        {
            sb.AppendLine($"- [{item.Id}] {(item.IsCompleted ? "[x]" : "[ ]")} {item.Content}");
        }

        return ToolInvokeResult.Ok(sb.ToString().TrimEnd());
    }

    private async Task<ToolInvokeResult> UpdateAsync(JsonElement args, CancellationToken ct)
    {
        var id = RequireString(args, "id");
        if (id is null)
        {
            return ToolInvokeResult.Fail("todo_update requires a non-empty 'id' string argument");
        }

        string? content = null;
        if (args.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
        {
            content = c.GetString();
        }

        bool? completed = null;
        if (args.TryGetProperty("completed", out var done) &&
            done.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            completed = done.GetBoolean();
        }

        if (content is null && completed is null)
        {
            return ToolInvokeResult.Fail(
                "todo_update requires 'completed' (bool) and/or 'content' (string) to change");
        }

        var updated = await _store.UpdateAsync(id, content, completed, ct).ConfigureAwait(false);
        if (!updated.IsSuccess)
        {
            return ToolInvokeResult.Fail(updated.Error ?? "todo_update failed");
        }

        var item = updated.Value!;
        return ToolInvokeResult.Ok(
            $"Updated todo [{item.Id}]: {(item.IsCompleted ? "[x]" : "[ ]")} {item.Content}");
    }

    private async Task<ToolInvokeResult> DeleteAsync(JsonElement args, CancellationToken ct)
    {
        var id = RequireString(args, "id");
        if (id is null)
        {
            return ToolInvokeResult.Fail("todo_delete requires a non-empty 'id' string argument");
        }

        var deleted = await _store.DeleteAsync(id, ct).ConfigureAwait(false);
        return deleted.IsSuccess
            ? ToolInvokeResult.Ok($"Deleted todo [{id}].")
            : ToolInvokeResult.Fail(deleted.Error ?? "todo_delete failed");
    }

    private static string? RequireString(JsonElement args, string name)
    {
        if (args.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String)
        {
            var value = el.GetString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }
}
