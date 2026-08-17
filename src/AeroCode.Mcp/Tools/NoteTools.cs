using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AeroCode.Core.Services;
using ModelContextProtocol.Server;

namespace AeroCode.Mcp.Tools;

/// <summary>
/// MCP Tools: 暴露 AeroCode.Core 业务给外部 AI (DeepSeek Harness / Claude Code / Cursor / pi-agent / Cline 等)
/// 每个 Tool 都有详细 Description,符合 MCP 最佳实践。
/// </summary>
[McpServerToolType]
public sealed class NoteTools
{
    private readonly INoteService _notes;
    private readonly INotebookService _notebooks;
    private readonly ITagService _tags;
    private readonly ISearchService _search;

    public NoteTools(INoteService notes, INotebookService notebooks, ITagService tags, ISearchService search)
    {
        _notes = notes; _notebooks = notebooks; _tags = tags; _search = search;
    }

    [McpServerTool(Name = "list_notes"), Description("列出所有未删除的笔记,可按 notebook 过滤,limit 默认 50,最大 500")]
    public async Task<string> ListNotes(
        [Description("按笔记本 ID 过滤,可选")] long? notebook_id = null,
        [Description("最大返回数量,默认 50,最大 500")] int limit = 50)
    {
        var r = notebook_id.HasValue
            ? await _notes.GetByNotebookAsync(notebook_id.Value, recursive: true)
            : await _notes.GetAllAsync();
        if (!r.IsSuccess) return $"Error: {r.Error}";
        var list = r.Value!.Take(System.Math.Clamp(limit, 1, 500))
            .Select(n => new { n.Id, n.Title, n.IsPinned, n.UpdatedAt, n.WordCount });
        return JsonSerializer.Serialize(new { count = list.Count(), notes = list });
    }

    [McpServerTool(Name = "get_note"), Description("按 ID 获取单条笔记的完整内容,含 Markdown 正文和标签")]
    public async Task<string> GetNote([Description("笔记 ID")] long id)
    {
        var r = await _notes.GetByIdAsync(id);
        if (!r.IsSuccess) return $"Error: {r.Error}";
        var n = r.Value!;
        return JsonSerializer.Serialize(new
        {
            n.Id, n.Title, n.Content, n.IsPinned, n.UpdatedAt, n.CreatedAt, n.WordCount,
            notebook = n.Notebook is null ? null : new { n.Notebook.Id, n.Notebook.Name },
            tags = n.NoteTags.Select(nt => nt.Tag?.Name).Where(t => t is not null).ToArray()
        });
    }

    [McpServerTool(Name = "create_note"), Description("创建一条新笔记,返回新 ID。title 必填,content 可空,notebook_id 可空")]
    public async Task<string> CreateNote(
        [Description("笔记标题,必填,非空")] string title,
        [Description("Markdown 内容,默认空字符串")] string content = "",
        [Description("所属笔记本 ID,可选,不填或 0 表示无笔记本")] long? notebook_id = null)
    {
        var r = await _notes.CreateAsync(title, content ?? string.Empty, notebook_id);
        return r.IsSuccess
            ? JsonSerializer.Serialize(new { ok = true, id = r.Value!.Id })
            : $"Error: {r.Error}";
    }

    [McpServerTool(Name = "update_note"), Description("更新笔记字段,只更新传入的非 null 字段")]
    public async Task<string> UpdateNote(
        [Description("笔记 ID")] long id,
        [Description("新标题,可选")] string? title = null,
        [Description("新内容,可选")] string? content = null,
        [Description("新笔记本 ID,可选,0 表示无笔记本")] long? notebook_id = null,
        [Description("是否置顶,可选")] bool? is_pinned = null)
    {
        var r = await _notes.UpdateAsync(id, title, content, notebook_id, is_pinned);
        return r.IsSuccess ? "OK" : $"Error: {r.Error}";
    }

    [McpServerTool(Name = "delete_note"), Description("软删除笔记(可恢复)。hard=true 永久删除,默认 false")]
    public async Task<string> DeleteNote(
        [Description("笔记 ID")] long id,
        [Description("是否硬删除(不可恢复),默认 false")] bool hard = false)
    {
        var r = hard
            ? await _notes.HardDeleteAsync(id)
            : await _notes.SoftDeleteAsync(id);
        return r.IsSuccess ? "OK" : $"Error: {r.Error}";
    }

