// Copyright (c) AeroCode
// CompositionRootTests — 组合根（App.axaml.cs BuildServices）安全内核装配语义的集成测试。
// 复刻组合根的真实装配拓扑（守卫顺序 / breaker 包装 / HookEngine 总线订阅 / Scheduler 持久化），
// 全部真实组件 + 真实磁盘；唯一替身是"人工授权通道"（Avalonia 弹窗无法在无头测试中运行，
// 与 DialogPermissionBrokerTests 的 ScriptedPresenter 同一约定）。
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AeroAgent.Moa.Safety;
using AeroAgent.Moa.Tools;
using AeroAgent.Moa.Tools.Workspace;
using AeroCode.Harness.EventBus;
using AeroCode.Harness.Hooks;
using AeroCode.Harness.Permission;
using AeroCode.Harness.Scheduler;
using Xunit;

namespace AeroCode.Tests.AppTests;

public sealed class CompositionRootTests : IDisposable
{
    private readonly string _workspaceRoot;
    private readonly string _dataDir;

    public CompositionRootTests()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "aerocode-composition-root", Guid.NewGuid().ToString("N"));
        _workspaceRoot = Path.Combine(baseDir, "workspace");
        _dataDir = Path.Combine(baseDir, "appdata");
        Directory.CreateDirectory(_workspaceRoot);
        Directory.CreateDirectory(_dataDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path.GetDirectoryName(_workspaceRoot)!, recursive: true);
        }
        catch
        {
            // best-effort 清理
        }
    }

    /// <summary>
    /// 复刻 App.axaml.cs 的守卫链装配（顺序即契约）：
    /// 工作区边界 → 命令结构分级 → doom-loop → 敏感文件 → 可选急停哨兵。
    /// </summary>
    private static ToolGuardChain BuildChain(
        WorkspaceContext workspace, string dataDir, string? estopFile = null, EventBus? estopBus = null)
    {
        var guards = new List<IToolGuard>
        {
            new WorkspaceBoundaryGuard(workspace),
            new CommandClassifierGuard(),
            new DoomLoopGuard(threshold: 3),
            new SensitiveFileGuard(workspace, dataDir),
        };
        if (!string.IsNullOrWhiteSpace(estopFile))
        {
            guards.Add(new EstopGuard(estopFile, estopBus ?? new EventBus()));
        }

        return new ToolGuardChain(guards);
    }

    private static Dictionary<string, object?> Args(params (string Key, object Value)[] pairs)
    {
        var d = new Dictionary<string, object?>();
        foreach (var (key, value) in pairs)
        {
            d[key] = value;
        }

        return d;
    }

    /// <summary>轮询直到条件成立（钩子/子进程派发为异步，观测真实副作用）。</summary>
    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan limit, string what)
    {
        var deadline = DateTime.UtcNow + limit;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.True(condition(), $"condition not met within {limit.TotalSeconds:0}s: {what}");
    }

    // ---- 1. 守卫链：工作区越界 → Ask（合法操作仍可由用户放行，不 Deny）----

    [Fact]
    public void GuardChain_OutOfBoundaryPath_EscalatesAsk_InBoundaryPassesThrough()
    {
        var chain = BuildChain(new WorkspaceContext(_workspaceRoot), _dataDir);
        var outside = Path.Combine(Path.GetTempPath(), "outside-target.cs");

        // 越界绝对路径 → Ask（链的首个非 Allow 裁决）
        Assert.Equal(PermissionDecision.Ask, chain.Check("write_file", Args(("path", outside))));

        // 工作区内路径 → 链无意见（null = 交还策略）
        var inside = Path.Combine(_workspaceRoot, "src", "a.cs");
        Assert.Null(chain.Check("write_file", Args(("path", inside))));
    }

    // ---- 2. 守卫链：doom-loop 第 3 次同参调用 → Ask ----

    [Fact]
    public void GuardChain_DoomLoop_ThirdIdenticalCall_EscalatesAsk()
    {
        var chain = BuildChain(new WorkspaceContext(_workspaceRoot), _dataDir);
        var args = Args(("path", Path.Combine(_workspaceRoot, "loop.txt")));

        Assert.Null(chain.Check("read_file", args)); // 第 1 次
        Assert.Null(chain.Check("read_file", args)); // 第 2 次
        Assert.Equal(PermissionDecision.Ask, chain.Check("read_file", args)); // 第 3 次 = 阈值
    }

    // ---- 3. 守卫链：敏感文件 → Deny；force 升 Ask；shell 管道命中同样 Deny ----

    [Fact]
    public void GuardChain_SensitiveFile_EnvDenied_ForceElevatesToAsk_ShellPipeDenied()
    {
        var chain = BuildChain(new WorkspaceContext(_workspaceRoot), _dataDir);
        var envPath = Path.Combine(_workspaceRoot, ".env");

        // .env 凭据文件：默认 Deny（零泄露）
        Assert.Equal(PermissionDecision.Deny, chain.Check("read_file", Args(("path", envPath))));

        // 用户显式豁免（force:true）：守卫只升到 Ask，交人工裁决，绝不静默放行
        Assert.Equal(PermissionDecision.Ask, chain.Check("read_file", Args(("path", envPath), ("force", true))));

        // shell 命令管道没有 force 语义：cat .env 一律 Deny
        Assert.Equal(PermissionDecision.Deny, chain.Check("run_shell", Args(("command", "type .env"))));
    }

    // ---- 4. 守卫链：AeroCode 配置自保护（按文件名全域拒绝，force 不可豁免）----
    // 组合序事实：工作区外路径先被边界守卫升 Ask（短路）——数据目录保护对工作区外
    // 目标表现为"人工 Ask 闸门"；本用例钉住工作区内配置文件的无条件 Deny 语义。

    [Fact]
    public void GuardChain_AeroCodeConfigSelfProtection_DenyEvenWithForce()
    {
        var chain = BuildChain(new WorkspaceContext(_workspaceRoot), _dataDir);
        var config = Path.Combine(_workspaceRoot, "permissions.json");

        Assert.Equal(PermissionDecision.Deny, chain.Check("write_file", Args(("path", config))));
        // force 也无法豁免配置自保护（不变量：审批链自身不能被工具改写）
        Assert.Equal(
            PermissionDecision.Deny,
            chain.Check("write_file", Args(("path", config), ("force", true))));
    }

    // ---- 5. 守卫链：急停哨兵 → Deny + 触发沿发布一次 EtopTrippedEvent ----

    [Fact]
    public void GuardChain_EstopSentinel_DeniesAllTools_PublishesTripEventOnce()
    {
        var bus = new EventBus();
        var trips = new List<EtopTrippedEvent>();
        bus.Subscribe<EtopTrippedEvent>(e => trips.Add(e));
        var sentinel = Path.Combine(_dataDir, "ESTOP");
        var chain = BuildChain(new WorkspaceContext(_workspaceRoot), _dataDir, sentinel, bus);

        File.WriteAllText(sentinel, "stop");
        // 每次检查用不同参数：doom-loop 守卫在链中先于 estop，同参重复会先短路成 Ask。
        Assert.Equal(PermissionDecision.Deny, chain.Check("write_file", Args(("path", Path.Combine(_workspaceRoot, "a.txt")))));

        // 触发沿只发布一次；持续触发不重复刷事件
        Assert.Equal(PermissionDecision.Deny, chain.Check("write_file", Args(("path", Path.Combine(_workspaceRoot, "b.txt")))));
        Assert.Single(trips);

        // 哨兵移除后恢复：链交还策略（急停不销毁计划）
        File.Delete(sentinel);
        Assert.Null(chain.Check("write_file", Args(("path", Path.Combine(_workspaceRoot, "c.txt")))));
    }

    // ---- 6. 审批熔断：连续批准超限 → 强制人工通道 + 发布熔断事件 ----

    [Fact]
    public async Task ApprovalCircuitBreaker_BurstExceeded_ForcesInteractiveAndPublishesEvent()
    {
        var bus = new EventBus();
        var broken = new List<ApprovalCircuitBrokenEvent>();
        bus.Subscribe<ApprovalCircuitBrokenEvent>(e => broken.Add(e));

        // 组合根拓扑的等价替身：interactive = 人工弹窗通道，autoAdopt = 快速通道
        var interactive = new ScriptedBroker(PermissionDecision.Allow);
        var autoAdopt = new ScriptedBroker(PermissionDecision.Allow);
        var breaker = new ApprovalCircuitBreaker(
            interactiveBroker: interactive,
            autoAdoptBroker: autoAdopt,
            eventBus: bus,
            sessionId: "comp-root-test",
            maxConsecutiveApprovals: 2,
            maxSessionCostUsd: 5.0);

        var args = Args(("path", Path.Combine(_workspaceRoot, "f.txt")));
        Assert.Equal(PermissionDecision.Allow, await breaker.ResolveAsync("write_file", args, CancellationToken.None));
        Assert.Equal(PermissionDecision.Allow, await breaker.ResolveAsync("write_file", args, CancellationToken.None));
        Assert.Equal(0, interactive.Calls); // 未熔断：全部走快速通道

        // 第 3 次：连续批准达到阈值 2 → 熔断，强制人工通道，事件发布一次
        Assert.Equal(PermissionDecision.Allow, await breaker.ResolveAsync("write_file", args, CancellationToken.None));
        Assert.Equal(1, interactive.Calls);
        Assert.True(breaker.IsBroken);
        Assert.Equal(2, autoAdopt.Calls);
        Assert.Single(broken);
        Assert.Equal("comp-root-test", broken[0].SessionId);

        // 熔断锁存：后续全部走人工（自动采纳不再被信任）——通道计数是锁存证据
        Assert.Equal(PermissionDecision.Allow, await breaker.ResolveAsync("write_file", args, CancellationToken.None));
        Assert.Equal(2, interactive.Calls);
        Assert.Equal(2, autoAdopt.Calls);
    }

    // ---- 7. HookEngine 端到端：EventBus.Publish → 真实子进程把事件 JSON 写进真实文件 ----

    [Fact]
    public async Task HookEngine_EndToEndViaEventBus_WritesRealFile()
    {
        var outPath = Path.Combine(_dataDir, "session-start-capture.json");
        var ps1 = Path.Combine(_dataDir, "capture.ps1");
        // 真实从 stdin 读到 EOF 再落盘——stdin 未通则文件不会出现
        File.WriteAllText(ps1, "param($outPath)\n$input | Set-Content -LiteralPath $outPath -Encoding UTF8\n");
        var command = $"powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"{ps1}\" \"{outPath}\"";

        var hooksPath = Path.Combine(_dataDir, "hooks.json");
        var hookDef = new Dictionary<string, object?>
        {
            ["id"] = "capture-session-start",
            ["event"] = "SessionStartEvent",
            ["command"] = command,
            ["timeoutSec"] = 30,
            ["enabled"] = true,
        };
        File.WriteAllText(hooksPath, JsonSerializer.Serialize(new[] { hookDef }));

        // 组合根装配路径：引擎构造即订阅 EventBus 白名单事件
        var bus = new EventBus();
        using var engine = new HookEngine(bus);
        Assert.Equal(1, engine.LoadFrom(hooksPath));

        bus.Publish(new SessionStartEvent("comp-root-e2e-session", DateTime.UtcNow));
        await WaitUntilAsync(
            () => engine.Runs.Count == 1,
            TimeSpan.FromSeconds(30),
            "hook run recorded after SessionStartEvent");

        var captured = File.ReadAllText(outPath);
        Assert.Contains("comp-root-e2e-session", captured);
        Assert.True(engine.Runs[0].Success);
    }

    // ---- 8. Scheduler：jobs.json 真实落盘 + 重启重载 + 一次性任务触发后停用持久化 ----

    [Fact]
    public async Task Scheduler_JobsPersistedReloaded_OneShotConsumedAcrossRestart()
    {
        var jobsPath = Path.Combine(_dataDir, "jobs.json");
        var firedMarker = Path.Combine(_dataDir, "fired.txt");

        var svc = new SchedulerService(jobsPath, estopSentinelPath: null, bus: null, log: null);
        svc.AddOrUpdate(new JobDef
        {
            Id = "persist-once",
            AtUtc = DateTime.UtcNow.AddSeconds(-5), // 已到期的一次性任务
            Command = $"echo fired> \"{firedMarker}\"",
            TimeoutSec = 30,
        });

        // 持久化即时落盘
        Assert.True(File.Exists(jobsPath));

        // 可测核心：真实子进程触发（文件真实出现）
        Assert.Equal(1, svc.RunDueJobsOnce(DateTimeOffset.UtcNow.AddSeconds(5)));
        await WaitUntilAsync(() => File.Exists(firedMarker), TimeSpan.FromSeconds(20), "job subprocess wrote marker");
        // 一次性消耗：立即停用并落盘
        Assert.False(svc.Jobs[0].Enabled);

        // 组合根重启语义：新实例 Load 恢复任务定义，且消耗状态（停用）一并恢复
        var reloaded = new SchedulerService(jobsPath, estopSentinelPath: null, bus: null, log: null);
        reloaded.Load();
        var job = Assert.Single(reloaded.Jobs);
        Assert.Equal("persist-once", job.Id);
        Assert.False(job.Enabled); // 复燃不可能：消耗已持久化
        Assert.Null(reloaded.LastLoadError);
    }

    /// <summary>
    /// 人工授权通道替身：按脚本出队裁决并记录调用（弹窗无法无头运行）。
    /// 脚本耗尽后返回 <see cref="_fallback"/>——模拟"始终同意的人类"（粘性语义）。
    /// </summary>
    private sealed class ScriptedBroker : IPermissionBroker
    {
        private readonly Queue<PermissionDecision> _decisions;
        private readonly PermissionDecision _fallback;

        public ScriptedBroker(PermissionDecision fallback = PermissionDecision.Allow, params PermissionDecision[] decisions)
        {
            _fallback = fallback;
            _decisions = new Queue<PermissionDecision>(decisions);
        }

        public int Calls { get; private set; }

        public ValueTask<PermissionDecision> ResolveAsync(
            string toolName,
            IReadOnlyDictionary<string, object?>? args,
            CancellationToken ct)
        {
            Calls++;
            lock (_decisions)
            {
                return ValueTask.FromResult(
                    _decisions.Count > 0 ? _decisions.Dequeue() : _fallback);
            }
        }
    }
}
