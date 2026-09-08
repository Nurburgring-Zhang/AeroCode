using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
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
using AeroAgent.Moa.Budget;
using AeroAgent.Moa.Curation;
using AeroAgent.Moa.Gateway;
using AeroAgent.Moa.Guard;
using AeroAgent.Moa.LoopGuard;
using AeroAgent.Moa.Planning;
using AeroAgent.Moa.Profiles;
using AeroAgent.Moa.Safety;
using AeroAgent.Moa.Strategies;
using AeroAgent.Moa.Subagent;
using AeroAgent.Moa.Tools;
using AeroAgent.Moa.Tools.Workspace;
using AeroAgent.Moa.Verify;
using AeroCode.AI.Capabilities;
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
using AeroCode.Harness.Curation;
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

        // R3 修复（F-MED-1）：损坏 JSON 拒载不再静默——JsonException 被 SettingsService 内部
        // 吞掉（fail-safe 降级），拒载事实经 LastLoadError 暴露。只记「拒载事实 + 备份文件名」，
        // 不记异常消息（JsonException 消息可能回显 JSON 内容，直接落日志有泄敏风险）。
        if (settings.LastLoadError is not null)
        {
            LogToFile("WARN",
                "Settings JSON rejected as corrupt; running with defaults. Backup: "
                + (settings.LastCorruptBackupPath ?? "(backup failed, original file kept in place)"));
        }

        sc.AddSingleton(settings);

        // 1b. Apply theme (before any view is rendered)
        var themeService = new ThemeService();
        themeService.Apply(settings.Current.Ui.Theme);
        sc.AddSingleton(themeService);

        // 2. Build AI options + ProviderFactory (singleton)
        var aiOptions = settings.ToAiOptions();
        var loggerFactory = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));

        // R3 修复（F-MED-1）：settings.json 拒载事实显式 WARN（与 moaoptions :238-243 / scheduler
        // 的可观测性口径对齐）。只记「拒载 + 备份文件名」级别信息——JsonException 消息可能回显
        // JSON 内容，绝不打原始异常消息全文；WARN 文本再过一遍 SensitiveTextScrubber 兜底。
        if (settings.LastLoadError is not null)
        {
            var backupNote = settings.LastCorruptBackupPath is null
                ? "损坏文件未能备份（原文件保留）"
                : $"损坏文件已备份为 {settings.LastCorruptBackupPath}";
            loggerFactory.CreateLogger("AeroCode.Settings").LogWarning(
                "[DEGRADED] {Detail}",
                SensitiveTextScrubber.Scrub($"settings.json 解析失败已拒载，回退默认设置；{backupNote}"));
        }
        // R3 缝合（δ Sδ2）：能力探测提前于 ProviderFactory 构造（构造零网络，ProbeAsync 才外呼），
        // 以便喂入工厂——OpenAIProvider 的 O 家族 xhigh 档由此受 probe 门控（未接线时休眠透传）。
        // 下方 B3 节注册进容器的是同一实例（R2 既有注册点保持）。
        var capabilityProbe = new VendorCapabilityProbe(
            id => aiOptions.Providers.FirstOrDefault(p =>
                string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)),
            logger: loggerFactory.CreateLogger("AeroCode.Capabilities"));
        var providerFactory = new ProviderFactory(aiOptions, loggerFactory, capabilityProbe: capabilityProbe);
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
        // R2 修复 MED-3：moaoptions.json 非法时 store 会静默回退默认编排选项（预算上限、角色绑定等
        // 全部丢失）。此处对拒载事实显式 WARN（可观测，不静默）；预算上限抢救由 store 侧 TrySalvageBudgetCap 尽力保留。
        if (moaOptionsStore.LastLoadError is not null)
        {
            loggerFactory.CreateLogger("AeroCode.Moa").LogWarning(
                "[DEGRADED] moaoptions.json 解析失败（编排选项回退默认，预算上限已尽力抢救保留）：{Error}",
                moaOptionsStore.LastLoadError);
        }
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
                    providerFactory.Get(settings.Current.Ai.DefaultProviderId), advisorModel,
                    // R3 修复（F-MED-2）：advisor 请求内容脱敏改由开关受控——默认 false =
                    // R3 前行为（原样序列化进 prompt）；true = 脱敏预览（敏感原文不外送判定模型）。
                    sanitizeArgs: settings.Current.Safety.AdvisorSanitizeArgs);
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

        // R3 修复（S-MED-2）：breaker 在 broker 之后构造，但 broker 的强制人工信号要在
        // 熔断后生效——先声明局部变量，闭包捕获（ResolveAsync 真正执行时 breaker 必已赋值）。
        ApprovalCircuitBreaker? approvalBreaker = null;
        var dialogBroker = new DialogPermissionBroker(
            harnessHost.Permission, permissionStore,
            new AvaloniaPermissionDialogPresenter(),
            loggerFactory.CreateLogger<DialogPermissionBroker>(),
            advisor,
            settings.Current.Safety.AutoApproveLowRisk,
            // R3 缝合（γ S-3/S-4）：自动采纳收紧（默认 false = 现行为逐字节一致）+
            // 被脱敏参数白名单（默认空 = 被脱敏即转人工）。
            tightenAutoAdopt: settings.Current.Safety.AutoAdoptTighten,
            autoAdoptModifiedArgsWhitelist: settings.Current.Safety.AutoAdoptWhitelist,
            // R3 修复（S-MED-2）：熔断后强制人工——信号为真时 advisor risk=low 也直接弹窗，
            // 连续批准/成本熔断对自动放行通道真正生效。
            forceInteractive: () => approvalBreaker?.IsBroken == true);
        sc.AddSingleton(dialogBroker);

        // B2 审批熔断（G3）：会话内连续批准/累计成本任一超限 → 强制人工弹窗。
        // 成本通道由 ChatViewModel 在轮完成时用真实 usage 计费累计（RecordCost）。
        approvalBreaker = new ApprovalCircuitBreaker(
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
        // R1 缝合（#13）：checkpoint 存储单一实例——工具箱留痕与 LoopGuard 恢复 /
        // MissionController 续跑共用。只有 loopGuard 启用时才注入 WorkerRunner/MissionController
        //（默认关闭 = 基线不注入，行为不变）。
        CheckpointStore? checkpointStore = null;
        var planWorkflow = workspace is null
            ? null
            : new PlanWorkflow(harnessHost.Permission, workspace.Root);
        sc.AddSingleton(new InstructionLoader(paths.RootDirectory, workspace?.Root));

        if (workspace is not null)
        {
            sc.AddSingleton(workspace);
            sc.AddSingleton(planWorkflow!);

            try
            {
                checkpointStore = new CheckpointStore(Path.Combine(paths.RootDirectory, "checkpoints"));
            }
            catch (Exception ex)
            {
                loggerFactory.CreateLogger("AeroCode.Workspace").LogWarning(
                    "[DEGRADED] 检查点目录不可用，写类工具将不留检查点：{Error}", ex.Message);
            }

            if (checkpointStore is not null)
            {
                // R3 缝合（β S1）：同一实例注册进容器——MissionViewModel 的可选构造参数
                // checkpointStore 由此解析到与 MissionController 恢复路径同源的实例，
                // 恢复按钮可用性探针（ResumePlanner fail-closed）与控制器恢复完全同源。
                sc.AddSingleton(checkpointStore);
            }

            // R3 缝合（γ S-1/S-2）：run_shell 沙箱门控注入点。Enforce 映射 settings.sandbox.enforce
            //（默认 false = 现行为逐字节一致：ShellRunner 不建沙箱直跑）；Enforce=true 时由
            // ShellRunner 逐次创建/释放 Job Object，fail-closed 拒绝事件经 Audit 委托记 WARN 审计。
            // R4 α：同一配置共享给 GitWorkflow（git 族沙箱面），enforce 时 git 子命令同口径受控。
            var sandboxAuditLogger = loggerFactory.CreateLogger("AeroCode.Sandbox");
            var shellSandboxOptions = new ShellSandboxOptions
            {
                Enforce = settings.Current.Sandbox.Enforce,
                Audit = msg => sandboxAuditLogger.LogWarning("[sandbox-audit] {Message}", msg),
            };
            toolboxRegistry.Register(new WorkspaceToolbox(
                workspace,
                new ShellRunner(
                    workspace.Root,
                    TimeSpan.FromSeconds(settings.Current.Workspace.ShellTimeoutSeconds)),
                checkpointStore,
                shellSandboxOptions));
            if (settings.Current.Sandbox.Enforce)
            {
                sandboxAuditLogger.LogInformation(
                    "sandbox.enforce=true：run_shell 与 git 工作流走 Job Object 沙箱（fail-closed，非 Windows/创建失败/圈入失败一律拒绝直跑）");
            }
            toolboxRegistry.Register(new GitToolbox(new GitWorkflow(workspace.Root, shellSandboxOptions)));
            // R4 γ-2 热重载接线：sandbox.enforce 变更运行时生效（不重启）——ShellRunner/GitWorkflow
            // 逐次调用读取同一 ShellSandboxOptions 实例的当前 Enforce 值（S-LOW-6 快照语义修复）。
            // 其余开关（budget/loopGuard/curation/deprecation）热重载消费延后 R5（如实记录）。
            settings.SettingsChanged += (_, _) =>
            {
                var newValue = settings.Current.Sandbox.Enforce;
                if (shellSandboxOptions.Enforce != newValue)
                {
                    shellSandboxOptions.Enforce = newValue;
                    sandboxAuditLogger.LogInformation(
                        "[settings-hotreload] sandbox.enforce → {Value}（运行时生效，下一次 run_shell/git 命令起适用）",
                        newValue);
                }
            };
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
        // R1 缝合（#13，C-GATE）：parallelEnabled 显式下传（默认 true = 现行为，翻转只经设置层）；
        // mission 级 token 闸门按设置装配（未启用 = GetService 返回 null = 基线行为）。
        var subagentOptions = new SubagentOptions
        {
            Enabled = settings.Current.Subagent.Enabled,
            MaxDepth = Math.Clamp(settings.Current.Subagent.MaxDepth, 1, SubAgentSpec.MaxDepth),
            MaxParallel = Math.Max(1, settings.Current.Subagent.MaxParallel),
            ParallelEnabled = settings.Current.Subagent.ParallelEnabled,
        };

        // ---- R1 缝合窗口（#13）：C-GATE / C-LOOP 接线。两能力默认全关：
        //      不注册实例 → WorkerRunner/SubAgentRunner 的可选构造参数保持 null，
        //      行为与基线完全一致；翻转只能经 settings.json 显式配置发生。----
        var budgetLogger = loggerFactory.CreateLogger("AeroCode.Budget");
        if (settings.Current.Budget.Enabled)
        {
            if (settings.Current.Budget.LimitTokens > 0)
            {
                var warningRatio = settings.Current.Budget.WarningRatio;
                if (double.IsNaN(warningRatio) || warningRatio is <= 0 or > 1)
                {
                    budgetLogger.LogWarning(
                        "[DEGRADED] budget.warningRatio={Value} 非法（须在 (0,1]），回退默认 0.8",
                        warningRatio);
                    warningRatio = 0.8;
                }

                var budgetGate = new TokenBudgetGate(settings.Current.Budget.LimitTokens, warningRatio);
                budgetGate.BudgetExhausted += snap => budgetLogger.LogWarning(
                    "token 预算耗尽（{Spent}/{Limit}）：mission 降级单 agent，在飞并行子代理将被取消",
                    snap.SpentTokens, snap.LimitTokens);
                sc.AddSingleton<ITokenBudgetGate>(budgetGate);
                budgetLogger.LogInformation(
                    "mission 级 token 预算闸门已启用（limit={Limit} tokens, warning={Ratio:P0}）",
                    settings.Current.Budget.LimitTokens, warningRatio);
            }
            else
            {
                budgetLogger.LogWarning(
                    "[DEGRADED] budget.enabled=true 但 limitTokens 非正，闸门未启用（行为与关闭一致）");
            }
        }
        else
        {
            budgetLogger.LogInformation("Budget.Enabled=false，mission 级 token 预算闸门未启用（默认行为）");
        }

        HumanPauseEscalationPolicy? escalationPolicy = null;
        if (settings.Current.LoopGuard.Enabled)
        {
            var loopGuardLogger = loggerFactory.CreateLogger("AeroCode.LoopGuard");
            sc.AddSingleton(new LoopGuardOptions
            {
                GoalAnchoringEnabled = settings.Current.LoopGuard.GoalAnchoringEnabled,
                MaxStrikes = Math.Max(1, settings.Current.LoopGuard.MaxStrikes),
                CheckpointSummaryChars = Math.Max(40, settings.Current.LoopGuard.CheckpointSummaryChars),
            });
            sc.AddSingleton<IDeviationDetector>(new DefaultKeywordDetector(
                settings.Current.LoopGuard.OffGoalKeywords is { Count: > 0 }
                    ? settings.Current.LoopGuard.OffGoalKeywords
                    : null));
            escalationPolicy = new HumanPauseEscalationPolicy(harnessHost.EventBus);
            sc.AddSingleton<IEscalationPolicy>(escalationPolicy);
            if (checkpointStore is null)
            {
                loopGuardLogger.LogWarning(
                    "[DEGRADED] LoopGuard.Enabled=true 但 checkpoint 存储不可用（无工作区），" +
                    "偏离升级时不做 checkpoint 恢复（如实标注，链路不中断）");
            }
            // checkpointStore 非空时：LoopGuard 连续偏离的 checkpoint 恢复与锚定段摘要消费真实检查点
            //（同一实例，容器注册已在 checkpointStore 创建处统一完成——R3 缝合 β S1）。

            loopGuardLogger.LogInformation(
                "LoopGuard 已启用（锚定={Anchor}, maxStrikes={Strikes}）；升级凭据由 MissionController 受理供人审批",
                settings.Current.LoopGuard.GoalAnchoringEnabled,
                Math.Max(1, settings.Current.LoopGuard.MaxStrikes));
        }
        else
        {
            loggerFactory.CreateLogger("AeroCode.LoopGuard").LogInformation(
                "LoopGuard.Enabled=false，目标锚定与偏离升级未启用（默认行为）");
        }

        // ---- R2 缝合窗口（#20）：B3 能力探测 / B6 版本 pin·弃用监控 / B5 guardrail /
        //      C2 critique 验证器 / B1 成本排序选项。默认值全部 = 现行为：
        //      探测构造零网络（ProbeAsync 才外呼）；弃用监控默认关（绝不外呼）；
        //      guardrail 默认 MarkOnly；critique 默认不注入（null = 基线）。
        //      翻转只经 settings.json 显式配置发生。----
        var capabilitiesLogger = loggerFactory.CreateLogger("AeroCode.Capabilities");

        // B3 能力探测：实例已在 ProviderFactory 之前提前构造（R3 缝合 δ Sδ2，工厂喂同一实例）；
        // 构造不发网络，ProbeAsync 时才外呼（fail-closed 三态）。
        sc.AddSingleton<IVendorCapabilityProbe>(capabilityProbe);

        // B6 版本 pin（ApiVersionHeaders）：无独立注入点——pin 数据随 aiOptions 进入
        // ProviderConfig，由 ClaudeProvider/OpenAICompatibleProvider 内部经 VendorVersionPin.From 消费；
        // 本波次已修 SettingsService 快照丢弃 pin 的真实断点（Copy() 此前不拷 ApiVersionHeaders）。

        // B6 弃用监控（R3 修复 HIGH-1/S-MED-5）：真实消费点 = ExpertsStrategy 的网关
        // execute 路径（生产可达）。外呼总闸语义：deprecation.enabled=false → 绝不外呼
        //（与 enabled 字段自身文档一致）；deprecation.monitor 是网关路径消费开关。
        // 双真才构造启用实例并注入 ExpertsStrategy——任一为 false → 不构造 → 执行路径
        // 零外呼零开销（策略侧另有 monitor null/IsEnabled=false 硬门兜底）。
        DeprecationMonitor? gatewayDeprecationMonitor = null;
        if (settings.Current.Deprecation.Enabled && settings.Current.Deprecation.Monitor)
        {
            gatewayDeprecationMonitor = new DeprecationMonitor(
                enabled: true,
                settings.Current.Deprecation.UrlAllowlist,
                logger: capabilitiesLogger);
            capabilitiesLogger.LogInformation(
                "弃用监控已启用（enabled && monitor 双真；白名单 {Count} 条；空白名单 = 即使启用也不外呼）",
                settings.Current.Deprecation.UrlAllowlist.Count);
        }
        else
        {
            capabilitiesLogger.LogInformation(
                "Deprecation.Enabled={Enabled}, Deprecation.Monitor={Monitor}：双真才外呼，当前不外呼（默认行为）",
                settings.Current.Deprecation.Enabled, settings.Current.Deprecation.Monitor);
        }

        // B5 guardrail：无条件装配（默认 MarkOnly → 只标记不拦截，权限裁决行为与基线一致）；
        // Enforce 只经 settings 翻转。R3 缝合（γ S-5）起工具段装配独立验证器
        // ToolCallGuardrailValidator（敏感形态 + 不可逆破坏命令 finding）；MarkOnly 语义不变
        //（finding 只标记，是否升级为拦截仍只经 guardrail.mode=Enforce 显式翻转）。
        var guardrailLogger = loggerFactory.CreateLogger("AeroCode.Guardrail");
        var guardrailModeRaw = settings.Current.Guardrail.Mode?.Trim() ?? string.Empty;
        if (!string.Equals(guardrailModeRaw, "MarkOnly", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(guardrailModeRaw, "Enforce", StringComparison.OrdinalIgnoreCase))
        {
            guardrailLogger.LogWarning(
                "[DEGRADED] guardrail.mode={Mode} 非法（MarkOnly|Enforce），回退 MarkOnly",
                settings.Current.Guardrail.Mode);
            guardrailModeRaw = "MarkOnly";
        }

        var guardrailPipeline = new GuardrailPipeline(
            new GuardrailOptions
            {
                Mode = string.Equals(guardrailModeRaw, "Enforce", StringComparison.OrdinalIgnoreCase)
                    ? GuardrailMode.Enforce
                    : GuardrailMode.MarkOnly,
            },
            loggerFactory.CreateLogger<GuardrailPipeline>());
        guardrailPipeline.AddValidator(GuardrailStage.Input, new FactAssertionValidator());
        guardrailPipeline.AddValidator(GuardrailStage.ToolCall, new ToolCallGuardrailValidator());
        harnessHost.Permission.GuardrailAdvisor = new GuardrailPermissionAdvisor(
            guardrailPipeline,
            loggerFactory.CreateLogger("AeroCode.Guardrail"));
        guardrailLogger.LogInformation(
            "guardrail 已装配（mode={Mode}）：输入段 FactAssertion + 工具段 tool-call（敏感形态/不可逆破坏命令 finding）；" +
            "MarkOnly 默认只标记不拦截，Enforce 翻转只经 settings",
            guardrailPipeline.Mode);
        sc.AddSingleton(guardrailPipeline);

        // C2 critique 校验循环（可选，默认关 = 基线）：启用且默认 provider 可用时装配 LLM 验证器；
        // SubAgentRunner 工厂经 GetService 惰性解析（未注册 = null = 现行为）。
        if (settings.Current.Critique.Enabled)
        {
            var critiqueLlm = new AutonomyLlmClient(providerFactory);
            if (critiqueLlm.IsAvailable)
            {
                sc.AddSingleton<ICompletionVerifier>(new LlmCompletionVerifier(
                    critiqueLlm, loggerFactory.CreateLogger("AeroCode.Critique")));
                sc.AddSingleton(new CritiqueLoopOptions
                {
                    MaxCritiqueRounds = Math.Clamp(settings.Current.Critique.MaxCritiqueRounds, 1, 2),
                });
                loggerFactory.CreateLogger("AeroCode.Critique").LogInformation(
                    "critique 校验循环已启用（≤{Rounds} 轮，判定不信自报）",
                    Math.Clamp(settings.Current.Critique.MaxCritiqueRounds, 1, 2));
            }
            else
            {
                loggerFactory.CreateLogger("AeroCode.Critique").LogWarning(
                    "[DEGRADED] critique.enabled=true 但无可用 provider，完成判定未启用（行为与关闭一致）");
            }
        }
        else
        {
            loggerFactory.CreateLogger("AeroCode.Critique").LogInformation(
                "Critique.Enabled=false，完成判定未启用（默认行为）");
        }

        // B1 成本排序选项（可配置数据，DI 载体）：B1 四层判定为纯函数。R2 修复 HIGH-1 起，
        // settings costTiers.enabled=true 时经 ApplyR2Wave 把本选项注入 DecomposeStrategy 的
        // 真实 worker 选模点（ModelAssigner.Decide 四层判定）；默认 false = 既有 Assign 打分路径
        //（现行为，请求形态不变）。进入生产请求面的开关另有
        // effort.enabled（→ 峰值档裁决）与 costTiers.cacheBreakpointsEnabled（→ 缓存断点），
        // 均由 ApplyR2Wave 装配。
        sc.AddSingleton(new CostTierOptions
        {
            LongContextTokenThreshold = settings.Current.CostTiers.LongContextTokenThreshold ?? 100_000,
            BatchDiscountMultiplier = settings.Current.CostTiers.BatchDiscountMultiplier ?? 0.5,
            PeakPremiumMultiplier = settings.Current.CostTiers.PeakPremiumMultiplier ?? 1.0,
        });

        sc.AddSingleton<ISubAgentLauncher>(sp => new SubAgentRunner(
            sp.GetRequiredService<ISessionService>(),
            providerFactory,
            profileCatalog,
            harnessHost.EventBus,
            subagentOptions,
            toolRouter,
            loggerFactory.CreateLogger<SubAgentRunner>(),
            sp.GetService<ITokenBudgetGate>(),
            sp.GetService<ICompletionVerifier>(),    // R2 C2：critique 启用才有实现（null = 基线）
            sp.GetService<CritiqueLoopOptions>()));  // R2 C2：轮数上限（null = 默认 ≤2 轮 + AcceptWithFindings）

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
        // R3 修复（HIGH-1/S-MED-5）：专家团策略是生产可达的网关执行路径——MarkOnly
        // 弃用监控经此注入（双真门控的 monitor 实例在 B6 节构造；未启用 = null = 零开销）。
        sc.AddSingleton<IOrchestrationStrategy>(sp => new ExpertsStrategy(
            sp.GetRequiredService<MoaGatewayClient>(),
            sp.GetRequiredService<ISessionService>(),
            loggerFactory.CreateLogger<ExpertsStrategy>(),
            gatewayDeprecationMonitor));
        sc.AddSingleton<IChatOrchestrationFacade, ChatOrchestrationFacade>();
        // 注：GatewayOrchestrationFacade 不再注册进容器（R3 修复 HIGH-1）——生产聊天走
        // ChatOrchestrationFacade/ExpertsStrategy，门面此前「注册无人消费」；其 MarkOnly
        // 弃用检查能力保留为库内可测试组件（测试覆盖），消费点移至 ExpertsStrategy。

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
            loggerFactory.CreateLogger<MissionController>(),
            escalationPolicy,   // R1 C-LOOP：升级凭据受理订阅（null = 不订阅，基线行为）
            checkpointStore));  // R1 C-RESUME：恢复路径（null = 恢复不可用，诚实降级）
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

        // ---- B2 G4 WindowsJobSandbox 退役（R3 缝合 γ S-6）：批次 B 曾在此注册单例工厂并
        //      标「[DEGRADED] 已注册但未挂接」。批次 C 起沙箱生命周期由 ShellRunner 逐次管理
        //      （ShellSandboxOptions.Enforce=true 时按次创建/Dispose，fail-closed），
        //      该单例不再是挂接路径，移除以免双源歧义。----

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
        ApplyContextCuration(serviceProvider, settings, providerFactory, loggerFactory);
        ApplyR2Wave(serviceProvider, settings, loggerFactory);
        RegisterToolboxes(serviceProvider, settings, loggerFactory);
        ApplyPersistedPermissions(serviceProvider, loggerFactory);
        return serviceProvider;
    }

    /// <summary>
    /// R1 缝合（#13，C-CURATE）：启用策展设置时构造真实 <see cref="ContextCurator"/> 并绑定到
    /// WorkerRunner 单例的 Curator 注入点（β 契约的唯一 settable 注入点；null = 不策展 = 基线）。
    /// 可选 LLM 摘要：useLlmSummary 且默认 provider 可用 → ContextCuratorLlmAdapter 桥接
    /// （输出统一敏感形态过滤 + 长度封顶）；不可用 → 确定性模板降级并记 WARN，绝不阻塞启动。
    /// </summary>
    private static void ApplyContextCuration(
        ServiceProvider services,
        SettingsService settings,
        IProviderRegistry providerRegistry,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("AeroCode.Curation");
        var curation = settings.Current.Curation;
        if (!curation.Enabled)
        {
            logger.LogInformation("Curation.Enabled=false，上下文策展未启用（默认行为）");
            return;
        }

        CurationSummarizer? summarizer = null;
        if (curation.UseLlmSummary)
        {
            var summaryLlm = new AutonomyLlmClient(providerRegistry);
            if (summaryLlm.IsAvailable)
            {
                var adapter = new ContextCuratorLlmAdapter(
                    async (text, ct) =>
                    {
                        var completion = await summaryLlm.CompleteAsync(
                            "把对话历史压缩为不超过 200 字的进展摘要：保留关键事实、结论与未完成项，不要输出其他内容。",
                            text, temperature: 0.2, ct).ConfigureAwait(false);
                        return completion?.Content ?? string.Empty;
                    },
                    maxOutputChars: 400);
                // Func<string,Task<string>> 与 CurationSummarizer 是不同委托类型，需经 lambda 适配。
                summarizer = text => adapter.Summarize(text);
            }
            else
            {
                logger.LogWarning(
                    "[DEGRADED] curation.useLlmSummary=true 但无可用 provider，LLM 摘要降级为确定性模板（策展仍可用）");
            }
        }

        var curator = new ContextCurator(new CurationOptions
        {
            WatermarkThresholdTokens = curation.WatermarkThresholdTokens,
            KeepRecentMessages = Math.Max(1, curation.KeepRecentMessages),
            MaxStateBlockChars = Math.Max(64, curation.MaxStateBlockChars),
            Summarizer = summarizer,
        });
        services.GetRequiredService<WorkerRunner>().Curator = curator;
        logger.LogInformation(
            "上下文策展已启用并绑定 WorkerRunner（水位 {Threshold} tokens，保留最近 {Keep} 条，LLM 摘要={Llm}）",
            curation.WatermarkThresholdTokens, curation.KeepRecentMessages,
            summarizer is not null ? "启用" : "关闭（确定性模板）");
    }

    /// <summary>
    /// R2 缝合（#20，⑤⑥）：把进入生产请求面的 R2 开关绑定到 WorkerRunner 单例的 settable
    /// 注入点（Curator 同款模式；默认全关 = 请求与基线逐字节一致）。
    /// ⑤ effort：settings.effort.enabled → PeakEffortProfile（探测 fail-closed，Peak 命中才发射
    /// 厂商 token；未注入 probe / Standard 档不发射任何字段）。
    /// ⑥ 缓存断点：costTiers.cacheBreakpointsEnabled → 工具循环冻结前缀边界写入
    /// ChatRequest.CacheBreakpoints（0-based，断点 = 最后一条冻结 system 消息）。
    /// </summary>
    private static void ApplyR2Wave(
        ServiceProvider services,
        SettingsService settings,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("AeroCode.R2");
        var worker = services.GetRequiredService<WorkerRunner>();

        worker.CacheBreakpointsEnabled = settings.Current.CostTiers.CacheBreakpointsEnabled;
        if (settings.Current.CostTiers.CacheBreakpointsEnabled)
        {
            logger.LogInformation(
                "缓存断点传递已启用：工具循环冻结前缀边界 → ChatRequest.CacheBreakpoints（0-based）");
        }

        if (settings.Current.Effort.Enabled)
        {
            var probe = services.GetService<IVendorCapabilityProbe>();
            if (probe is not null)
            {
                worker.PeakEffortProfile = new EffortProfile(probe);
                worker.PeakEffortRequested = true;
                logger.LogInformation(
                    "effort 峰值档已启用：档位经能力探测 fail-closed 裁决（Standard 档不发射任何字段，请求与基线一致）");
            }
            else
            {
                logger.LogWarning(
                    "[DEGRADED] effort.enabled=true 但能力探测不可用，峰值档未装配（请求与基线一致）");
            }
        }
        else
        {
            logger.LogInformation(
                "Effort.Enabled=false，effort 峰值档未启用（默认行为，不发射任何字段）");
        }

        // ---- R2 修复 HIGH-1：settings costTiers.enabled=true 时把 B1 四层成本排序接入
        // DecomposeStrategy 的真实 worker 选模点（ModelAssigner.Decide）；默认 false = 不注入，
        // 选模走既有 Assign 打分路径（现行为）。costTiers.peakTierEnabled 作为④层真实输入传递
        //（④层实际可用性仍由 effort 探测裁决）。----
        if (settings.Current.CostTiers.Enabled)
        {
            var decompose = services.GetServices<IOrchestrationStrategy>()
                .OfType<DecomposeStrategy>()
                .FirstOrDefault();
            if (decompose is not null)
            {
                decompose.CostTierSelection = new CostTierSelectionOptions
                {
                    Options = services.GetRequiredService<CostTierOptions>(),
                    PeakTierEnabled = settings.Current.CostTiers.PeakTierEnabled,
                };
                logger.LogInformation(
                    "B1 成本排序已接入 DecomposeStrategy worker 选模点（四层判定，peakTierEnabled={PeakTier}）",
                    settings.Current.CostTiers.PeakTierEnabled);
            }
            else
            {
                logger.LogWarning(
                    "[DEGRADED] costTiers.enabled=true 但 DecomposeStrategy 不可用，worker 选模保持既有打分路径");
            }
        }

        // ---- R2 修复 MED-5：C2 critique 钩子接进 WorkerRunner（组合根只在 critique.enabled=true
        // 且验证器可用时注册 ICompletionVerifier/CritiqueLoopOptions——未注册 = null = 现行为）。
        // 工具循环最终答复经独立 critique（判定不信自报，≤2 轮有界重试）。----
        worker.CompletionVerifier = services.GetService<ICompletionVerifier>();
        worker.CritiqueOptions = services.GetService<CritiqueLoopOptions>();
        if (worker.CompletionVerifier is not null)
        {
            logger.LogInformation(
                "C2 critique 已接入 WorkerRunner（判定不信自报，有界重试）");
        }

        // ---- R2 修复 MED-4：B5 输入/输出段 guardrail 接进 WorkerRunner（注入才生效，null = 现行为）。
        // 发现仅记录 + WARN（MarkOnly 语义，不阻断、不改变流程走向）；工具段经
        // GuardrailPermissionAdvisor → PermissionPolicy 挂点（上方既有装配）。----
        worker.Guardrail = services.GetService<GuardrailPipeline>();
    }

    /// <summary>
    /// R2 缝合（#20，C2）：组合根内建的 LLM 完成判定验证器（γ 契约的默认实现；可选装配，
    /// critique.enabled=false = 不注册 = 现行为）。判定不信自报：任务目标、产出与独立佐证
    /// 一并交给默认 provider 裁决，产出须为 JSON {"accepted":bool,"reason":string,"feedback":string}；
    /// 无产出/解析失败 = Reject（诚实有界收敛：不冒充通过；重试轮真实用量由 CritiqueLoop
    /// 调用方逐轮核算）。文本过敏感形态过滤（安全硬门 #4）。
    /// </summary>
    private sealed class LlmCompletionVerifier : ICompletionVerifier
    {
        private const int MaxEvidenceItems = 5;
        private const int MaxFeedbackChars = 500;
        private readonly AutonomyLlmClient _llm;
        private readonly ILogger _logger;

        public LlmCompletionVerifier(AutonomyLlmClient llm, ILogger logger)
        {
            _llm = llm;
            _logger = logger;
        }

        public async ValueTask<CompletionVerdict> VerifyAsync(
            CompletionVerificationRequest request, CancellationToken cancellationToken)
        {
            var evidence = request.Evidence is { Count: > 0 }
                ? string.Join("\n---\n", request.Evidence.Take(MaxEvidenceItems))
                : "（无独立佐证）";
            var prompt =
                "判断下面的「产出」是否完成了「任务目标」。判定必须基于独立佐证，不信产出自报。" +
                "只输出 JSON：{\"accepted\":true|false,\"reason\":\"…\",\"feedback\":\"…\"}。" +
                $"\n[任务目标]\n{request.TaskGoal}" +
                $"\n[产出]\n{SensitiveTextScrubber.Scrub(request.Output)}" +
                $"\n[独立佐证]\n{SensitiveTextScrubber.Scrub(evidence)}";
            try
            {
                var completion = await _llm.CompleteAsync(
                    "你是严格的完成判定审查员，只输出 JSON。",
                    prompt,
                    temperature: 0.0,
                    cancellationToken).ConfigureAwait(false);
                var text = completion?.Content;
                if (string.IsNullOrWhiteSpace(text))
                {
                    return CompletionVerdict.Reject("验证器无产出（诚实拒绝，不冒充通过）");
                }

                var json = ExtractJsonObject(text);
                if (json is null)
                {
                    return CompletionVerdict.Reject(
                        "验证器输出无法解析为 JSON（诚实拒绝，不冒充通过）",
                        Truncate(SensitiveTextScrubber.Scrub(text)));
                }

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var accepted = root.TryGetProperty("accepted", out var a) && a.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.String => string.Equals(a.GetString(), "true", StringComparison.OrdinalIgnoreCase),
                    _ => false,
                };
                var reason = root.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String
                    ? SensitiveTextScrubber.Scrub(r.GetString() ?? string.Empty)
                    : null;
                if (accepted)
                {
                    return CompletionVerdict.Accept(string.IsNullOrWhiteSpace(reason) ? null : reason);
                }

                var feedback = root.TryGetProperty("feedback", out var f) && f.ValueKind == JsonValueKind.String
                    ? SensitiveTextScrubber.Scrub(f.GetString() ?? string.Empty)
                    : reason;
                return CompletionVerdict.Reject(
                    string.IsNullOrWhiteSpace(reason) ? "critique 未通过" : reason,
                    string.IsNullOrWhiteSpace(feedback) ? null : feedback);
            }
            catch (OperationCanceledException)
            {
                throw; // 取消不吞。
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    "[DEGRADED] critique 验证器调用失败，按拒绝收敛（不冒充通过）：{Error}", ex.Message);
                return CompletionVerdict.Reject(
                    "验证器调用失败（诚实拒绝，不冒充通过）",
                    Truncate(SensitiveTextScrubber.Scrub(ex.Message)));
            }
        }

        private static string? ExtractJsonObject(string text)
        {
            var start = text.IndexOf('{');
            var end = text.LastIndexOf('}');
            return start >= 0 && end > start ? text[start..(end + 1)] : null;
        }

        private static string? Truncate(string text) =>
            string.IsNullOrEmpty(text) ? null
                : text.Length <= MaxFeedbackChars ? text
                : text[..MaxFeedbackChars] + "…";
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
