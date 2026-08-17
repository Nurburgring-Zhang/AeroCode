using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AeroAgent.Moa.Tools;
using AeroCode.AI.Models;
using AeroCode.Core.Services;

namespace AeroCode.App.Tools;

/// <summary>
/// 内建笔记工具域：12 个工具直连 AeroCode.Core 服务（真实 SQLite 库），
/// 与 AeroCode.Mcp 的 NoteTools 同名同形（参数与输出 JSON 一致）——
/// 模型在“进程内直连”与“MCP 子进程”两条链路上看到完全一致的工具契约。
/// 域内失败（参数非法/服务报错）不抛异常，以 <see cref="ToolInvokeResult.Fail"/> 如实交还模型。
/// </summary>
public sealed class NoteToolbox : IWorkerToolbox
{
    private readonly INoteService _notes;
    private readonly INotebookService _notebooks;
    private readonly ITagService _tags;
    private readonly ISearchService _search;

    /// <summary>与 MCP NoteTools 对齐的 12 个工具名（权限默认裁决/测试断言共用）。</summary>
    public static readonly IReadOnlyList<string> ToolNames = new[]
    {
        "list_notes", "get_note", "create_note", "update_note", "delete_note", "search_notes",
        "list_notebooks", "create_notebook", "list_tags", "set_note_tags", "get_notes_by_tag", "toggle_pin",
    };

    public NoteToolbox(
        INoteService notes,
        INotebookService notebooks,
        ITagService tags,
        ISearchService search)
    {
        _notes = notes ?? throw new ArgumentNullException(nameof(notes));
        _notebooks = notebooks ?? throw new ArgumentNullException(nameof(notebooks));
        _tags = tags ?? throw new ArgumentNullException(nameof(tags));
        _search = search ?? throw new ArgumentNullException(nameof(search));
    }

    public string Domain => "notes";

