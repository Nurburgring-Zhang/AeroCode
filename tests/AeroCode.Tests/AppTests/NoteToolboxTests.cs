using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AeroCode.App.Tools;
using AeroCode.Core.Data;
using AeroCode.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AeroCode.Tests.AppTests;

/// <summary>
/// NoteToolbox：12 个内建笔记工具在真实文件 SQLite 库上的完整行为验证。
/// 与 MCP NoteTools 同名同形——这里断言的每一个输出形态都与 MCP 链路一致。
/// </summary>
public sealed partial class NoteToolboxTests : IDisposable
{
    [GeneratedRegex("^[a-zA-Z0-9_-]{1,64}$")]
    private static partial Regex ToolNamePattern();

    private readonly string _tempDir;
    private readonly string _dbPath;
    private readonly SqliteConnection _keepAlive;
    private readonly AeroCodeDbContext _db;
    private readonly NoteToolbox _toolbox;

    public NoteToolboxTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"note_toolbox_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "notes.db");
        var connStr = new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString();
        _keepAlive = new SqliteConnection(connStr);
        _keepAlive.Open();

        var options = new DbContextOptionsBuilder<AeroCodeDbContext>()
            .UseSqlite(connStr)
            .Options;
        _db = new AeroCodeDbContext(options);
        _db.Database.EnsureCreated();

