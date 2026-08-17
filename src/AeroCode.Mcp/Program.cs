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
        await builder.Build().RunAsync();
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddLogging(b => b.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Warning));
        var dbPath = System.IO.Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            "AeroCode", "aerocode.db");
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dbPath)!);
        services.AddDbContext<AeroCode.Core.Data.AeroCodeDbContext>(opt => opt.UseSqlite($"Data Source={dbPath}"));
        services.AddSingleton<ITagService, TagService>();
        services.AddSingleton<INotebookService, NotebookService>();
        services.AddSingleton<INoteService, NoteService>();
        services.AddSingleton<ISearchService, SearchService>();
    }
}
