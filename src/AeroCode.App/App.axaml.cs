using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AeroAgent.Autonomy.Analysis;
using AeroAgent.Autonomy.Clarification;
using AeroAgent.Autonomy.Data;
using AeroAgent.Autonomy.Experience;
using AeroAgent.Autonomy.Learning;
using AeroAgent.Autonomy.Llm;
using AeroAgent.Autonomy.Mission;
using AeroAgent.Autonomy.Retrospective;
using AeroAgent.Autonomy.Steelman;
using AeroAgent.Conversation.Data;
using AeroAgent.Conversation.Orchestration;
using AeroAgent.Conversation.Services;
using AeroAgent.Moa.Aggregation;
using AeroAgent.Moa.Assignment;
using AeroAgent.Moa.Gateway;
using AeroAgent.Moa.Planning;
using AeroAgent.Moa.Profiles;
using AeroAgent.Moa.Safety;
using AeroAgent.Moa.Strategies;
using AeroAgent.Moa.Subagent;
using AeroAgent.Moa.Tools;
using AeroAgent.Moa.Tools.Workspace;
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
using AeroCode.Harness.Agents;
using AeroCode.Harness.Compaction;
using AeroCode.Harness.Hooks;
using AeroCode.Harness.Permission;
using AeroCode.Harness.PlanMode;
using AeroCode.Harness.Scheduler;
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
        // B2 会话 fork 能力：SessionService 同时实现 ISessionFork（同一真实持久化实例）。
        sc.AddSingleton<ISessionFork>(sp => (ISessionFork)sp.GetRequiredService<ISessionService>());

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
        // B2 组合根：EventBus/Compactor 从 HarnessHost 取同一实例（规格 2.3），
        // 守卫链/熔断/钩子/调度/子代理/压缩全部共享一条事件面。
        sc.AddSingleton(harnessHost.EventBus);
        sc.AddSingleton(new Compactor(
            harnessHost.EventBus,
            CompactionStrategy.TruncateOldest,
            triggerThresholdPercent: 100, // 阈值语义 = 估算 token ≥ 设置阈值即触发
            keepRecentMessages: Math.Clamp(settings.Current.Compaction.KeepRecentMessages, 1, 200)));
        sc.AddSingleton(new CompactionGateOptions
        {
            // ≤0 = 关闭溢出检测（不压缩，行为与批次 A 一致）。
            ThresholdTokens = settings.Current.Compaction.ThresholdTokens,
        });

        // S7 授权链：持久化存储（permissions.json）+ 对话框代理（Ask → 真实授权窗口）。
        // 用户的持久化决策在 RegisterToolboxes 之后应用（用户决定优先于内建默认）。
        var permissionStore = new JsonPermissionStore(paths.PermissionsFile);
        sc.AddSingleton(permissionStore);

        // B2 智能审批（G3）：AdvisorModel 非空时用默认 provider 的小模型判定。
        // provider 解析失败 → advisor 不可用（审批行为与无 advisor 一致，如实记录）。
        IPermissionAdvisor? advisor = null;
        var advisorModel = settings.Current.Safety.AdvisorModel;
        if (!string.IsNullOrWhiteSpace(advisorModel))
        {
            try
            {
                advisor = new PermissionAdvisor(
                    providerFactory.Get(settings.Current.Ai.DefaultProviderId), advisorModel);
            }
            catch (Exception ex)
            {
                loggerFactory.CreateLogger("AeroCode.Safety").LogWarning(
                    "[DEGRADED] 审批建议器不可用（默认 provider 解析失败，审批行为不变）：{Error}", ex.Message);
            }
        }

        if (advisor is not null)
        {
            sc.AddSingleton(advisor);
            sc.AddSingleton<IPermissionAdvisor>(advisor);
        }

        var dialogBroker = new DialogPermissionBroker(
            harnessHost.Permission, permissionStore,
            new AvaloniaPermissionDialogPresenter(),
            loggerFactory.CreateLogger<DialogPermissionBroker>(),
            advisor,
            settings.Current.Safety.AutoApproveLowRisk);
        sc.AddSingleton(dialogBroker);

        // B2 审批熔断（G3）：会话内连续批准/累计成本任一超限 → 强制人工弹窗。
        // 成本通道由 ChatViewModel 在轮完成时用真实 usage 计费累计（RecordCost）。
        var approvalBreaker = new ApprovalCircuitBreaker(
            interactiveBroker: dialogBroker,
            autoAdoptBroker: null,
            eventBus: harnessHost.EventBus,
            sessionId: "app",
            maxConsecutiveApprovals: Math.Max(1, settings.Current.Safety.ApprovalBurstLimit),
            maxSessionCostUsd: settings.Current.Safety.ApprovalCostLimitUsd > 0
                ? settings.Current.Safety.ApprovalCostLimitUsd
                : 5.0);
        sc.AddSingleton(approvalBreaker);
        sc.AddSingleton<IPermissionBroker>(approvalBreaker);

        var toolboxRegistry = new ToolboxRegistry();

        // 3e. 工作区工具域接线（批次 A）：WorkspaceContext 解析失败时诚实降级——
        //     不注册 workspace/git/plan 工具域并记 WARN，绝不伪造根路径。
        //     检查点/大输出落盘都在 AppDataPaths 根下（尊重 Android 数据根覆盖）。
        //     注册与 ChatViewModel 可选参数（默认 null）配套：未注册即如实无工作区。
        WorkspaceContext? workspace = ResolveWorkspace(settings, paths, loggerFactory);
        var planWorkflow = workspace is null
            ? null
            : new PlanWorkflow(harnessHost.Permission, workspace.Root);
        sc.AddSingleton(new InstructionLoader(paths.RootDirectory, workspace?.Root));

        if (workspace is not null)
        {
            sc.AddSingleton(workspace);
            sc.AddSingleton(planWorkflow!);

            ICheckpointTracker? checkpoints = null;
            try
            {
                checkpoints = new CheckpointStore(Path.Combine(paths.RootDirectory, "checkpoints"));
            }
            catch (Exception ex)
            {
                loggerFactory.CreateLogger("AeroCode.Workspace").LogWarning(
                    "[DEGRADED] 检查点目录不可用，写类工具将不留检查点：{Error}", ex.Message);
            }

            toolboxRegistry.Register(new WorkspaceToolbox(
                workspace,
                new ShellRunner(
                    workspace.Root,
                    TimeSpan.FromSeconds(settings.Current.Workspace.ShellTimeoutSeconds)),
                checkpoints));
            toolboxRegistry.Register(new GitToolbox(new GitWorkflow(workspace.Root)));
            toolboxRegistry.Register(new PlanToolbox(planWorkflow!));
        }

        // B2 守卫链（规格 2.1，替换批次 A 的 GuardWorkspaceBoundary 直传）：
        // 工作区边界 → 命令结构分类 → doom-loop → 敏感文件（含 AeroCode 配置自保护）→
        // 可选急停哨兵。preCheck 只许更审慎（ToolRouter 保证 Allow 不越过策略 Deny/Ask）。
        // 无工作区时工作区边界守卫缺席（诚实降级，其余守卫照常生效）。
        var safetyLogger = loggerFactory.CreateLogger("AeroCode.Safety");
        var guards = new List<IToolGuard>();
        if (workspace is not null)
        {
            guards.Add(new WorkspaceBoundaryGuard(workspace));
        }

        guards.Add(new CommandClassifierGuard());
        guards.Add(new DoomLoopGuard(Math.Max(2, settings.Current.Safety.DoomLoopThreshold)));
        guards.Add(new SensitiveFileGuard(workspace, paths.RootDirectory));
        var estopFile = settings.Current.Safety.EstopFile;
        if (!string.IsNullOrWhiteSpace(estopFile))
        {
            guards.Add(new EstopGuard(estopFile, harnessHost.EventBus));
        }

        var guardChain = new ToolGuardChain(guards);
        safetyLogger.LogInformation(
            "守卫链装配完成：{Guards}（estop={Estop}）",
            string.Join(" → ", guards.Select(g => g.Name)),
            string.IsNullOrWhiteSpace(estopFile) ? "未启用" : estopFile);

        // 工具大结果落盘汇：按日期分目录，截断后的引用路径指回真实文件。
        var toolRouter = new ToolRouter(
            toolboxRegistry,
            harnessHost.Permission,
            approvalBreaker,
            (toolName, args) => guardChain.Check(toolName, args),
            new FileToolOutputSink(Path.Combine(paths.RootDirectory, "tool-outputs")));
        sc.AddSingleton(toolboxRegistry);
        sc.AddSingleton(toolRouter);

        // B2 Hook 引擎（规格 2.3）：hooks.json 缺失 = 空载（正常态，非降级）；
        // 坏配置 fail-safe 拒载（InvalidDataException 捕获后记 WARN，不崩溃）。
        var hookEngine = new HookEngine(harnessHost.EventBus, loggerFactory.CreateLogger("AeroCode.Hooks"));
        sc.AddSingleton(hookEngine);
        sc.AddSingleton<IHookEngine>(hookEngine);
        if (settings.Current.Hooks.Enabled)
        {
            var hooksPath = Path.Combine(paths.RootDirectory, "hooks.json");
            if (File.Exists(hooksPath))
            {
                try
                {
                    var loaded = hookEngine.LoadFrom(hooksPath);
                    loggerFactory.CreateLogger("AeroCode.Hooks").LogInformation(
                        "已加载 {Count} 条事件钩子（{Path}）", loaded, hooksPath);
                }
                catch (InvalidDataException ex)
                {
                    loggerFactory.CreateLogger("AeroCode.Hooks").LogWarning(
                        "[DEGRADED] hooks.json 配置拒载（fail-safe，保留空载）：{Error}", ex.Message);
                }
            }
            else
            {
                loggerFactory.CreateLogger("AeroCode.Hooks").LogInformation(
                    "hooks.json 不存在，钩子引擎空载（{Path}）", hooksPath);
            }
        }
        else
        {
            loggerFactory.CreateLogger("AeroCode.Hooks").LogInformation(
                "Hooks.Enabled=false，事件钩子未启用");
        }

        // B2 调度服务（规格 2.4）：jobs.json 持久化 + Timer 触发 + 急停哨兵联动。
        // Enabled=false 不启动轮询（注册保留，设置页仍可查看/编辑任务定义）。
        var scheduler = new SchedulerService(
            Path.Combine(paths.RootDirectory, "jobs.json"),
            string.IsNullOrWhiteSpace(estopFile) ? null : estopFile,
            harnessHost.EventBus,
            msg => loggerFactory.CreateLogger("AeroCode.Scheduler").LogInformation("{Message}", msg));
        scheduler.Load();
        if (settings.Current.Scheduler.Enabled)
        {
            scheduler.Start();
        }
        else
        {
            loggerFactory.CreateLogger("AeroCode.Scheduler").LogInformation(
                "Scheduler.Enabled=false，调度轮询未启动");
        }

        if (scheduler.LastLoadError is not null)
        {
            loggerFactory.CreateLogger("AeroCode.Scheduler").LogWarning(
                "[DEGRADED] jobs.json 拒载（fail-safe 空载）：{Error}", scheduler.LastLoadError);
        }

        sc.AddSingleton(scheduler);

        // B2 子代理（规格 2.5）：ISubAgentLauncher 单例，独立会话 + 继承 ToolRouter
        // （同一策略/守卫/授权代理实例，权限显式继承）。设置节映射进 SubagentOptions。
        var subagentOptions = new SubagentOptions
        {
            Enabled = settings.Current.Subagent.Enabled,
            MaxDepth = Math.Clamp(settings.Current.Subagent.MaxDepth, 1, SubAgentSpec.MaxDepth),
            MaxParallel = Math.Max(1, settings.Current.Subagent.MaxParallel),
        };
        sc.AddSingleton<ISubAgentLauncher>(sp => new SubAgentRunner(
            sp.GetRequiredService<ISessionService>(),
            providerFactory,
            profileCatalog,
            harnessHost.EventBus,
            subagentOptions,
            toolRouter,
            loggerFactory.CreateLogger<SubAgentRunner>()));

        // B2 会话级组件：Steer 插话队列（G3 消费点在 ChatOrchestrationFacade）+ Todo 持久化
        //（短生命周期 DbContext 工厂——与 SessionService 的互斥锁模型解并发竞争）。
        sc.AddSingleton(new SteerQueue());
        sc.AddSingleton<ITodoStore>(new TodoStore(() => new ConversationDbContext(convOptions)));

        // AutoApproveEdits：启动即 AcceptEdits 档（文件编辑免逐次确认；
        // shell 与网络仍走原规则）。持久化授权面只覆盖逐工具决策，不回写档位。
        if (settings.Current.Workspace.AutoApproveEdits)
        {
            harnessHost.Permission.CurrentMode = PermissionMode.AcceptEdits;
        }

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
        // B2 G2-2 专家团策略：真实调 moa-gateway-pro（MOA_GATEWAY_URL/MOA_GATEWAY_KEY
        // 环境变量约定，与官方 CLI 一致）；网关不可达时诚实失败（不静默回退）。
        sc.AddSingleton(new MoaGatewayClient(MoaGatewayClientOptions.FromEnvironment()));
        sc.AddSingleton<IOrchestrationStrategy, ExpertsStrategy>();
        sc.AddSingleton<IChatOrchestrationFacade, ChatOrchestrationFacade>();
        sc.AddSingleton<ChatViewModel>();

        // ---- B2 G2-1 Mission 控制器接线（内核零改造，只装配其既有依赖）----
        var autonomyRoot = Path.Combine(paths.RootDirectory, "autonomy");
        var autonomyPaths = new AutonomyDataPaths(autonomyRoot);
        autonomyPaths.EnsureDirectories();
        var autonomyDb = new AutonomyDbContext(new DbContextOptionsBuilder<AutonomyDbContext>()
            .UseSqlite($"Data Source={autonomyPaths.DatabaseFile}")
            .Options);
        var missionStore = new MissionStore(autonomyDb);
        var autonomyLlm = new AutonomyLlmClient(providerFactory);
        var clarificationGate = new ClarificationGate(autonomyLlm);
        sc.AddSingleton(missionStore);
        sc.AddSingleton(sp => new MoaMissionExecutor(
            sp.GetRequiredService<ISessionService>(),
            sp.GetRequiredService<IChatOrchestrationFacade>()));
        sc.AddSingleton<IMissionExecutor>(sp => sp.GetRequiredService<MoaMissionExecutor>());
        sc.AddSingleton(sp => new MissionController(
            new TaskAnalyzer(autonomyLlm),
            new StrategySelector(),
            clarificationGate,
            new SteelmanProtocol(autonomyLlm),
            missionStore,
            sp.GetRequiredService<IMissionExecutor>(),
            new RetrospectiveEngine(),
            new ExperienceInjector(missionStore),
            autonomyLlm,
            autonomyPaths,
            loggerFactory.CreateLogger<MissionController>()));
        sc.AddSingleton<MissionViewModel>();

        // ---- B2 G2-3 会话记忆：学习库（四型沉淀的真实存储）+ 召回/沉淀服务 ----
        var learningPaths = new LearningDataPaths(Path.Combine(paths.RootDirectory, "learning"));
        var experienceStore = new ExperienceStore(
            LearningDbContext.Create(learningPaths), learningPaths);
        sc.AddSingleton(experienceStore);
        sc.AddSingleton<IClarificationPresenter>(new AvaloniaClarificationPresenter());
        sc.AddSingleton<IClarificationPort>(new ClarificationGatePort(clarificationGate));
        sc.AddSingleton(sp => new SessionMemoryService(
            paths,
            settings.Current.Memory,
            sp.GetRequiredService<INoteService>(),
            providerFactory,
            embeddingClient,
            experienceStore,
            settings.Current.Ai.DefaultProviderId,
            loggerFactory.CreateLogger<SessionMemoryService>()));

        // ---- B2 声明式 agent 定义（agents/*.md）：目录存在才加载；结果注册为单例 ----
        //     （最小接线：注册 + 日志条数；画像映射的完整消费面按规格留待后续批次。）
        var agentsRoot = Path.Combine(paths.RootDirectory, "agents");
        if (Directory.Exists(agentsRoot))
        {
            try
            {
                var agentResult = new AgentDefinitionLoader(w =>
                        loggerFactory.CreateLogger("AeroCode.Agents").LogWarning("[DEGRADED] {Warning}", w))
                    .LoadFromDirectory(agentsRoot);
                sc.AddSingleton(agentResult);
                loggerFactory.CreateLogger("AeroCode.Agents").LogInformation(
                    "已加载 {Count} 个声明式 agent 定义（{Warnings} 条警告，目录 {Dir}）",
                    agentResult.Agents.Count, agentResult.Warnings.Count, agentsRoot);
            }
            catch (DirectoryNotFoundException ex)
            {
                loggerFactory.CreateLogger("AeroCode.Agents").LogWarning(
                    "[DEGRADED] agents 目录不可读，声明式 agent 未加载：{Error}", ex.Message);
            }
        }
        else
        {
            loggerFactory.CreateLogger("AeroCode.Agents").LogInformation(
                "agents 目录不存在，跳过声明式 agent 加载（{Dir}）", agentsRoot);
        }

        // ---- B2 G4 WindowsJobSandbox：ShellRunner 构造无沙箱参数（builder 所有权约束，
        //      不碰其文件）→ 无法在不改动 ShellRunner 的前提下真实挂接。诚实处置：
        //      注册单例工厂（惰性，解析即真实创建 Job Object）+ 启动日志记录待挂接状态，
        //      绝不伪造挂接。详见 batchB_delta_report.md。
        sc.AddSingleton(_ => new WindowsJobSandbox(
            processMemoryLimitBytes: 1L << 30,
            maxActiveProcesses: 32));
        loggerFactory.CreateLogger("AeroCode.Sandbox").LogWarning(
            "[DEGRADED] WindowsJobSandbox 已注册但未挂接：ShellRunner 构造签名无沙箱参数，" +
            "run_shell 仍走 ShellRunner 原生路径（不伪造挂接）");

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
    /// 解析/创建工作区根：settings.workspace.root 为空时用 Documents/AeroCode-workspace
    /// （首次惰性创建）。目录无法创建时诚实降级返回 null——组合根不注册 workspace/git
    /// 工具域并记 WARN，绝不伪造根路径继续运行。
    /// </summary>
    private static WorkspaceContext? ResolveWorkspace(
        SettingsService settings, AppDataPaths paths, ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("AeroCode.Workspace");
        var configured = settings.Current.Workspace.Root;
        var root = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "AeroCode-workspace")
            : configured;
        try
        {
            Directory.CreateDirectory(root);
            return new WorkspaceContext(root);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                "[DEGRADED] 工作区根 '{Root}' 不可用（{Error}）——workspace/git/plan 工具域未注册",
                root, ex.Message);
            return null;
        }
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
    /// 权限默认裁决：笔记工具 = 用户在笔记 UI 本来就能做的操作 → Allow
    /// （delete_note 的硬删除不可逆 → Override 升级为 Ask）；
    /// 技能工具能分发任意技能（浏览器/进程/文件操作）→ 默认 Ask；
    /// MCP 工具来自外部进程、副作用任意 → 保持 Ask。
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

        // ---- B2 工具域注册（规格 2.6）：与 workspace 可用性无关的域，全平台注册 ----
        // todo_*：会话级任务清单（TodoStore 真实读写 SQLite；当前会话经 ChatViewModel 访问器解析）。
        registry.Register(new TodoToolbox(
            services.GetRequiredService<ITodoStore>(),
            () => services.GetRequiredService<ChatViewModel>().SelectedSession?.Id ?? string.Empty));

        // web_search / web_fetch：真实检索栈（SearchService 默认后端）+ 真实 HTTP。
        registry.Register(new WebToolbox(logger: loggerFactory.CreateLogger<WebToolbox>()));

        // question：结构化澄清——评估走真实 ClarificationGate（端口适配），弹窗走真实 UI。
        // 澄清工具本身无副作用（只向用户发问），Allow；弹窗未回应时工具诚实失败。
        var clarifyToolbox = new ClarifyToolbox(
            services.GetRequiredService<IClarificationPort>(),
            services.GetRequiredService<IClarificationPresenter>(),
            logger: loggerFactory.CreateLogger<ClarifyToolbox>());
        registry.Register(clarifyToolbox);
        permission.SetRule(new ToolPermissionRule
        {
            ToolName = "question",
            DefaultDecision = PermissionDecision.Allow,
            Notes = "结构化澄清：向用户弹窗提问，无副作用",
        });

        foreach (var def in clarifyToolbox.Definitions.Where(d => d.Name != "question"))
        {
            permission.SetDefaultDecision(def.Name, PermissionDecision.Ask);
        }

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

        // 技能工具默认 Ask（而非 Allow）：run_skill 能分发任意技能——包括启动
        // Chromium、克隆仓库、读写文件等重副作用操作—— blanket Allow 等于让模型
        // 无确认直通整条技能链。Ask 规则同样进入设置页权限列表，用户可预先允许。
        // 持久化决策在本方法之后应用（ApplyPersistedPermissions），用户记住的选择优先。
        foreach (var def in skillToolbox.Definitions)
        {
            permission.SetDefaultDecision(def.Name, PermissionDecision.Ask);
        }

        // single-view 生命周期（Android）：MCP 的 stdio 传输依赖启动桌面式子进程，
        // Android 上不可用；且 DiscoverAsync().GetAwaiter().GetResult() 阻塞启动线程
        // 会造成 ANR。→ 跳过 MCP 工具箱注册，内建笔记/技能工具箱不受影响，
        // 有启用配置时如实降级记录（绝不静默）。
        if (Application.Current?.ApplicationLifetime is ISingleViewApplicationLifetime)
        {
            if (settings.Current.McpServers.Any(c => c.Enabled))
            {
                logger.LogWarning(
                    "[DEGRADED] Android（single-view）平台不支持 stdio 子进程 MCP 传输，已跳过 {Count} 个 MCP 服务器注册",
                    settings.Current.McpServers.Count(c => c.Enabled));
            }
            return;
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