    public IReadOnlyList<ToolDefinition> Definitions { get; } = new List<ToolDefinition>
    {
        new()
        {
            Name = "list_notes",
            Description = "列出所有未删除的笔记，可按 notebook 过滤，limit 默认 50，最大 500",
            ParametersJsonSchema = """
                {"type":"object","properties":{"notebook_id":{"type":"integer","description":"按笔记本 ID 过滤，可选"},"limit":{"type":"integer","description":"最大返回数量，默认 50，最大 500"}}}
                """,
        },
        new()
        {
            Name = "get_note",
            Description = "按 ID 获取单条笔记的完整内容，含 Markdown 正文和标签",
            ParametersJsonSchema = """
                {"type":"object","properties":{"id":{"type":"integer","description":"笔记 ID"}},"required":["id"]}
                """,
        },
        new()
        {
            Name = "create_note",
            Description = "创建一条新笔记，返回新 ID。title 必填，content 可空，notebook_id 可空",
            ParametersJsonSchema = """
                {"type":"object","properties":{"title":{"type":"string","description":"笔记标题，必填，非空"},"content":{"type":"string","description":"Markdown 内容，默认空字符串"},"notebook_id":{"type":"integer","description":"所属笔记本 ID，可选"}},"required":["title"]}
                """,
        },
        new()
        {
            Name = "update_note",
            Description = "更新笔记字段，只更新传入的非 null 字段",
            ParametersJsonSchema = """
                {"type":"object","properties":{"id":{"type":"integer","description":"笔记 ID"},"title":{"type":"string","description":"新标题，可选"},"content":{"type":"string","description":"新内容，可选"},"notebook_id":{"type":"integer","description":"新笔记本 ID，可选，0 表示无笔记本"},"is_pinned":{"type":"boolean","description":"是否置顶，可选"}},"required":["id"]}
                """,
        },
        new()
        {
            Name = "delete_note",
            Description = "软删除笔记（可恢复）。hard=true 永久删除，默认 false",
            ParametersJsonSchema = """
                {"type":"object","properties":{"id":{"type":"integer","description":"笔记 ID"},"hard":{"type":"boolean","description":"是否硬删除（不可恢复），默认 false"}},"required":["id"]}
                """,
        },
        new()
        {
            Name = "search_notes",
            Description = "全文搜索，匹配 title 或 content，limit 默认 50",
            ParametersJsonSchema = """
                {"type":"object","properties":{"query":{"type":"string","description":"搜索关键词，必填，至少 1 个非空字符"},"limit":{"type":"integer","description":"最大返回数量，默认 50，最大 500"}},"required":["query"]}
                """,
        },
        new()
        {
            Name = "list_notebooks",
            Description = "列出根笔记本（顶级笔记本）",
            ParametersJsonSchema = """
                {"type":"object","properties":{}}
                """,
        },
        new()
        {
            Name = "create_notebook",
            Description = "创建新笔记本，parent_id 可选（0 或 null 表示顶级）",
            ParametersJsonSchema = """
                {"type":"object","properties":{"name":{"type":"string","description":"笔记本名，必填"},"description":{"type":"string","description":"描述，可选"},"parent_id":{"type":"integer","description":"父笔记本 ID，可选，0 表示顶级"}},"required":["name"]}
                """,
        },
        new()
        {
            Name = "list_tags",
            Description = "列出所有标签",
            ParametersJsonSchema = """
                {"type":"object","properties":{}}
                """,
        },
        new()
        {
            Name = "set_note_tags",
            Description = "设置笔记的标签（覆盖式，传空数组清空）",
            ParametersJsonSchema = """
                {"type":"object","properties":{"note_id":{"type":"integer","description":"笔记 ID"},"tag_names":{"type":"array","items":{"type":"string"},"description":"标签名数组，如 [\"工作\",\"重要\"]"}},"required":["note_id","tag_names"]}
                """,
        },
        new()
        {
            Name = "get_notes_by_tag",
            Description = "按标签名获取该标签下的所有笔记",
            ParametersJsonSchema = """
                {"type":"object","properties":{"tag_name":{"type":"string","description":"标签名"}},"required":["tag_name"]}
                """,
        },
        new()
        {
            Name = "toggle_pin",
            Description = "切换笔记置顶状态，返回新状态",
            ParametersJsonSchema = """
                {"type":"object","properties":{"id":{"type":"integer","description":"笔记 ID"}},"required":["id"]}
                """,
        },
    };

    public async Task<ToolInvokeResult> InvokeAsync(string toolName, string argumentsJson, CancellationToken ct)
    {
        JsonElement args;
        try
        {
            args = ParseObject(argumentsJson);
        }
        catch (JsonException ex)
        {
            return ToolInvokeResult.Fail($"工具参数 JSON 非法：{ex.Message}");
        }
        catch (ToolArgumentException ex)
        {
            // 根节点非对象等结构性错误同样诚实降级，不得逸出工具循环。
            return ToolInvokeResult.Fail(ex.Message);
        }

        try
        {
            return toolName switch
            {
                "list_notes" => await ListNotesAsync(args, ct),
                "get_note" => await GetNoteAsync(args, ct),
                "create_note" => await CreateNoteAsync(args, ct),
                "update_note" => await UpdateNoteAsync(args, ct),
                "delete_note" => await DeleteNoteAsync(args, ct),
                "search_notes" => await SearchNotesAsync(args, ct),
                "list_notebooks" => await ListNotebooksAsync(ct),
                "create_notebook" => await CreateNotebookAsync(args, ct),
                "list_tags" => await ListTagsAsync(ct),
                "set_note_tags" => await SetNoteTagsAsync(args, ct),
                "get_notes_by_tag" => await GetNotesByTagAsync(args, ct),
                "toggle_pin" => await TogglePinAsync(args, ct),
                _ => ToolInvokeResult.Fail($"笔记工具 '{toolName}' 不存在"),
            };
        }
        catch (OperationCanceledException)
        {
            // 取消必须透传：工具循环上层统一落库 Cancelled 状态。
            throw;
        }
        catch (ToolArgumentException ex)
        {
            return ToolInvokeResult.Fail(ex.Message);
        }
        catch (Exception ex)
        {
            return ToolInvokeResult.Fail($"笔记工具执行失败：{ex.Message}");
        }
    }