    [McpServerTool(Name = "search_notes"), Description("全文搜索,匹配 title 或 content,limit 默认 50")]
    public async Task<string> SearchNotes(
        [Description("搜索关键词,必填,至少 1 个非空字符")] string query,
        [Description("最大返回数量,默认 50,最大 500")] int limit = 50)
    {
        var r = await _search.SearchAsync(query, limit);
        if (!r.IsSuccess) return $"Error: {r.Error}";
        var list = r.Value!.Select(n => new { n.Id, n.Title, n.UpdatedAt, snippet = Snippet(n.Content, 100) });
        return JsonSerializer.Serialize(new { count = list.Count(), notes = list });
    }

    [McpServerTool(Name = "list_notebooks"), Description("列出根笔记本(顶级笔记本)")]
    public async Task<string> ListNotebooks()
    {
        var r = await _notebooks.GetRootsAsync();
        if (!r.IsSuccess) return $"Error: {r.Error}";
        return JsonSerializer.Serialize(new { notebooks = r.Value!.Select(nb => new { nb.Id, nb.Name }) });
    }

    [McpServerTool(Name = "create_notebook"), Description("创建新笔记本,parent_id 可选(0 或 null 表示顶级)")]
    public async Task<string> CreateNotebook(
        [Description("笔记本名,必填")] string name,
        [Description("描述,可选")] string? description = null,
        [Description("父笔记本 ID,可选,0 表示顶级")] long? parent_id = null)
    {
        var r = await _notebooks.CreateAsync(name, description, parent_id);
        return r.IsSuccess
            ? JsonSerializer.Serialize(new { ok = true, id = r.Value!.Id })
            : $"Error: {r.Error}";
    }

    [McpServerTool(Name = "list_tags"), Description("列出所有标签")]
    public async Task<string> ListTags()
    {
        var r = await _tags.GetAllAsync();
        if (!r.IsSuccess) return $"Error: {r.Error}";
        return JsonSerializer.Serialize(new { tags = r.Value!.Select(t => new { t.Id, t.Name }) });
    }

    [McpServerTool(Name = "set_note_tags"), Description("设置笔记的标签(覆盖式,传空数组清空)")]
    public async Task<string> SetNoteTags(
        [Description("笔记 ID")] long note_id,
        [Description("标签名数组,如 [\"工作\",\"重要\"]")] string[] tag_names)
    {
        var r = await _notes.SetTagsAsync(note_id, tag_names ?? System.Array.Empty<string>());
        return r.IsSuccess ? "OK" : $"Error: {r.Error}";
    }

    [McpServerTool(Name = "get_notes_by_tag"), Description("按标签名获取该标签下的所有笔记")]
    public async Task<string> GetNotesByTag([Description("标签名")] string tag_name)
    {
        var allTags = await _tags.GetAllAsync();
        if (!allTags.IsSuccess) return $"Error: {allTags.Error}";
        var tag = allTags.Value!.FirstOrDefault(t => string.Equals(t.Name, tag_name, System.StringComparison.OrdinalIgnoreCase));
        if (tag is null) return JsonSerializer.Serialize(new { count = 0, notes = System.Array.Empty<object>() });
        var r = await _tags.GetNotesByTagAsync(tag.Id);
        if (!r.IsSuccess) return $"Error: {r.Error}";
        return JsonSerializer.Serialize(new { count = r.Value!.Count, notes = r.Value!.Select(n => new { n.Id, n.Title, n.UpdatedAt }) });
    }

    [McpServerTool(Name = "toggle_pin"), Description("切换笔记置顶状态,返回新状态")]
    public async Task<string> TogglePin([Description("笔记 ID")] long id)
    {
        var r = await _notes.TogglePinAsync(id);
        return r.IsSuccess ? JsonSerializer.Serialize(new { ok = true, is_pinned = r.Value }) : $"Error: {r.Error}";
    }

    private static string Snippet(string text, int maxLen)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return text.Length <= maxLen ? text : text[..maxLen] + "...";
    }
}
