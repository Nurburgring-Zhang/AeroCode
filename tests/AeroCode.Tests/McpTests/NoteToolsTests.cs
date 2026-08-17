// Copyright (c) AeroCode V3.0
// NoteTools (MCP) tests — verifies the business surface area exposed to MCP clients.
using System.Text.Json;
using System.Threading.Tasks;
using AeroCode.Core.Data;
using AeroCode.Core.Services;
using AeroCode.Mcp.Tools;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AeroCode.Tests.McpTests;

public class NoteToolsTests
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

    private static NoteTools MakeTools(AeroCodeDbContext db)
        => new(new NoteService(db, new TagService(db)),
               new NotebookService(db),
               new TagService(db),
               new SearchService(db));

    [Fact]
    public async Task ListNotes_Empty_ReturnsZeroCount()
    {
        using var db = NewDb();
        var tools = MakeTools(db);
        var json = await tools.ListNotes();
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(0, doc.RootElement.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task CreateNote_ReturnsId()
    {
        using var db = NewDb();
        var tools = MakeTools(db);
        var json = await tools.CreateNote("Hello MCP");
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("id").GetInt64() > 0);
    }

    [Fact]
    public async Task GetNote_AfterCreate_ContainsContent()
    {
        using var db = NewDb();
        var tools = MakeTools(db);
        var c = await tools.CreateNote("Title X", "Body Y");
        using var cd = JsonDocument.Parse(c);
        var id = cd.RootElement.GetProperty("id").GetInt64();
        var g = await tools.GetNote(id);
        using var gd = JsonDocument.Parse(g);
        // NoteTools uses anonymous-object serialization → PascalCase by default.
        Assert.Equal("Body Y", gd.RootElement.GetProperty("Content").GetString());
        Assert.Equal("Title X", gd.RootElement.GetProperty("Title").GetString());
    }

    [Fact]
    public async Task UpdateNote_ChangesTitle()
    {
        using var db = NewDb();
        var tools = MakeTools(db);
        var c = await tools.CreateNote("Old", "Body");
        using var cd = JsonDocument.Parse(c);
        var id = cd.RootElement.GetProperty("id").GetInt64();
        var u = await tools.UpdateNote(id, "New");
        Assert.Equal("OK", u);
        var g = await tools.GetNote(id);
        using var gd = JsonDocument.Parse(g);
        Assert.Equal("New", gd.RootElement.GetProperty("Title").GetString());
    }

    [Fact]
    public async Task DeleteNote_ThenList_HidesIt()
    {
        using var db = NewDb();
        var tools = MakeTools(db);
        var c = await tools.CreateNote("Throwaway");
        using var cd = JsonDocument.Parse(c);
        var id = cd.RootElement.GetProperty("id").GetInt64();
        await tools.DeleteNote(id);
        var l = await tools.ListNotes();
        using var ld = JsonDocument.Parse(l);
        Assert.Equal(0, ld.RootElement.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task SearchNotes_EnglishTerm_HitsNote()
    {
        using var db = NewDb();
        var tools = MakeTools(db);
        await tools.CreateNote("Quantum Computing", "qubit superposition entanglement");
        await tools.CreateNote("Cooking", "pasta carbonara");
        var s = await tools.SearchNotes("quantum");
        using var sd = JsonDocument.Parse(s);
        Assert.True(sd.RootElement.GetProperty("count").GetInt32() >= 1);
    }

    [Fact]
    public async Task ListNotebooks_AndCreate_AndList()
    {
        using var db = NewDb();
        var tools = MakeTools(db);
        var l0 = await tools.ListNotebooks();
        using var l0d = JsonDocument.Parse(l0);
        Assert.Equal(0, l0d.RootElement.GetProperty("notebooks").GetArrayLength());
        var c = await tools.CreateNotebook("Research");
        using var cd = JsonDocument.Parse(c);
        Assert.True(cd.RootElement.GetProperty("ok").GetBoolean());
        var l1 = await tools.ListNotebooks();
        using var l1d = JsonDocument.Parse(l1);
        Assert.Equal(1, l1d.RootElement.GetProperty("notebooks").GetArrayLength());
    }

    [Fact]
    public async Task SetNoteTags_LinkAndRetrieve()
    {
        using var db = NewDb();
        var tools = MakeTools(db);
        var c = await tools.CreateNote("Tagged");
        using var cd = JsonDocument.Parse(c);
        var id = cd.RootElement.GetProperty("id").GetInt64();
        await tools.SetNoteTags(id, new[] { "AI", "research" });
        var t = await tools.GetNotesByTag("AI");
        using var td = JsonDocument.Parse(t);
        Assert.True(td.RootElement.GetProperty("count").GetInt32() >= 1);
    }

    [Fact]
    public async Task TogglePin_FlipsState()
    {
        using var db = NewDb();
        var tools = MakeTools(db);
        var c = await tools.CreateNote("Pin me");
        using var cd = JsonDocument.Parse(c);
        var id = cd.RootElement.GetProperty("id").GetInt64();
        var p1 = await tools.TogglePin(id);
        using var p1d = JsonDocument.Parse(p1);
        Assert.True(p1d.RootElement.GetProperty("is_pinned").GetBoolean());
        var p2 = await tools.TogglePin(id);
        using var p2d = JsonDocument.Parse(p2);
        Assert.False(p2d.RootElement.GetProperty("is_pinned").GetBoolean());
    }

    [Fact]
    public async Task GetNote_InvalidId_ReturnsError()
    {
        using var db = NewDb();
        var tools = MakeTools(db);
        var r = await tools.GetNote(99999);
        Assert.StartsWith("Error:", r);
    }
}
