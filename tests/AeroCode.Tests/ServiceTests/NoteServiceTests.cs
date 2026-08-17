using System.Linq;
using System.Threading.Tasks;
using AeroCode.Core.Data;
using AeroCode.Core.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AeroCode.Tests.ServiceTests;

public class NoteServiceTests
{
    private static AeroCodeDbContext NewDb()
    {
        var opts = new DbContextOptionsBuilder<AeroCodeDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        var db = new AeroCodeDbContext(opts);
        db.Database.OpenConnection();
        db.Database.EnsureCreated();
        return db;
    }

    [Fact]
    public async Task Create_WithTitle_StoresNote()
    {
        using var db = NewDb();
        var svc = new NoteService(db, new TagService(db));
        var r = await svc.CreateAsync("测试笔记", "内容ABC", null);

        Assert.True(r.IsSuccess);
        Assert.Equal("测试笔记", r.Value!.Title);
        Assert.Equal("内容ABC", r.Value.Content);
        Assert.Equal(3, r.Value.WordCount); // 中英混排: "内" "容" "ABC" = 3 word
    }

    [Fact]
    public async Task Create_EmptyTitle_Fails()
    {
        using var db = NewDb();
        var svc = new NoteService(db, new TagService(db));
        var r = await svc.CreateAsync("", "内容");

        Assert.False(r.IsSuccess);
        Assert.Contains("标题不能为空", r.Error);
    }

    [Fact]
    public async Task Update_ChangesContent()
    {
        using var db = NewDb();
        var svc = new NoteService(db, new TagService(db));
        var create = await svc.CreateAsync("A", "1");
        var r = await svc.UpdateAsync(create.Value!.Id, null, "新内容", null, null);

        Assert.True(r.IsSuccess);
        Assert.Equal("新内容", r.Value!.Content);
    }

    [Fact]
    public async Task SoftDelete_HidesFromDefaultList()
    {
        using var db = NewDb();
        var svc = new NoteService(db, new TagService(db));
        var create = await svc.CreateAsync("A", "x");
        await svc.SoftDeleteAsync(create.Value!.Id);

        var all = await svc.GetAllAsync(includeDeleted: false);
        Assert.True(all.IsSuccess);
        Assert.Empty(all.Value!);

        var withDel = await svc.GetAllAsync(includeDeleted: true);
        Assert.Single(withDel.Value!);
    }

    [Fact]
    public async Task TogglePin_TogglesState()
    {
        using var db = NewDb();
        var svc = new NoteService(db, new TagService(db));
        var create = await svc.CreateAsync("A", "x");
        var first = await svc.TogglePinAsync(create.Value!.Id);
        var second = await svc.TogglePinAsync(create.Value!.Id);

        Assert.True(first.IsSuccess && first.Value);
        Assert.True(second.IsSuccess && !second.Value);
    }

    [Fact]
    public async Task SetTags_CreatesAndLinksTags()
    {
        using var db = NewDb();
        var tagSvc = new TagService(db);
        var svc = new NoteService(db, tagSvc);
        var create = await svc.CreateAsync("A", "x");
        var r = await svc.SetTagsAsync(create.Value!.Id, new[] { "工作", "重要", "工作" });

        Assert.True(r.IsSuccess);
        var tags = await tagSvc.GetAllAsync();
        Assert.Equal(2, tags.Value!.Count);
    }

    [Fact]
    public async Task GetById_NotFound_Fails()
    {
        using var db = NewDb();
        var svc = new NoteService(db, new TagService(db));
        var r = await svc.GetByIdAsync(999);

        Assert.False(r.IsSuccess);
    }
}
