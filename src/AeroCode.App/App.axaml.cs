using System;
using System.IO;
using System.Threading.Tasks;
using AeroAgent.Conversation.Data;
using AeroAgent.Conversation.Orchestration;
using AeroAgent.Conversation.Services;
using AeroAgent.Moa.Aggregation;
using AeroAgent.Moa.Assignment;
using AeroAgent.Moa.Planning;
using AeroAgent.Moa.Profiles;
using AeroAgent.Moa.Strategies;
using AeroCode.AI.Embedding;
using AeroCode.AI.Providers;
using AeroCode.AI.Telemetry;
using AeroCode.App.Configuration;
using AeroCode.App.Services;
using AeroCode.App.ViewModels;
using AeroCode.App.Views;
using AeroCode.Core.Data;
using AeroCode.Core.Services;
using AeroCode.Harness;
using AeroCode.Skills;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AeroCode.App;

public partial class App : Application
{
    private ServiceProvider? _services;

    public static ServiceProvider Services => ((App)Current!)._services!;

    public override void Initialize() { AvaloniaXamlLoader.Load(this); }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            try
            {
                _services = BuildServices();
                ApplyMigrations(_services);

                var main = _services.GetRequiredService<MainWindow>();
                desktop.MainWindow = main;
                main.DataContext = _services.GetRequiredService<MainWindowViewModel>();

                // Eagerly initialize V3 VMs so they subscribe to EventBus
                _services.GetRequiredService<SkillsViewModel>();
                _services.GetRequiredService<DiagnosticsViewModel>();
                _services.GetRequiredService<MemoryViewModel>();
                _services.GetRequiredService<CodeReviewViewModel>();
            }
            catch (Exception ex)
            {
                LogToFile("FATAL", $"初始化失败: {ex}");
                throw;
            }
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Build DI container, initialize Settings, ProviderFactory, SkillHub, HarnessHost.
    /// All initialization is synchronous (GetAwaiter().GetResult()) because
    /// OnFrameworkInitializationCompleted is sync and we need everything ready
    /// before the first view is shown.
    /// </summary>
    private static ServiceProvider BuildServices()
    {
        var sc = new ServiceCollection();
        var paths = new AppDataPaths();
        sc.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));
        sc.AddSingleton(paths);
        sc.AddSingleton<SettingsService>();

        // 1. Load settings synchronously (SettingsService is sync-ready)
        var settings = new SettingsService(paths);
        try
        {
            settings.LoadAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            LogToFile("WARN", $"Settings load failed, using defaults: {ex.Message}");
            // Fall through with default settings
        }

        // 1b. Apply theme (before any view is rendered)
        var themeService = new ThemeService();
        themeService.Apply(settings.Current.Ui.Theme);
        sc.AddSingleton(themeService);

        // 2. Build AI options + ProviderFactory (singleton)
        var aiOptions = settings.ToAiOptions();
        var loggerFactory = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));
        var providerFactory = new ProviderFactory(aiOptions, loggerFactory);
        sc.AddSingleton(providerFactory);
        sc.AddSingleton<IProviderRegistry>(providerFactory);
        sc.AddSingleton(loggerFactory);

        // 2b. V3.2 OpenTelemetry bootstrapper (real CNCF SDK). Set AEROCODE_OTLP_ENDPOINT env to enable OTLP export.
        var otelOpts = new OtelOptions
        {
            ServiceName = "AeroCode",
            ServiceVersion = "3.2.0",
            OtlpEndpoint = Environment.GetEnvironmentVariable("AEROCODE_OTLP_ENDPOINT"),
            EnableConsoleExporter = false, // don't spam stdout in the app
            EnableHttpClientInstrumentation = true,
            EnableRuntimeInstrumentation = true,
            TraceSamplingRatio = 1.0
        };
        var otel = new OtelBootstrapper(otelOpts);
        sc.AddSingleton(otel);
        sc.AddSingleton(otel.Metrics);
        sc.AddSingleton(otel.ActivitySource);

        // 2c. V3.2 Embedding (real HTTP to Ollama or OpenAI-compatible). Used by SemanticSearcher for cosine top-K.
        var ollamaUrl = Environment.GetEnvironmentVariable("AEROCODE_OLLAMA_URL") ?? "http://localhost:11434";
        var embeddingClient = new EmbeddingClient(new EmbeddingClientOptions
        {
            BaseUrl = ollamaUrl,
            Model = Environment.GetEnvironmentVariable("AEROCODE_EMBEDDING_MODEL") ?? "all-minlm-l6-v2",
            Backend = EmbeddingBackend.Ollama
        });
        var vectorStore = new VectorStore();
        sc.AddSingleton(embeddingClient);
        sc.AddSingleton(vectorStore);

        // 3. Database (EF Core SQLite)
        var dbPath = paths.DatabaseFile;
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        sc.AddDbContext<AeroCodeDbContext>(opt => opt.UseSqlite($"Data Source={dbPath}"));

        // 3b. 统一对话（AeroAgent.Conversation）。独立 SQLite 库，单例匹配本应用
        //     既有约定（笔记服务亦为单例）；SessionService 内部以互斥锁串行化
        //     DbContext 操作，MOA 并行 worker 的并发持久化由该锁保证。
        var convPath = paths.ConversationDatabaseFile;
        Directory.CreateDirectory(Path.GetDirectoryName(convPath)!);
        var convOptions = new DbContextOptionsBuilder<ConversationDbContext>()
            .UseSqlite($"Data Source={convPath}")
            .Options;
        var convDb = new ConversationDbContext(convOptions);
        convDb.Database.EnsureCreated();
        // 既有库补列（如 Phase 1 库缺 chat_messages.Label / IsFinal）——幂等。
        ConversationDbContext.EnsureSchemaAsync(convDb).GetAwaiter().GetResult();
        sc.AddSingleton(convDb);
        sc.AddSingleton<ISessionService, SessionService>();

        // 3c. MOA 编排（AeroAgent.Moa）。画像目录：文件覆盖内建种子；
        //     编排选项：缺失/损坏时回退默认（JsonMoaOptionsStore 自带容错）。
        var profileCatalog = new ModelProfileCatalog(new JsonFileProfileStore(paths.MoaProfilesFile));
        profileCatalog.LoadAsync(BuiltInProfiles.Seed()).GetAwaiter().GetResult();
        sc.AddSingleton<IModelProfileCatalog>(profileCatalog);
        sc.AddSingleton(profileCatalog);

        var moaOptions = new JsonMoaOptionsStore(paths.MoaOptionsFile)
            .LoadAsync().GetAwaiter().GetResult();
        sc.AddSingleton(moaOptions);

        sc.AddSingleton<WorkerRunner>();
        sc.AddSingleton<ModelAssigner>();
        sc.AddSingleton<ModelResolver>();
        sc.AddSingleton<TaskPlanner>();
        sc.AddSingleton<Synthesizer>();

        sc.AddSingleton<IOrchestrationStrategy, SingleStrategy>();
        sc.AddSingleton<IOrchestrationStrategy, RouterStrategy>();
        sc.AddSingleton<IOrchestrationStrategy, DecomposeStrategy>();
        sc.AddSingleton<IOrchestrationStrategy, EnsembleStrategy>();
        sc.AddSingleton<IOrchestrationStrategy, PipelineStrategy>();
        sc.AddSingleton<IChatOrchestrationFacade, ChatOrchestrationFacade>();
        sc.AddSingleton<ChatViewModel>();

        // 4. Core services
        sc.AddSingleton<ITagService, TagService>();
        sc.AddSingleton<INotebookService, NotebookService>();
        sc.AddSingleton<INoteService, NoteService>();
        sc.AddSingleton<ISearchService, SearchService>();

        // 5. App services
        sc.AddSingleton<IDialogService, DialogService>();

        // 6. V3 Skills engine
        var skillsRoot = Path.Combine(paths.RootDirectory, "skills");
        var skillHub = new SkillHub(skillsRoot);
        skillHub.LoadFromDisk();  // Sync — load all user SKILL.md files
        sc.AddSingleton(skillHub);

        // 7. V3 Harness engine (uses providerFactory + eventBus)
        var harnessHost = new HarnessHost();
        sc.AddSingleton(harnessHost);

        // 8. ViewModels
        sc.AddSingleton<MainWindowViewModel>();
        sc.AddSingleton<AIAssistantViewModel>();
        sc.AddSingleton<SkillsViewModel>();
        sc.AddSingleton<MemoryViewModel>();
        sc.AddSingleton<CodeReviewViewModel>();
        sc.AddSingleton<DiagnosticsViewModel>();
        sc.AddSingleton<SettingsViewModel>();
        sc.AddSingleton<MainWindow>();

        return sc.BuildServiceProvider(validateScopes: false);
    }

    private static void ApplyMigrations(IServiceProvider sp)
    {
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroCodeDbContext>();
        db.Database.EnsureCreated();
    }

    private static void LogToFile(string level, string msg)
    {
        try
        {
            var dir = new AppDataPaths().LogDirectory;
            Directory.CreateDirectory(dir);
            File.AppendAllText(
                Path.Combine(dir, $"aerocode-{DateTime.UtcNow:yyyyMMdd}.log"),
                $"[{DateTime.UtcNow:O}] [{level}] {msg}{Environment.NewLine}");
        }
        catch { }
    }
}
