using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AeroAgent.Conversation.Data;
using AeroAgent.Conversation.Services;
using AeroAgent.Moa.Tools;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AeroCode.Tests.MoaTests;

/// <summary>
/// 会话 Todo 全链路（真实临时 SQLite + 真实 EF Core + 真实工具域）：
/// TodoStore CRUD/会话隔离/持久化跨上下文 + TodoToolbox 参数校验与会话绑定。
/// </summary>
public sealed class TodoToolboxTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteConnection _keepAlive;
    private readonly DbContextOptions<ConversationDbContext> _options;

    public TodoToolboxTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"todo_test_{Guid.NewGuid():N}.db");
        var connStr = new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString();
        _keepAlive = new SqliteConnection(connStr);
        _keepAlive.Open();
        _options = new DbContextOptionsBuilder<ConversationDbContext>()
            .UseSqlite(connStr)
            .Options;
        using var db = new ConversationDbContext(_options);
        db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _keepAlive.Dispose();
        SqliteConnection.ClearPool(_keepAlive);
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    private TodoStore NewStore() => new(() => new ConversationDbContext(_options));

    private async Task<string> NewSessionAsync(string title)
    {
        await using var db = new ConversationDbContext(_options);
        var session = new AeroAgent.Conversation.Models.ChatSession { Title = title };
        db.Sessions.Add(session);
        await db.SaveChangesAsync();
        db.Entry(session).State = EntityState.Detached;
        return session.Id;
    }

    private (TodoToolbox Box, string SessionId) NewBox(TodoStore store, string sessionId)
        => (new TodoToolbox(store, () => sessionId), sessionId);

    private static string ExtractId(string output)
    {
        // 输出格式 "Added todo [<id>] ..." / "Updated todo [<id>] ..." / "Deleted todo [<id>]."
        var start = output.IndexOf('[', StringComparison.Ordinal) + 1;
        var end = output.IndexOf(']', start);
        Assert.True(start > 0 && end > start, $"no todo id in output: {output}");
        return output[start..end];
    }

    // ---------- TodoStore 真实 CRUD ----------

    [Fact]
    public async Task Add_List_Update_Delete_FullCrud_PersistsAcrossContexts()
    {
        var store = NewStore();
        var sessionId = await NewSessionAsync("crud");

        var added = await store.AddAsync(sessionId, "实现批处理");
        Assert.True(added.IsSuccess);
        var item = added.Value!;

        // 跨 DbContext（短生命周期工厂）读回 = 真实落库，不是本上下文内的跟踪对象。
        var listed = await store.ListAsync(sessionId);
        Assert.True(listed.IsSuccess);
        var loaded = Assert.Single(listed.Value!);
        Assert.Equal(item.Id, loaded.Id);
        Assert.Equal("实现批处理", loaded.Content);
        Assert.False(loaded.IsCompleted);

        var updated = await store.UpdateAsync(item.Id, isCompleted: true);
        Assert.True(updated.IsSuccess);
        Assert.True(updated.Value!.IsCompleted);

        var renamed = await store.UpdateAsync(item.Id, content: "实现批处理（改）");
        Assert.True(renamed.IsSuccess);
        Assert.Equal("实现批处理（改）", renamed.Value!.Content);
        Assert.True(renamed.Value!.IsCompleted); // 未指定的字段不动

        var deleted = await store.DeleteAsync(item.Id);
        Assert.True(deleted.IsSuccess);
        var listedAfter = await store.ListAsync(sessionId);
        Assert.Empty(listedAfter.Value!);
    }

    [Fact]
    public async Task Add_RejectsEmptyContent_AndUnknownSession()
    {
        var store = NewStore();
        var sessionId = await NewSessionAsync("guard");

        var empty = await store.AddAsync(sessionId, "   ");
        Assert.False(empty.IsSuccess);
        Assert.Contains("empty", empty.Error);

        var unknown = await store.AddAsync("no-such-session", "内容");
        Assert.False(unknown.IsSuccess);
        Assert.Contains("not found", unknown.Error);
    }

    [Fact]
    public async Task List_OrdersByPosition_AutoIncremented()
    {
        var store = NewStore();
        var sessionId = await NewSessionAsync("order");

        var a = await store.AddAsync(sessionId, "第一步");
        var b = await store.AddAsync(sessionId, "第二步");
        var c = await store.AddAsync(sessionId, "第三步");
        Assert.True(a.IsSuccess && b.IsSuccess && c.IsSuccess);
        Assert.True(a.Value!.Position < b.Value!.Position);
        Assert.True(b.Value!.Position < c.Value!.Position);

        var listed = await store.ListAsync(sessionId);
        var contents = listed.Value!.Select(t => t.Content).ToList();
        Assert.Equal(new[] { "第一步", "第二步", "第三步" }, contents);
    }

    [Fact]
    public async Task Sessions_AreIsolated()
    {
        var store = NewStore();
        var sessionA = await NewSessionAsync("A");
        var sessionB = await NewSessionAsync("B");

        await store.AddAsync(sessionA, "A 的待办");
        await store.AddAsync(sessionB, "B 的待办");
        await store.AddAsync(sessionB, "B 的第二项");

        var listA = await store.ListAsync(sessionA);
        var listB = await store.ListAsync(sessionB);
        var itemA = Assert.Single(listA.Value!);
        Assert.Equal("A 的待办", itemA.Content);
        Assert.Equal(2, listB.Value!.Count);
        Assert.All(listB.Value!, t => Assert.DoesNotContain("A 的待办", t.Content));

        // 清空只影响目标会话。
        var cleared = await store.ClearAsync(sessionB);
        Assert.True(cleared.IsSuccess);
        Assert.Equal(2, cleared.Value);
        Assert.Single((await store.ListAsync(sessionA)).Value!);
    }

    [Fact]
    public async Task Update_And_Delete_UnknownTodo_FailHonest()
    {
        var store = NewStore();

        var updateUnknown = await store.UpdateAsync("missing-id", isCompleted: true);
        Assert.False(updateUnknown.IsSuccess);
        Assert.Contains("not found", updateUnknown.Error);

        var deleteUnknown = await store.DeleteAsync("missing-id");
        Assert.False(deleteUnknown.IsSuccess);
        Assert.Contains("not found", deleteUnknown.Error);

        var nothing = await store.UpdateAsync("missing-id");
        Assert.False(nothing.IsSuccess);
        Assert.Contains("nothing to update", nothing.Error);
    }

    [Fact]
    public async Task LegacyDatabase_GetsTodoTable_ViaEnsureSchema()
    {
        // 模拟批次 A 时代的存量库：删掉 todo_items（EnsureSchemaAsync 必须幂等补建）。
        await using (var db = new ConversationDbContext(_options))
        {
            await db.Database.ExecuteSqlRawAsync("DROP TABLE todo_items;");
        }

        await using (var db = new ConversationDbContext(_options))
        {
            await ConversationDbContext.EnsureSchemaAsync(db);
            await ConversationDbContext.EnsureSchemaAsync(db); // 幂等：重复执行不抛
        }

        // 升级后 todo 工具立即可用（真实 EF 读写新表）。
        var store = NewStore();
        var sessionId = await NewSessionAsync("legacy");
        var add = await store.AddAsync(sessionId, "升级后写入");
        Assert.True(add.IsSuccess);
        var listed = await store.ListAsync(sessionId);
        Assert.Single(listed.Value!);
    }

    // ---------- TodoToolbox 工具域（真实 store + 真实 DB）----------

    [Fact]
    public async Task Toolbox_Add_List_Update_Delete_EndToEnd()
    {
        var store = NewStore();
        var sessionId = await NewSessionAsync("toolbox");
        var (box, _) = NewBox(store, sessionId);

        var add = await box.InvokeAsync("todo_add", "{\"content\":\"跑全量回归\"}", CancellationToken.None);
        Assert.True(add.Success);
        var id = ExtractId(add.Output);

        var list = await box.InvokeAsync("todo_list", "{}", CancellationToken.None);
        Assert.True(list.Success);
        Assert.Contains("[ ] 跑全量回归", list.Output);

        var update = await box.InvokeAsync("todo_update", $"{{\"id\":\"{id}\",\"completed\":true}}", CancellationToken.None);
        Assert.True(update.Success);
        Assert.Contains("[x] 跑全量回归", update.Output);

        var listAfter = await box.InvokeAsync("todo_list", "{}", CancellationToken.None);
        Assert.Contains("[x] 跑全量回归", listAfter.Output);

        var delete = await box.InvokeAsync("todo_delete", $"{{\"id\":\"{id}\"}}", CancellationToken.None);
        Assert.True(delete.Success);
        var listEmpty = await box.InvokeAsync("todo_list", "{}", CancellationToken.None);
        Assert.Contains("Task list is empty", listEmpty.Output);
    }

    [Fact]
    public async Task Toolbox_SessionBinding_FollowsAccessor()
    {
        var store = NewStore();
        var sessionA = await NewSessionAsync("bindA");
        var sessionB = await NewSessionAsync("bindB");
        var current = sessionA;
        var box = new TodoToolbox(store, () => current);

        var add = await box.InvokeAsync("todo_add", "{\"content\":\"绑定态待办\"}", CancellationToken.None);
        Assert.True(add.Success);

        // 访问器切换会话后：旧会话数据不可见，新写入进入新会话（会话隔离经工具域生效）。
        current = sessionB;
        var listB = await box.InvokeAsync("todo_list", "{}", CancellationToken.None);
        Assert.Contains("Task list is empty", listB.Output);
        var addB = await box.InvokeAsync("todo_add", "{\"content\":\"B 侧待办\"}", CancellationToken.None);
        Assert.True(addB.Success);

        current = sessionA;
        var listA = await box.InvokeAsync("todo_list", "{}", CancellationToken.None);
        Assert.Contains("绑定态待办", listA.Output);
        Assert.DoesNotContain("B 侧待办", listA.Output);
    }

    [Fact]
    public async Task Toolbox_ParameterAndBinding_ErrorsAreHonest()
    {
        var store = NewStore();
        var sessionId = await NewSessionAsync("errs");
        var (box, _) = NewBox(store, sessionId);

        // 缺参/坏 JSON/未知工具。
        Assert.False((await box.InvokeAsync("todo_add", "{}", CancellationToken.None)).Success);
        Assert.False((await box.InvokeAsync("todo_add", "not-json", CancellationToken.None)).Success);
        Assert.False((await box.InvokeAsync("todo_update", "{\"id\":\"x\"}", CancellationToken.None)).Success);
        Assert.False((await box.InvokeAsync("todo_nonexistent", "{}", CancellationToken.None)).Success);

        // 会话未绑定：诚实失败，不静默写库。
        var unbound = new TodoToolbox(store, () => string.Empty);
        var noSession = await unbound.InvokeAsync("todo_add", "{\"content\":\"孤儿\"}", CancellationToken.None);
        Assert.False(noSession.Success);
        Assert.Contains("no session is bound", noSession.Output);

        // add 的真实拒绝：未知会话经绑定访问器传穿。
        var badSession = new TodoToolbox(store, () => "ghost-session");
        var ghost = await badSession.InvokeAsync("todo_add", "{\"content\":\"幽灵\"}", CancellationToken.None);
        Assert.False(ghost.Success);
        Assert.Contains("not found", ghost.Output);
    }
}
