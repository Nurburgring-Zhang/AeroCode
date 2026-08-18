using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AeroAgent.Conversation.Data;
using AeroAgent.Conversation.Orchestration;
using AeroAgent.Conversation.Services;
using AeroAgent.Moa.Aggregation;
using AeroAgent.Moa.Assignment;
using AeroAgent.Moa.Planning;
using AeroAgent.Moa.Profiles;
using AeroAgent.Moa.Strategies;
using AeroAgent.Moa.Tools;
using AeroCode.AI.Embedding;
using AeroCode.AI.Providers;
using AeroCode.AI.Telemetry;
using AeroCode.App.Configuration;
using AeroCode.App.Mcp;
using AeroCode.App.Services;
using AeroCode.App.Tools;
using AeroCode.App.ViewModels;
using AeroCode.App.Views;
using AeroCode.Core.Data;
using AeroCode.Core.Services;
using AeroCode.Harness;
using AeroCode.Harness.Permission;
using AeroCode.Mcp.Client;
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
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
        {
            // PHASE 4：Android（single-view）——同一套服务，主视图直接挂 MainView（无 Window）。
            try
            {
                _services = BuildServices();
                ApplyMigrations(_services);

                var main = new MainView
                {
                    DataContext = _services.GetRequiredService<MainWindowViewModel>()
                };
                singleView.MainView = main;

                // Eagerly initialize V3 VMs so they subscribe to EventBus
                _services.GetRequiredService<SkillsViewModel>();
                _services.GetRequiredService<DiagnosticsViewModel>();
                _services.GetRequiredService<MemoryViewModel>();
                _services.GetRequiredService<CodeReviewViewModel>();
            }
            catch (Exception ex)
            {
                LogToFile("FATAL", $"初始化失败(single-view): {ex}");
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

        // 1. Load settings synchronously (SettingsService is sync-ready)。
        //    注册"已加载的这个实例"——按类型注册会让 DI 另造一个未 Load 的空实例，
        //    设置页将水合空白配置并在保存时擦掉 settings.json。
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

        sc.AddSingleton(settings);

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

        var moaOptionsStore = new JsonMoaOptionsStore(paths.MoaOptionsFile);
        var moaOptions = moaOptionsStore.LoadAsync().GetAwaiter().GetResult();
        sc.AddSingleton(moaOptionsStore);
        sc.AddSingleton(moaOptions);

        // 3d. Harness 与工具内核。HarnessHost 提前创建：其 PermissionPolicy 是
        //     ToolRouter 的唯一裁决源；注册表/路由器以单例实例注入，
        //     WorkerRunner 的可选 ToolRouter 构造参数由 MS.DI 自动解析。
        //     工具箱本体（内建 + MCP）在容器构建后注册（需要解析 Core 服务）。
        var harnessHost = new HarnessHost();
        sc.AddSingleton(harnessHost);
        sc.AddSingleton(harnessHost.Permission);

        // S7 授权链：持久化存储（permissions.json）+ 对话框代理（Ask → 真实授权窗口）。
        // 用户的持久化决策在 RegisterToolboxes 之后应用（用户决定优先于内建默认）。
        var permissionStore = new JsonPermissionStore(paths.PermissionsFile);
        sc.AddSingleton(permissionStore);
        var permissionBroker = new DialogPermissionBroker(
            harnessHost.Permission, permissionStore,
            new AvaloniaPermissionDialogPresenter(),
            loggerFactory.CreateLogger<DialogPermissionBroker>());
        sc.AddSingleton<IPermissionBroker>(permissionBroker);

        var toolboxRegistry = new ToolboxRegistry();
        var toolRouter = new ToolRouter(toolboxRegistry, harnessHost.Permission, permissionBroker);
        sc.AddSingleton(toolboxRegistry);
        sc.AddSingleton(toolRouter);

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
        // single-view 平台（Android）的对话框覆盖层宿主；桌面不消费但注册保持容器一致。
        sc.AddSingleton<OverlayService>();

        // 6. V3 Skills engine
        var skillsRoot = Path.Combine(paths.RootDirectory, "skills");
        var skillHub = new SkillHub(skillsRoot);
        skillHub.LoadFromDisk();  // Sync — load all user SKILL.md files
        sc.AddSingleton(skillHub);

        // 7. ViewModels
        sc.AddSingleton<MainWindowViewModel>();
        sc.AddSingleton<AIAssistantViewModel>();
        sc.AddSingleton<SkillsViewModel>();
        sc.AddSingleton<MemoryViewModel>();
        sc.AddSingleton<CodeReviewViewModel>();
        sc.AddSingleton<DiagnosticsViewModel>();
        sc.AddSingleton<SettingsViewModel>();
        sc.AddSingleton<MainWindow>();

        var serviceProvider = sc.BuildServiceProvider(validateScopes: false);
        RegisterToolboxes(serviceProvider, settings, loggerFactory);
        ApplyPersistedPermissions(serviceProvider, loggerFactory);
        return serviceProvider;
    }

    /// <summary>
    /// 应用 permissions.json 中的用户决策，覆盖内建默认（笔记/技能工具 Allow、
    /// MCP 工具 Ask、CreateDefault 规则表）。必须在 RegisterToolboxes 之后执行：
    /// 用户记住的拒绝/询问优先于应用的便利默认。读取失败如实降级为默认策略。
    /// </summary>
    private static void ApplyPersistedPermissions(
        ServiceProvider services, ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("AeroCode.Permissions");
        PermissionSettings persisted;
        try
        {
            persisted = services.GetRequiredService<JsonPermissionStore>()
                .LoadAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            logger.LogWarning("[DEGRADED] 权限文件读取失败，使用内建默认策略：{Error}", ex.Message);
            return;
        }

        var permission = services.GetRequiredService<PermissionPolicy>();
        foreach (var (toolName, decision) in persisted.ToolDecisions)
        {
            permission.SetDefaultDecision(toolName, decision);
        }

        if (persisted.ToolDecisions.Count > 0)
        {
            logger.LogInformation("已恢复 {Count} 条持久化工具授权决策", persisted.ToolDecisions.Count);
        }
    }

    /// <summary>
    /// 容器构建后注册工具域：内建（笔记/技能）+ settings.json 中启用的 MCP 服务器。
    /// 权限默认裁决：内建工具 = 用户在笔记 UI 本来就能做的操作 → Allow
    /// （delete_note 的硬删除不可逆 → Override 升级为 Ask）；
    /// MCP 工具来自外部进程、副作用任意 → 保持 Ask（S7 授权代理落地后向用户询问）。
    /// MCP 配置错误绝不阻塞启动：发现失败如实降级记录，应用照常可用。
    /// </summary>
    private static void RegisterToolboxes(
        ServiceProvider services, SettingsService settings, ILoggerFactory loggerFactory)
    {
        var registry = services.GetRequiredService<ToolboxRegistry>();
        var permission = services.GetRequiredService<HarnessHost>().Permission;
        var logger = loggerFactory.CreateLogger("AeroCode.Toolboxes");

        var noteToolbox = new NoteToolbox(
            services.GetRequiredService<INoteService>(),
            services.GetRequiredService<INotebookService>(),
            services.GetRequiredService<ITagService>(),
            services.GetRequiredService<ISearchService>());
        registry.Register(noteToolbox);

        var skillToolbox = new SkillToolbox(
            services.GetRequiredService<SkillHub>(),
            services.GetRequiredService<IProviderRegistry>(),
            services.GetRequiredService<AppDataPaths>().RootDirectory,
            logger);
        registry.Register(skillToolbox);

        foreach (var def in noteToolbox.Definitions)
        {
            permission.SetDefaultDecision(def.Name, PermissionDecision.Allow);
        }

        permission.SetRule(new ToolPermissionRule
        {
            ToolName = "delete_note",
            DefaultDecision = PermissionDecision.Allow,
            Notes = "软删除可恢复→放行；硬删除不可逆→询问用户",
            Override = args => args is not null
                && args.TryGetValue("hard", out var hard)
                && hard is true
                ? PermissionDecision.Ask
                : PermissionDecision.Allow,
        });

        foreach (var def in skillToolbox.Definitions)
        {
            permission.SetDefaultDecision(def.Name, PermissionDecision.Allow);
        }

        var mcpConfigs = settings.Current.McpServers.Where(c => c.Enabled).ToList();
        if (mcpConfigs.Count == 0)
        {
            return;
        }

        var gateways = mcpConfigs
            .Select(c => (IMcpGateway)new McpGateway(c, logger))
            .ToList();
        var mcpToolbox = new McpToolbox(gateways, logger);
        try
        {
            mcpToolbox.DiscoverAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            logger.LogWarning("[DEGRADED] MCP 工具发现整体失败：{Error}", ex.Message);
        }

        foreach (var warning in mcpToolbox.DiscoveryWarnings)
        {
            logger.LogWarning("[DEGRADED] {Warning}", warning);
        }

        if (mcpToolbox.Definitions.Count > 0)
        {
            registry.Register(mcpToolbox);
            // 显式 Ask 规则（而非依赖"未知工具→Ask"兜底）：
            // MCP 工具进入设置页权限列表，用户可预先允许/拒绝/保持询问。
            foreach (var def in mcpToolbox.Definitions)
            {
                permission.SetRule(new ToolPermissionRule
                {
                    ToolName = def.Name,
                    DefaultDecision = PermissionDecision.Ask,
                    Notes = "MCP 外部进程工具：副作用任意，须征求授权",
                });
            }
        }
        else
        {
            // 一个工具都没发现：不注册，如实释放子进程资源。
            mcpToolbox.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
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
