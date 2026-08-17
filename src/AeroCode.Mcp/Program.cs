using System.Threading.Tasks;
using AeroCode.Core.Data;
using AeroCode.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace AeroCode.Mcp;

public static class Program
{
    public static async Task Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        ConfigureServices(builder.Services);
        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithToolsFromAssembly()
            .WithPromptsFromAssembly();
        var host = builder.Build();

        // 首次启动建库建表（桌面 App 有自己的 EnsureCreated 入口；
        // MCP server 作为独立宿主必须同样负责 schema 引导，否则全新环境无表可写）。
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroCodeDbContext>();
            db.Database.EnsureCreated();
            FtsMigrations.EnsureFts5(db);
        }

        await host.RunAsync();
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        // stdio 传输的 stdout 只能跑 JSON-RPC：所有级别日志一律走 stderr。
        services.AddLogging(b => b.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace));
        // AEROCODE_DB_PATH 覆盖：E2E/隔离环境指向临时库，不污染用户真实笔记库。
        var dbPath = Environment.GetEnvironmentVariable("AEROCODE_DB_PATH");
        if (string.IsNullOrWhiteSpace(dbPath))
        {
            dbPath = System.IO.Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                "AeroCode", "aerocode.db");
        }
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(dbPath))!);
        services.AddDbContext<AeroCode.Core.Data.AeroCodeDbContext>(opt => opt.UseSqlite($"Data Source={dbPath}"));
        services.AddSingleton<ITagService, TagService>();
        services.AddSingleton<INotebookService, NotebookService>();
        services.AddSingleton<INoteService, NoteService>();
        services.AddSingleton<ISearchService, SearchService>();
    }
}