    // ---- 各工具实现：输出 JSON 形态与 AeroCode.Mcp/NoteTools 完全一致 ----

    private async Task<ToolInvokeResult> ListNotesAsync(JsonElement args, CancellationToken ct)
    {
        var notebookId = GetLong(args, "notebook_id", required: false);
        var limit = GetInt(args, "limit", required: false) ?? 50;
        var r = notebookId.HasValue
            ? await _notes.GetByNotebookAsync(notebookId.Value, recursive: true, ct)
            : await _notes.GetAllAsync(ct: ct);
        if (!r.IsSuccess)
        {
            return ToolInvokeResult.Fail(r.Error ?? "列出笔记失败");
        }

        var list = r.Value!.Take(Math.Clamp(limit, 1, 500))
            .Select(n => new { n.Id, n.Title, n.IsPinned, n.UpdatedAt, n.WordCount });
        return ToolInvokeResult.Ok(JsonSerializer.Serialize(new { count = list.Count(), notes = list }));
    }

    private async Task<ToolInvokeResult> GetNoteAsync(JsonElement args, CancellationToken ct)
    {
        var id = GetLong(args, "id", required: true)!.Value;
        var r = await _notes.GetByIdAsync(id, ct);
        if (!r.IsSuccess)
        {
            return ToolInvokeResult.Fail(r.Error ?? $"笔记 #{id} 不存在");
        }

        var n = r.Value!;
        return ToolInvokeResult.Ok(JsonSerializer.Serialize(new
        {
            n.Id,
            n.Title,
            n.Content,
            n.IsPinned,
            n.UpdatedAt,
            n.CreatedAt,
            n.WordCount,
            notebook = n.Notebook is null ? null : new { n.Notebook.Id, n.Notebook.Name },
            tags = n.NoteTags.Select(nt => nt.Tag?.Name).Where(t => t is not null).ToArray(),
        }));
    }

    private async Task<ToolInvokeResult> CreateNoteAsync(JsonElement args, CancellationToken ct)
    {
        var title = GetString(args, "title", required: true)!;
        var content = GetString(args, "content", required: false) ?? string.Empty;
        var notebookId = GetLong(args, "notebook_id", required: false);
        var r = await _notes.CreateAsync(title, content, notebookId, ct);
        return r.IsSuccess
            ? ToolInvokeResult.Ok(JsonSerializer.Serialize(new { ok = true, id = r.Value!.Id }))
            : ToolInvokeResult.Fail(r.Error ?? "创建笔记失败");
    }

    private async Task<ToolInvokeResult> UpdateNoteAsync(JsonElement args, CancellationToken ct)
    {
        var id = GetLong(args, "id", required: true)!.Value;
        var title = GetString(args, "title", required: false);
        var content = GetString(args, "content", required: false);
        var notebookId = GetLong(args, "notebook_id", required: false);
        var isPinned = GetBool(args, "is_pinned", required: false);
        var r = await _notes.UpdateAsync(id, title, content, notebookId, isPinned, ct);
        return r.IsSuccess ? ToolInvokeResult.Ok("OK") : ToolInvokeResult.Fail(r.Error ?? "更新笔记失败");
    }

    private async Task<ToolInvokeResult> DeleteNoteAsync(JsonElement args, CancellationToken ct)
    {
        var id = GetLong(args, "id", required: true)!.Value;
        var hard = GetBool(args, "hard", required: false) ?? false;
        var r = hard
            ? await _notes.HardDeleteAsync(id, ct)
            : await _notes.SoftDeleteAsync(id, ct);
        return r.IsSuccess ? ToolInvokeResult.Ok("OK") : ToolInvokeResult.Fail(r.Error ?? "删除笔记失败");
    }

