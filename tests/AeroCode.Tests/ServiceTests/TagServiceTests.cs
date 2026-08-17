using System.Threading.Tasks;
using AeroCode.Core.Data;
using AeroCode.Core.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AeroCode.Tests.ServiceTests;

public class TagServiceTests
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
    public async Task CreateOrGet_NormalizesCase()
    {
        using var db = NewDb();
        var svc = new TagService(db);
        var a = await svc.CreateOrGetAsync("Work");
        var b = await svc.CreateOrGetAsync("WORK");
        var c = await svc.CreateOrGetAsync("work");

        Assert.True(a.IsSuccess);
        Assert.Equal(a.Value!.Id, b.Value!.Id);
        Assert.Equal(a.Value!.Id, c.Value!.Id);
    }

    [Fact]
    public async Task CreateOrGet_EmptyName_Fails()
    {
        using var db = NewDb();
        var svc = new TagService(db);
        var r = await svc.CreateOrGetAsync("  ");

        Assert.False(r.IsSuccess);
    }
}