        var tags = new TagService(_db);
        var notebooks = new NotebookService(_db);
        var notes = new NoteService(_db, tags);
        var search = new SearchService(_db);
        _toolbox = new NoteToolbox(notes, notebooks, tags, search);
    }

    public void Dispose()
    {
        _db.Dispose();
        _keepAlive.Dispose();
        SqliteConnection.ClearPool(_keepAlive);
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // 临时目录清理失败不影响测试结果
        }
    }

    private async Task<string> InvokeOkAsync(string toolName, string argsJson)
    {
        var result = await _toolbox.InvokeAsync(toolName, argsJson, CancellationToken.None);
        Assert.True(result.Success, $"工具 {toolName} 应成功，实际失败：{result.Error}");
        Assert.False(result.Denied);
        return result.Output;
    }

    private static long ParseCreatedId(string output)
    {
        using var doc = JsonDocument.Parse(output);
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        return doc.RootElement.GetProperty("id").GetInt64();
    }

    [Fact]
    public void Definitions_Expose12Tools_WithValidNamesAndSchemas()
    {
        Assert.Equal(12, _toolbox.Definitions.Count);
        Assert.Equal(NoteToolbox.ToolNames, _toolbox.Definitions.Select(d => d.Name).ToList());
        foreach (var def in _toolbox.Definitions)
        {
            Assert.True(ToolNamePattern().IsMatch(def.Name), $"工具名非法：{def.Name}");
            Assert.False(string.IsNullOrWhiteSpace(def.Description));
            // Schema 必须是合法 JSON 对象（provider 序列化时直接透传）
            using var schema = JsonDocument.Parse(def.ParametersJsonSchema);
            Assert.Equal(JsonValueKind.Object, schema.RootElement.ValueKind);
        }
    }

    [Fact]
    public async Task CreateThenGetNote_RoundTripsThroughRealDb()
    {
        var created = await InvokeOkAsync("create_note",
            """{"title":"E2E 笔记","content":"# 小节\n真实内容"}""");
        var id = ParseCreatedId(created);
        Assert.True(id > 0);

        // 真实落库（直查 DB，不经过工具自身）
        var row = await _db.Notes.AsNoTracking().SingleAsync(n => n.Id == id);
        Assert.Equal("E2E 笔记", row.Title);
        Assert.Equal("# 小节\n真实内容", row.Content);

        var fetched = await InvokeOkAsync("get_note", $"{{\"id\":{id}}}");
        using var doc = JsonDocument.Parse(fetched);
        Assert.Equal("E2E 笔记", doc.RootElement.GetProperty("Title").GetString());
        Assert.Equal("# 小节\n真实内容", doc.RootElement.GetProperty("Content").GetString());
        Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("tags").ValueKind);
    }

    [Fact]
    public async Task GetNote_Missing_FailsHonestly()
    {
        var result = await _toolbox.InvokeAsync("get_note", "{\"id\":99999}", CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("未找到笔记 #99999", result.Output);
    }

    [Fact]
    public async Task ListNotes_FiltersByNotebook_AndClampsLimit()
    {
        var nb = ParseCreatedId(await InvokeOkAsync("create_notebook", """{"name":"工作"}"""));
        await InvokeOkAsync("create_note", """{"title":"A"}""");
        await InvokeOkAsync("create_note", """{"title":"B"}""");
        await InvokeOkAsync("create_note", $"{{\"title\":\"C\",\"notebook_id\":{nb}}}");

        var filtered = await InvokeOkAsync("list_notes", $"{{\"notebook_id\":{nb}}}");
        using var f = JsonDocument.Parse(filtered);
        Assert.Equal(1, f.RootElement.GetProperty("count").GetInt32());
        Assert.Equal("C", f.RootElement.GetProperty("notes")[0].GetProperty("Title").GetString());

        var limited = await InvokeOkAsync("list_notes", """{"limit":2}""");
        using var l = JsonDocument.Parse(limited);
        Assert.Equal(2, l.RootElement.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task UpdateNote_PartialFields_KeepOthers()
    {
        var id = ParseCreatedId(await InvokeOkAsync("create_note",
            """{"title":"原标题","content":"原内容"}"""));

        Assert.Equal("OK", await InvokeOkAsync("update_note", $"{{\"id\":{id},\"title\":\"新标题\"}}"));

        var fetched = await InvokeOkAsync("get_note", $"{{\"id\":{id}}}");
        using var doc = JsonDocument.Parse(fetched);
        Assert.Equal("新标题", doc.RootElement.GetProperty("Title").GetString());
        Assert.Equal("原内容", doc.RootElement.GetProperty("Content").GetString()); // 未传字段不动
    }

    [Fact]
    public async Task DeleteNote_SoftHides_HardRemoves()
    {
        var softId = ParseCreatedId(await InvokeOkAsync("create_note", """{"title":"软删"}"""));
        var hardId = ParseCreatedId(await InvokeOkAsync("create_note", """{"title":"硬删"}"""));

        Assert.Equal("OK", await InvokeOkAsync("delete_note", $"{{\"id\":{softId}}}"));
        Assert.Equal("OK", await InvokeOkAsync("delete_note", $"{{\"id\":{hardId},\"hard\":true}}"));

        // 软删：列表不可见，行仍在（可恢复）
        var listed = await InvokeOkAsync("list_notes", "{}");
        using var doc = JsonDocument.Parse(listed);
        Assert.DoesNotContain(doc.RootElement.GetProperty("notes").EnumerateArray(),
            n => n.GetProperty("Id").GetInt64() == softId);
        Assert.True(await _db.Notes.AsNoTracking().AnyAsync(n => n.Id == softId && n.IsDeleted));

        // 硬删：行彻底消失
        Assert.False(await _db.Notes.AsNoTracking().AnyAsync(n => n.Id == hardId));
    }

    [Fact]
    public async Task SearchNotes_FindsByContent()
    {
        var id = ParseCreatedId(await InvokeOkAsync("create_note",
            """{"title":"普通标题","content":"这里有独特词 quantum-zebra"}"""));

        var output = await InvokeOkAsync("search_notes", """{"query":"quantum-zebra"}""");
        using var doc = JsonDocument.Parse(output);
        Assert.True(doc.RootElement.GetProperty("count").GetInt32() >= 1);
        Assert.Contains(doc.RootElement.GetProperty("notes").EnumerateArray(),
            n => n.GetProperty("Id").GetInt64() == id);
    }

    [Fact]
    public async Task Notebooks_CreateWithParent_ListRoots()
    {
        var root = ParseCreatedId(await InvokeOkAsync("create_notebook", """{"name":"根本"}"""));
        await InvokeOkAsync("create_notebook", $"{{\"name\":\"子本\",\"parent_id\":{root}}}");

        var listed = await InvokeOkAsync("list_notebooks", "{}");
        using var doc = JsonDocument.Parse(listed);
        var names = doc.RootElement.GetProperty("notebooks").EnumerateArray()
            .Select(nb => nb.GetProperty("Name").GetString()).ToList();
        Assert.Contains("根本", names);
        Assert.DoesNotContain("子本", names); // list_notebooks 只列根
    }

    [Fact]
    public async Task Tags_SetListAndQueryByTag_FullChain()
    {
        var id = ParseCreatedId(await InvokeOkAsync("create_note", """{"title":"带标签"}"""));
        Assert.Equal("OK", await InvokeOkAsync("set_note_tags",
            $"{{\"note_id\":{id},\"tag_names\":[\"工作\",\"重要\"]}}"));

        var tagsJson = await InvokeOkAsync("list_tags", "{}");
        using var tagsDoc = JsonDocument.Parse(tagsJson);
        Assert.Equal(2, tagsDoc.RootElement.GetProperty("tags").GetArrayLength());

        var byTag = await InvokeOkAsync("get_notes_by_tag", """{"tag_name":"工作"}""");
        using var byTagDoc = JsonDocument.Parse(byTag);
        Assert.Equal(1, byTagDoc.RootElement.GetProperty("count").GetInt32());
        Assert.Equal(id, byTagDoc.RootElement.GetProperty("notes")[0].GetProperty("Id").GetInt64());

        // 不存在的标签：诚实返回空集，不报错
        var none = await InvokeOkAsync("get_notes_by_tag", """{"tag_name":"幽灵标签"}""");
        using var noneDoc = JsonDocument.Parse(none);
        Assert.Equal(0, noneDoc.RootElement.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task TogglePin_FlipsState()
    {
        var id = ParseCreatedId(await InvokeOkAsync("create_note", """{"title":"置顶实验"}"""));

        var first = await InvokeOkAsync("toggle_pin", $"{{\"id\":{id}}}");
        using var f = JsonDocument.Parse(first);
        Assert.True(f.RootElement.GetProperty("is_pinned").GetBoolean());

        var second = await InvokeOkAsync("toggle_pin", $"{{\"id\":{id}}}");
        using var s = JsonDocument.Parse(second);
        Assert.False(s.RootElement.GetProperty("is_pinned").GetBoolean());
    }

    [Fact]
    public async Task UnknownTool_HonestFailure()
    {
        var result = await _toolbox.InvokeAsync("ghost_tool", "{}", CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("ghost_tool", result.Output);
    }

    [Fact]
    public async Task InvalidJson_FailsWithoutTouchingServices()
    {
        var result = await _toolbox.InvokeAsync("create_note", "{not json", CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("JSON 非法", result.Output);
    }

    [Fact]
    public async Task NonObjectRoot_Fails()
    {
        var result = await _toolbox.InvokeAsync("list_notes", "[1,2]", CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("JSON 对象", result.Output);
    }

    [Fact]
    public async Task MissingRequiredParam_ModelSeesActionableError()
    {
        var result = await _toolbox.InvokeAsync("create_note", "{}", CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("缺少必填参数 'title'", result.Output);
    }

    [Fact]
    public async Task WrongParamType_ModelSeesActionableError()
    {
        var result = await _toolbox.InvokeAsync("get_note", """{"id":"abc"}""", CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("必须是整数", result.Output);
    }

    [Fact]
    public async Task SetNoteTags_NonArrayItems_Fails()
    {
        var id = ParseCreatedId(await InvokeOkAsync("create_note", """{"title":"t"}"""));
        var result = await _toolbox.InvokeAsync(
            "set_note_tags", $"{{\"note_id\":{id},\"tag_names\":[\"ok\",5]}}", CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("必须是字符串", result.Output);
    }

    [Fact]
    public void Constructor_NullServices_Throw()
    {
        var tags = new TagService(_db);
        var notebooks = new NotebookService(_db);
        var notes = new NoteService(_db, tags);
        var search = new SearchService(_db);

        Assert.Throws<ArgumentNullException>(() => new NoteToolbox(null!, notebooks, tags, search));
        Assert.Throws<ArgumentNullException>(() => new NoteToolbox(notes, null!, tags, search));
        Assert.Throws<ArgumentNullException>(() => new NoteToolbox(notes, notebooks, null!, search));
        Assert.Throws<ArgumentNullException>(() => new NoteToolbox(notes, notebooks, tags, null!));
    }
}
