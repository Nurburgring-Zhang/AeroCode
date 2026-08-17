using System.Linq;
using System.Threading.Tasks;
using AeroCode.Core.Data;
using AeroCode.Core.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AeroCode.Tests.ServiceTests;

public class NotebookServiceTests
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
    public async Task Create_Root_AssignsSortOrder()
    {
        using var db = NewDb();
        var svc = new NotebookService(db);
        var r1 = await svc.CreateAsync("工作", null, null);
        var r2 = await svc.CreateAsync("学习", null, null);

        Assert.True(r1.IsSuccess && r2.IsSuccess);
        Assert.Equal(0, r1.Value!.SortOrder);
        Assert.Equal(1, r2.Value!.SortOrder);
    }

    [Fact]
    public async Task Create_Nested_StoresParentId()
    {
        using var db = NewDb();
        var svc = new NotebookService(db);
        var root = await svc.CreateAsync("工作", null, null);
        var child = await svc.CreateAsync("项目A", null, root.Value!.Id);

        Assert.True(child.IsSuccess);
        Assert.Equal(root.Value.Id, child.Value!.ParentId);
    }

    [Fact]
    public async Task Delete_NotEmpty_WithoutCascade_Fails()
    {
        using var db = NewDb();
        var nbSvc = new NotebookService(db);
        var noteSvc = new NoteService(db, new TagService(db));

        var nb = await nbSvc.CreateAsync("工作", null, null);
        await noteSvc.CreateAsync("第一条", "x", nb.Value!.Id);

        var del = await nbSvc.DeleteAsync(nb.Value!.Id, cascade: false);
        Assert.False(del.IsSuccess);
        Assert.Contains("cascade=true", del.Error);
    }

    [Fact]
    public async Task Delete_WithCascade_RemovesNotes()
    {
        using var db = NewDb();
        var nbSvc = new NotebookService(db);
        var noteSvc = new NoteService(db, new TagService(db));

        var nb = await nbSvc.CreateAsync("工作", null, null);
        await noteSvc.CreateAsync("第一条", "x", nb.Value!.Id);

        var del = await nbSvc.DeleteAsync(nb.Value!.Id, cascade: true);
        Assert.True(del.IsSuccess);

        var notes = await noteSvc.GetByNotebookAsync(nb.Value!.Id);
        Assert.Empty(notes.Value!);
    }
}