    private async Task<ToolInvokeResult> SearchNotesAsync(JsonElement args, CancellationToken ct)
    {
        var query = GetString(args, "query", required: true)!;
        var limit = GetInt(args, "limit", required: false) ?? 50;
        var r = await _search.SearchAsync(query, limit, ct);
        if (!r.IsSuccess)
        {
            return ToolInvokeResult.Fail(r.Error ?? "搜索失败");
        }

        var list = r.Value!.Select(n => new { n.Id, n.Title, n.UpdatedAt, snippet = Snippet(n.Content, 100) });
        return ToolInvokeResult.Ok(JsonSerializer.Serialize(new { count = list.Count(), notes = list }));
    }

    private async Task<ToolInvokeResult> ListNotebooksAsync(CancellationToken ct)
    {
        var r = await _notebooks.GetRootsAsync(ct);
        if (!r.IsSuccess)
        {
            return ToolInvokeResult.Fail(r.Error ?? "列出笔记本失败");
        }

        return ToolInvokeResult.Ok(JsonSerializer.Serialize(
            new { notebooks = r.Value!.Select(nb => new { nb.Id, nb.Name }) }));
    }

    private async Task<ToolInvokeResult> CreateNotebookAsync(JsonElement args, CancellationToken ct)
    {
        var name = GetString(args, "name", required: true)!;
        var description = GetString(args, "description", required: false);
        var parentId = GetLong(args, "parent_id", required: false);
        var r = await _notebooks.CreateAsync(name, description, parentId, ct);
        return r.IsSuccess
            ? ToolInvokeResult.Ok(JsonSerializer.Serialize(new { ok = true, id = r.Value!.Id }))
            : ToolInvokeResult.Fail(r.Error ?? "创建笔记本失败");
    }

    private async Task<ToolInvokeResult> ListTagsAsync(CancellationToken ct)
    {
        var r = await _tags.GetAllAsync(ct);
        if (!r.IsSuccess)
        {
            return ToolInvokeResult.Fail(r.Error ?? "列出标签失败");
        }

        return ToolInvokeResult.Ok(JsonSerializer.Serialize(
            new { tags = r.Value!.Select(t => new { t.Id, t.Name }) }));
    }

    private async Task<ToolInvokeResult> SetNoteTagsAsync(JsonElement args, CancellationToken ct)
    {
        var noteId = GetLong(args, "note_id", required: true)!.Value;
        var tagNames = GetStringArray(args, "tag_names", required: true)!;
        var r = await _notes.SetTagsAsync(noteId, tagNames, ct);
        return r.IsSuccess ? ToolInvokeResult.Ok("OK") : ToolInvokeResult.Fail(r.Error ?? "设置标签失败");
    }

    private async Task<ToolInvokeResult> GetNotesByTagAsync(JsonElement args, CancellationToken ct)
    {
        var tagName = GetString(args, "tag_name", required: true)!;
        var allTags = await _tags.GetAllAsync(ct);
        if (!allTags.IsSuccess)
        {
            return ToolInvokeResult.Fail(allTags.Error ?? "列出标签失败");
        }

        var tag = allTags.Value!.FirstOrDefault(
            t => string.Equals(t.Name, tagName, StringComparison.OrdinalIgnoreCase));
        if (tag is null)
        {
            return ToolInvokeResult.Ok(JsonSerializer.Serialize(new { count = 0, notes = Array.Empty<object>() }));
        }

        var r = await _tags.GetNotesByTagAsync(tag.Id, ct);
        if (!r.IsSuccess)
        {
            return ToolInvokeResult.Fail(r.Error ?? "按标签取笔记失败");
        }

        return ToolInvokeResult.Ok(JsonSerializer.Serialize(new
        {
            count = r.Value!.Count,
            notes = r.Value!.Select(n => new { n.Id, n.Title, n.UpdatedAt }),
        }));
    }

