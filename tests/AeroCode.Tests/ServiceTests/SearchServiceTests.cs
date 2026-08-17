// Copyright (c) AeroCode V3.0
// SearchService tests — verifies FTS5 + LIKE fallback.
using System.Linq;
using System.Threading.Tasks;
using AeroCode.Core.Data;
using AeroCode.Core.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AeroCode.Tests.ServiceTests;

public class SearchServiceTests
{
    private static AeroCodeDbContext NewDb()
    {
        var opts = new DbContextOptionsBuilder<AeroCodeDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        var db = new AeroCodeDbContext(opts);
        db.Database.OpenConnection();
        db.Database.EnsureCreated();
        FtsMigrations.EnsureFts5(db);  // Real FTS5
        return db;
    }

    [Fact]
    public async Task Search_EnglishHit_ReturnsNote()
    {
        using var db = NewDb();
        var svc = new SearchService(db);
        var noteSvc = new NoteService(db, new TagService(db));
        await noteSvc.CreateAsync("Quantum Computing", "qubits superposition entanglement", null);
        await noteSvc.CreateAsync("Cooking Recipes", "pasta carbonara", null);

        var r = await svc.SearchAsync("quantum");
        Assert.True(r.IsSuccess);
        Assert.Single(r.Value!);
        Assert.Equal("Quantum Computing", r.Value![0].Title);
    }

    [Fact]
    public async Task Search_MultipleTermsHit_ReturnsMultiple()
    {
        using var db = NewDb();
        var svc = new SearchService(db);
        var noteSvc = new NoteService(db, new TagService(db));
        await noteSvc.CreateAsync("Rust async", "tokio runtime", null);
        await noteSvc.CreateAsync("Rust ownership", "borrowing lifetimes", null);
        await noteSvc.CreateAsync("Python asyncio", "event loop", null);

        var r = await svc.SearchAsync("Rust");
        Assert.True(r.IsSuccess);
        Assert.Equal(2, r.Value!.Count);
    }

    [Fact]
    public async Task Search_CjkFallback_UsesLike()
    {
        using var db = NewDb();
        var svc = new SearchService(db);
        var noteSvc = new NoteService(db, new TagService(db));
        await noteSvc.CreateAsync("中文笔记", "这是关于深度学习的内容", null);
        await noteSvc.CreateAsync("English Note", "deep learning python", null);

        var r = await svc.SearchAsync("深度学习");
        Assert.True(r.IsSuccess);
        Assert.Single(r.Value!);
        Assert.Equal("中文笔记", r.Value![0].Title);
    }

    [Fact]
    public async Task Search_NoHit_ReturnsEmpty()
    {
        using var db = NewDb();
        var svc = new SearchService(db);
        var noteSvc = new NoteService(db, new TagService(db));
        await noteSvc.CreateAsync("Foo", "bar baz", null);

        var r = await svc.SearchAsync("nonexistent_xyz");
        Assert.True(r.IsSuccess);
        Assert.Empty(r.Value!);
    }

    [Fact]
    public async Task Search_AfterUpdate_FtsIsUpdated()
    {
        using var db = NewDb();
        var svc = new SearchService(db);
        var noteSvc = new NoteService(db, new TagService(db));
        var created = await noteSvc.CreateAsync("Original", "alpha beta", null);
        await noteSvc.UpdateAsync(created.Value!.Id, "Updated", "gamma delta", null, null);

        var r1 = await svc.SearchAsync("alpha");
        Assert.Empty(r1.Value!);  // 旧内容已不在

        var r2 = await svc.SearchAsync("gamma");
        Assert.Single(r2.Value!);
    }

    [Fact]
    public async Task Search_Limit_Respected()
    {
        using var db = NewDb();
        var svc = new SearchService(db);
        var noteSvc = new NoteService(db, new TagService(db));
        for (var i = 0; i < 10; i++)
            await noteSvc.CreateAsync($"Note {i}", "common keyword xyz", null);

        var r = await svc.SearchAsync("common", limit: 3);
        Assert.Equal(3, r.Value!.Count);
    }

    [Fact]
    public async Task Search_ExcludesSoftDeleted()
    {
        using var db = NewDb();
        var svc = new SearchService(db);
        var noteSvc = new NoteService(db, new TagService(db));
        var n1 = await noteSvc.CreateAsync("Keep", "unique_keyword_aaa", null);
        var n2 = await noteSvc.CreateAsync("Delete", "unique_keyword_bbb", null);
        await noteSvc.SoftDeleteAsync(n2.Value!.Id);

        var r = await svc.SearchAsync("unique_keyword_bbb");
        Assert.Empty(r.Value!);  // soft-deleted excluded
    }
}