    private async Task<ToolInvokeResult> TogglePinAsync(JsonElement args, CancellationToken ct)
    {
        var id = GetLong(args, "id", required: true)!.Value;
        var r = await _notes.TogglePinAsync(id, ct);
        return r.IsSuccess
            ? ToolInvokeResult.Ok(JsonSerializer.Serialize(new { ok = true, is_pinned = r.Value }))
            : ToolInvokeResult.Fail(r.Error ?? "切换置顶失败");
    }

    // ---- 参数解析：严格类型校验，错误信息直接交还模型 ----

    /// <summary>工具参数必须缺省（Undefined）或 JSON 对象；其它形态如实报错。</summary>
    private static JsonElement ParseObject(string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return default; // ValueKind.Undefined = 无参数
        }

        using var doc = JsonDocument.Parse(argumentsJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new ToolArgumentException("工具参数根节点必须是 JSON 对象");
        }

        return doc.RootElement.Clone();
    }

    private static bool HasValue(JsonElement root, string name, out JsonElement element)
    {
        element = default;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        return root.TryGetProperty(name, out element)
            && element.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);
    }

    private static string? GetString(JsonElement root, string name, bool required)
    {
        if (!HasValue(root, name, out var el))
        {
            if (required)
            {
                throw new ToolArgumentException($"缺少必填参数 '{name}'");
            }

            return null;
        }

        if (el.ValueKind != JsonValueKind.String)
        {
            throw new ToolArgumentException($"参数 '{name}' 必须是字符串");
        }

        return el.GetString();
    }

    private static long? GetLong(JsonElement root, string name, bool required)
    {
        if (!HasValue(root, name, out var el))
        {
            if (required)
            {
                throw new ToolArgumentException($"缺少必填参数 '{name}'");
            }

            return null;
        }

        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var value))
        {
            return value;
        }

        throw new ToolArgumentException($"参数 '{name}' 必须是整数");
    }

    private static int? GetInt(JsonElement root, string name, bool required)
    {
        var value = GetLong(root, name, required);
        if (value is null)
        {
            return null;
        }

        if (value < int.MinValue || value > int.MaxValue)
        {
            throw new ToolArgumentException($"参数 '{name}' 超出整数范围");
        }

        return (int)value;
    }

    private static bool? GetBool(JsonElement root, string name, bool required)
    {
        if (!HasValue(root, name, out var el))
        {
            if (required)
            {
                throw new ToolArgumentException($"缺少必填参数 '{name}'");
            }

            return null;
        }

        return el.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new ToolArgumentException($"参数 '{name}' 必须是布尔值"),
        };
    }

    private static string[]? GetStringArray(JsonElement root, string name, bool required)
    {
        if (!HasValue(root, name, out var el))
        {
            if (required)
            {
                throw new ToolArgumentException($"缺少必填参数 '{name}'");
            }

            return null;
        }

        if (el.ValueKind != JsonValueKind.Array)
        {
            throw new ToolArgumentException($"参数 '{name}' 必须是字符串数组");
        }

        var list = new List<string>();
        foreach (var item in el.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw new ToolArgumentException($"参数 '{name}' 的每一项必须是字符串");
            }

            list.Add(item.GetString()!);
        }

        return list.ToArray();
    }

    private static string Snippet(string text, int maxLen)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        return text.Length <= maxLen ? text : text[..maxLen] + "...";
    }

    /// <summary>参数语义错误（缺参/类型不符）：消息直接交还模型，便于其自我纠正。</summary>
    private sealed class ToolArgumentException : Exception
    {
        public ToolArgumentException(string message)
            : base(message)
        {
        }
    }
}
