// Copyright (c) AeroCode
// G3 守卫链测试（builder-β）：五个守卫每守卫 ≥4 用例 + ToolGuardChain 组合 + ToolRouter 集成。
// 全部真实可机检：真实临时目录、真实文件哨兵、真实 WorkspaceContext 路径数学、真实哈希。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AeroAgent.Moa.Tools;
using AeroAgent.Moa.Tools.Workspace;
using AeroCode.AI.Models;
using AeroCode.Harness.EventBus;
using AeroCode.Harness.Permission;
using Xunit;

namespace AeroCode.Tests.MoaTests;

/// <summary>测试用守卫：可编程裁决（组合语义验证用）。</summary>
internal sealed class StubGuard : IToolGuard
{
    private readonly PermissionDecision? _decision;
    public int Calls { get; private set; }

    public StubGuard(string name, PermissionDecision? decision)
    {
        Name = name;
        _decision = decision;
    }

    public string Name { get; }

    public PermissionDecision? Check(string toolName, IReadOnlyDictionary<string, object?>? args)
    {
        Calls++;
        return _decision;
    }
}

/// <summary>
/// 守卫钉子：WorkspaceBoundary（越界→Ask）/ DoomLoop（同参哈希环形窗口）/ Estop（哨兵 fail-safe）/
/// SensitiveFile（.env 拒、force 升 Ask、配置最高优先）/ CommandClassifier（结构分级）。
/// </summary>
public sealed class GuardChainTests : IDisposable
{
    private readonly string _dir;

    public GuardChainTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"guardchain_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // 临时目录清理失败不影响测试结论
        }
    }

    private WorkspaceContext NewWorkspace()
    {
        var root = Path.Combine(_dir, $"ws_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return new WorkspaceContext(root);
    }

    private static IReadOnlyDictionary<string, object?> Args(params (string Key, object? Value)[] items) =>
        items.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

    // ---------- WorkspaceBoundaryGuard ----------

    [Fact]
    public void Boundary_PathInsideWorkspace_NoOpinion()
    {
        var ws = NewWorkspace();
        var guard = new WorkspaceBoundaryGuard(ws);
        Assert.Null(guard.Check("read_file", Args(("path", "src/a.txt"))));
        Assert.Null(guard.Check("read_file", Args(("path", "."))));
    }

    [Fact]
    public void Boundary_RelativePathEscapingRoot_Ask()
    {
        var ws = NewWorkspace();
        var guard = new WorkspaceBoundaryGuard(ws);
        Assert.Equal(PermissionDecision.Ask, guard.Check("read_file", Args(("path", "../outside.txt"))));
    }

    [Fact]
    public void Boundary_AbsolutePathOutside_Ask()
    {
        var ws = NewWorkspace();
        var guard = new WorkspaceBoundaryGuard(ws);
        var outside = Path.Combine(Path.GetTempPath(), "definitely-outside-aerocode.txt");
        Assert.Equal(PermissionDecision.Ask, guard.Check("read_file", Args(("path", outside))));
    }

    [Fact]
    public void Boundary_SuffixedPathKey_Ask_UnknownKey_NoOpinion()
    {
        var ws = NewWorkspace();
        var guard = new WorkspaceBoundaryGuard(ws);
        // 键名以 Path 结尾同样按路径解读
        Assert.Equal(PermissionDecision.Ask, guard.Check("copy", Args(("sourcePath", "C:\\Windows\\system32"))));
        // 非路径键不判定
        Assert.Null(guard.Check("grep_search", Args(("pattern", "C:\\Windows"))));
    }

    [Fact]
    public void Boundary_CommandWithAbsolutePathOutside_Ask()
    {
        var ws = NewWorkspace();
        var guard = new WorkspaceBoundaryGuard(ws);
        Assert.Equal(PermissionDecision.Ask,
            guard.Check("run_shell", Args(("command", "type C:\\Windows\\win.ini"))));
    }

    [Fact]
    public void Boundary_CommandInsideOrNonPathTokens_NoOpinion()
    {
        var ws = NewWorkspace();
        var guard = new WorkspaceBoundaryGuard(ws);
        Assert.Null(guard.Check("run_shell", Args(("command", "dotnet build"))));
        // cmd 单段旗标 /c 不是路径；引号内含空格的内部路径也不越界
        Assert.Null(guard.Check("run_shell", Args(("command", "cmd /c echo hi"))));
        Assert.Null(guard.Check("run_shell", Args(("command", $"dotnet test \"{ws.Root}\""))));
    }

    [Fact]
    public void Boundary_NullArgsOrMissingPath_NoOpinion()
    {
        var ws = NewWorkspace();
        var guard = new WorkspaceBoundaryGuard(ws);
        Assert.Null(guard.Check("read_file", null));
        Assert.Null(guard.Check("read_file", Args(("pattern", "x"))));
    }

    // ---------- DoomLoopGuard ----------

    [Fact]
    public void DoomLoop_SameArgs_ThirdCall_Ask_FourthStillAsk()
    {
        var guard = new DoomLoopGuard(threshold: 3);
        var args = Args(("command", "dotnet test"));

        Assert.Null(guard.Check("run_shell", args));
        Assert.Null(guard.Check("run_shell", args));
        Assert.Equal(PermissionDecision.Ask, guard.Check("run_shell", args));
        Assert.Equal(PermissionDecision.Ask, guard.Check("run_shell", args));
    }

    [Fact]
    public void DoomLoop_DifferentArgs_DoNotTrigger()
    {
        var guard = new DoomLoopGuard(threshold: 3);
        for (var i = 0; i < 5; i++)
        {
            Assert.Null(guard.Check("run_shell", Args(("command", $"build proj{i}"))));
        }
    }

    [Fact]
    public void DoomLoop_DifferentTool_SameArgs_DoNotTrigger()
    {
        var guard = new DoomLoopGuard(threshold: 3);
        var args = Args(("path", "a.txt"));
        Assert.Null(guard.Check("read_file", args));
        Assert.Null(guard.Check("read_file", args));
        // 工具不同 → 不同键：插进来也不推进 read_file 的计数
        Assert.Null(guard.Check("write_file", args));
        Assert.Null(guard.Check("list_directory", args));
        // read_file 仍是第 3 次 → Ask
        Assert.Equal(PermissionDecision.Ask, guard.Check("read_file", args));
    }

    [Fact]
    public void DoomLoop_ConfigurableThreshold()
    {
        var guard = new DoomLoopGuard(threshold: 2);
        var args = Args(("path", "same.txt"));
        Assert.Null(guard.Check("edit_file", args));
        Assert.Equal(PermissionDecision.Ask, guard.Check("edit_file", args));
    }

    [Fact]
    public void DoomLoop_NormalizesWhitespaceVariants_AsSameCall()
    {
        var guard = new DoomLoopGuard(threshold: 3);
        Assert.Null(guard.Check("run_shell", Args(("command", "git status"))));
        Assert.Null(guard.Check("run_shell", Args(("command", "git   status  "))));
        Assert.Equal(PermissionDecision.Ask, guard.Check("run_shell", Args(("command", " git status "))));
    }

    [Fact]
    public void DoomLoop_WindowEviction_PreventsStaleCounting()
    {
        // 容量 1 + 阈值 2：早先的 a 被逐出窗口后，再次 a 不算"第 2 次"。
        var guard = new DoomLoopGuard(threshold: 2, windowCapacity: 1);
        Assert.Null(guard.Check("run_shell", Args(("command", "a"))));
        Assert.Null(guard.Check("run_shell", Args(("command", "b")))); // a 被逐出
        Assert.Null(guard.Check("run_shell", Args(("command", "a"))));
    }

    [Fact]
    public void DoomLoop_InvalidThreshold_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DoomLoopGuard(threshold: 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DoomLoopGuard(threshold: 4, windowCapacity: 0));
        // 容量 < 阈值合法（永不触发），不抛
        var guard = new DoomLoopGuard(threshold: 4, windowCapacity: 2);
        Assert.Equal("doom-loop", guard.Name);
    }

    [Fact]
    public void DoomLoop_CanonicalHash_IsStableAndOrderInsensitive()
    {
        var a = DoomLoopGuard.CanonicalHash(Args(("path", "x"), ("mode", "w")));
        var b = DoomLoopGuard.CanonicalHash(Args(("mode", "w"), ("path", "x")));
        Assert.Equal(a, b);
        Assert.NotEqual(a, DoomLoopGuard.CanonicalHash(Args(("path", "y"), ("mode", "w"))));
    }

    // ---------- EstopGuard ----------

    [Fact]
    public void Estop_NoSentinelFile_NoOpinion_NoEvent()
    {
        var bus = new EventBus();
        var events = new List<EtopTrippedEvent>();
        bus.Subscribe<EtopTrippedEvent>(events.Add);
        var guard = new EstopGuard(Path.Combine(_dir, "estop.flag"), bus);

        Assert.Null(guard.Check("run_shell", null));
        Assert.Empty(events);
    }

    [Fact]
    public void Estop_SentinelPresent_DenyAll_AndPublishesOnce()
    {
        var sentinel = Path.Combine(_dir, "estop.flag");
        var bus = new EventBus();
        var events = new List<EtopTrippedEvent>();
        bus.Subscribe<EtopTrippedEvent>(events.Add);
        var guard = new EstopGuard(sentinel, bus);

        File.WriteAllText(sentinel, "STOP");

        Assert.Equal(PermissionDecision.Deny, guard.Check("read_file", null));
        Assert.Equal(PermissionDecision.Deny, guard.Check("write_file", null));
        Assert.Equal(PermissionDecision.Deny, guard.Check("run_shell", null));
        Assert.Single(events);
        Assert.True(guard.IsTripped());
    }

    [Fact]
    public void Estop_SentinelRemoved_Recovers_AndRepublishesOnNextTrip()
    {
        var sentinel = Path.Combine(_dir, "estop.flag");
        var bus = new EventBus();
        var events = new List<EtopTrippedEvent>();
        bus.Subscribe<EtopTrippedEvent>(events.Add);
        var guard = new EstopGuard(sentinel, bus);

        File.WriteAllText(sentinel, "STOP");
        Assert.Equal(PermissionDecision.Deny, guard.Check("read_file", null));

        File.Delete(sentinel);
        Assert.Null(guard.Check("read_file", null)); // 移除即恢复

        File.WriteAllText(sentinel, "STOP AGAIN");
        Assert.Equal(PermissionDecision.Deny, guard.Check("read_file", null));
        Assert.Equal(2, events.Count); // 触发沿各发布一次
    }

    [Fact]
    public void Estop_CorruptedSentinel_FailSafeOn_Deny()
    {
        var sentinel = Path.Combine(_dir, "estop2.flag");
        File.WriteAllText(sentinel, "random garbage");
        var guard = new EstopGuard(sentinel, new EventBus(), failSafeWhenUnavailable: true, expectedMarker: "AeroCode::ESTOP");

        Assert.Equal(PermissionDecision.Deny, guard.Check("read_file", null));
    }

    [Fact]
    public void Estop_CorruptedSentinel_FailSafeOff_Abstains()
    {
        var sentinel = Path.Combine(_dir, "estop3.flag");
        File.WriteAllText(sentinel, "random garbage");
        var guard = new EstopGuard(sentinel, new EventBus(), failSafeWhenUnavailable: false, expectedMarker: "AeroCode::ESTOP");

        Assert.Null(guard.Check("read_file", null));
    }

    [Fact]
    public void Estop_ValidMarker_Trips()
    {
        var sentinel = Path.Combine(_dir, "estop4.flag");
        File.WriteAllText(sentinel, "AeroCode::ESTOP pressed by operator");
        var guard = new EstopGuard(sentinel, new EventBus(), expectedMarker: "AeroCode::ESTOP");

        Assert.Equal(PermissionDecision.Deny, guard.Check("read_file", null));
    }

    [Fact]
    public void Estop_UnreadableSentinel_FailSafeOn_Deny()
    {
        var sentinel = Path.Combine(_dir, "estop5.flag");
        File.WriteAllText(sentinel, "STOP");
        // 独占锁打开文件 → ReadAllText 抛 IOException → "不可读" → fail-safe 判定触发
        using var locked = new FileStream(sentinel, FileMode.Open, FileAccess.Read, FileShare.None);
        var guard = new EstopGuard(sentinel, new EventBus(), failSafeWhenUnavailable: true);

        Assert.Equal(PermissionDecision.Deny, guard.Check("read_file", null));
    }

    // ---------- SensitiveFileGuard ----------

    [Fact]
    public void Sensitive_EnvRead_Deny()
    {
        var guard = new SensitiveFileGuard(NewWorkspace());
        Assert.Equal(PermissionDecision.Deny, guard.Check("read_file", Args(("path", ".env"))));
        Assert.Equal(PermissionDecision.Deny, guard.Check("read_file", Args(("path", ".env.production"))));
    }

    [Fact]
    public void Sensitive_EnvWithForce_UpgradeToAsk()
    {
        var guard = new SensitiveFileGuard(NewWorkspace());
        Assert.Equal(PermissionDecision.Ask, guard.Check("write_file", Args(("path", ".env"), ("force", true))));
    }

    [Fact]
    public void Sensitive_EnvInSubdirectory_Deny()
    {
        var guard = new SensitiveFileGuard(NewWorkspace());
        Assert.Equal(PermissionDecision.Deny, guard.Check("read_file", Args(("path", "config/sub/.env.local"))));
        Assert.Equal(PermissionDecision.Deny, guard.Check("read_file", Args(("path", ".ENV")))); // 大小写不敏感
    }

    [Fact]
    public void Sensitive_AeroCodeConfig_Deny_EvenWithForce()
    {
        var dataDir = Path.Combine(_dir, "appdata");
        Directory.CreateDirectory(dataDir);
        var guard = new SensitiveFileGuard(NewWorkspace(), aeroCodeDataDir: dataDir);
        // permissions.json 按名全域拒绝：force 不豁免（最高优先）
        Assert.Equal(PermissionDecision.Deny, guard.Check("write_file", Args(("path", "permissions.json"), ("force", true))));
        // AeroCode 数据目录之下的任意目标（含 settings.json 等通用名）：按位置 Deny
        Assert.Equal(PermissionDecision.Deny,
            guard.Check("write_file", Args(("path", Path.Combine(dataDir, "settings.json")))));
        Assert.Equal(PermissionDecision.Deny,
            guard.Check("write_file", Args(("path", Path.Combine(dataDir, "profiles.json")))));
    }

    [Fact]
    public void Sensitive_GenericConfigName_OutsideDataDir_NoOpinion()
    {
        // settings.json 是通用名（VS Code 等项目也用）：数据目录之外不按名误伤，
        // 工作区内正常读写交给边界守卫与策略。
        var guard = new SensitiveFileGuard(NewWorkspace(), aeroCodeDataDir: Path.Combine(_dir, "appdata"));
        Assert.Null(guard.Check("read_file", Args(("path", "vscode/settings.json"))));
    }

    [Fact]
    public void Sensitive_NormalFile_NoOpinion()
    {
        var guard = new SensitiveFileGuard(NewWorkspace(), aeroCodeDataDir: Path.Combine(_dir, "appdata"));
        Assert.Null(guard.Check("read_file", Args(("path", "src/app.cs"))));
        Assert.Null(guard.Check("read_file", Args(("path", ".env.example")))); // 示例文件不是凭据
    }

    [Fact]
    public void Sensitive_CommandTouchingEnv_Deny_ExamplePasses()
    {
        var guard = new SensitiveFileGuard(NewWorkspace());
        Assert.Equal(PermissionDecision.Deny, guard.Check("run_shell", Args(("command", "type .env"))));
        Assert.Equal(PermissionDecision.Deny, guard.Check("run_shell", Args(("command", "cat config/.env.local"))));
        Assert.Null(guard.Check("run_shell", Args(("command", "type .env.example"))));
    }

    [Fact]
    public void Sensitive_CommandWritingConfigViaEnvVar_Deny()
    {
        // 审计 2a 回归钉子：%VAR% 间接引用不含盘符/UNC/~/POSIX 绝对形态，边界守卫弃权；
        // Bypass 档曾可零弹窗写 hooks.json = 持久化任意命令执行。命令 token 命中
        // AeroCode 配置名 → Deny（非 Ask：写配置必须走显式用户操作）。
        var guard = new SensitiveFileGuard(NewWorkspace());
        Assert.Equal(PermissionDecision.Deny, guard.Check("run_shell",
            Args(("command", "cmd /c echo x > %LOCALAPPDATA%\\AeroCode\\hooks.json"))));
        Assert.Equal(PermissionDecision.Deny, guard.Check("run_shell",
            Args(("command", "copy x.json %LOCALAPPDATA%\\AeroCode\\jobs.json"))));
        Assert.Equal(PermissionDecision.Deny, guard.Check("run_shell",
            Args(("command", "type %LOCALAPPDATA%\\AeroCode\\permissions.json"))));
    }

    [Fact]
    public void Sensitive_CommandTouchingSettingsJson_Deny()
    {
        var guard = new SensitiveFileGuard(NewWorkspace());
        Assert.Equal(PermissionDecision.Deny, guard.Check("run_shell", Args(("command", "type settings.json"))));
        Assert.Equal(PermissionDecision.Deny, guard.Check("run_shell", Args(("command", "type SETTINGS.JSON")))); // 大小写不敏感
        Assert.Equal(PermissionDecision.Deny, guard.Check("run_shell", Args(("command", "cat config/appsettings.json"))));
        // 词边界：同名前缀文件不误伤
        Assert.Null(guard.Check("run_shell", Args(("command", "type settings.json.bak"))));
        Assert.Null(guard.Check("run_shell", Args(("command", "type mysettings.json"))));
    }

    [Fact]
    public void Sensitive_CommandGitStatus_Unaffected()
    {
        // 正常命令不因配置名收紧受影响
        var guard = new SensitiveFileGuard(NewWorkspace());
        Assert.Null(guard.Check("run_shell", Args(("command", "git status"))));
        Assert.Null(guard.Check("run_shell", Args(("command", "dotnet build src/App"))));
    }

    [Fact]
    public void Sensitive_PathSideConfigProtection_Unchanged()
    {
        // 命令侧按名收紧不改变 path 形参语义：settings.json 等通用名在数据目录外仍不按名误伤
        var guard = new SensitiveFileGuard(NewWorkspace(), aeroCodeDataDir: Path.Combine(_dir, "appdata"));
        Assert.Null(guard.Check("read_file", Args(("path", "vscode/settings.json"))));
        Assert.Null(guard.Check("read_file", Args(("path", "src/App/appsettings.json"))));
        // permissions.json 按名全域拒绝保持不变
        Assert.Equal(PermissionDecision.Deny, guard.Check("write_file", Args(("path", "permissions.json"))));
    }

    // ---------- CommandClassifier / CommandClassifierGuard ----------

    [Theory]
    [InlineData("git status", ShellCommandClass.Single)]
    [InlineData("dotnet build src/App", ShellCommandClass.Single)]
    [InlineData("cat file | grep x", ShellCommandClass.Pipeline)]
    [InlineData("echo $(whoami)", ShellCommandClass.Substitution)]
    [InlineData("echo `whoami`", ShellCommandClass.Substitution)]
    [InlineData("git commit -m \"a | b\"", ShellCommandClass.Single)] // 管道在引号内=字面量
    [InlineData("echo hi > out.txt", ShellCommandClass.Redirection)]
    [InlineData("dotnet build && dotnet test", ShellCommandClass.Chained)]
    [InlineData("a ; b", ShellCommandClass.Chained)]
    [InlineData("cmd /c echo hi", ShellCommandClass.Single)] // /c 单段旗标不是路径也不破坏单命令结构
    [InlineData("git commit -m \"unclosed", ShellCommandClass.ParseFailure)]
    [InlineData("", ShellCommandClass.ParseFailure)]
    [InlineData("echo one\necho two", ShellCommandClass.Chained)] // 多行=多命令
    public void CommandClassifier_StructureClasses_AreDeterministic(string command, ShellCommandClass expected)
    {
        Assert.Equal(expected, CommandClassifier.Classify(command));
    }

    [Fact]
    public void CommandClassifier_DecisionMapping()
    {
        Assert.Equal(PermissionDecision.Allow, CommandClassifierGuard.DecisionFor(ShellCommandClass.Single));
        Assert.Equal(PermissionDecision.Deny, CommandClassifierGuard.DecisionFor(ShellCommandClass.Substitution));
        Assert.Equal(PermissionDecision.Ask, CommandClassifierGuard.DecisionFor(ShellCommandClass.Pipeline));
        Assert.Equal(PermissionDecision.Ask, CommandClassifierGuard.DecisionFor(ShellCommandClass.Redirection));
        Assert.Equal(PermissionDecision.Ask, CommandClassifierGuard.DecisionFor(ShellCommandClass.Chained));
        Assert.Equal(PermissionDecision.Ask, CommandClassifierGuard.DecisionFor(ShellCommandClass.ParseFailure));
    }

    [Fact]
    public void CommandClassifierGuard_RoutesByCommandArg()
    {
        var guard = new CommandClassifierGuard();
        Assert.Equal(PermissionDecision.Allow, guard.Check("run_shell", Args(("command", "git status"))));
        Assert.Equal(PermissionDecision.Deny, guard.Check("run_shell", Args(("command", "echo $(rm -rf /)"))));
        Assert.Equal(PermissionDecision.Ask, guard.Check("run_shell", Args(("command", "a | b"))));
        Assert.Null(guard.Check("read_file", Args(("path", "x")))); // 无 command 参数 = 弃权
        Assert.Null(guard.Check("run_shell", null));
    }

    // ---------- ToolGuardChain 组合 ----------

    [Theory]
    [InlineData(PermissionDecision.Deny, PermissionDecision.Ask, PermissionDecision.Allow)]
    [InlineData(PermissionDecision.Ask, PermissionDecision.Deny, PermissionDecision.Allow)]
    [InlineData(PermissionDecision.Ask, PermissionDecision.Allow, PermissionDecision.Deny)]
    [InlineData(PermissionDecision.Allow, PermissionDecision.Deny, PermissionDecision.Ask)]
    [InlineData(PermissionDecision.Ask, PermissionDecision.Allow, null)]
    [InlineData(PermissionDecision.Allow, PermissionDecision.Ask, null)]
    [InlineData(PermissionDecision.Allow, PermissionDecision.Allow, null)]
    [InlineData(null, null, null)]
    public void Chain_MostPrudentVerdict_OrderIndependent(
        PermissionDecision? d1, PermissionDecision? d2, PermissionDecision? d3)
    {
        // 审计修复（Reviewer-S 1a）：链裁决与守卫装配顺序无关——任何 Deny 永远胜出，
        // Ask 只在无 Deny 时胜出；null/Allow 合成回弃权。
        static PermissionDecision? Expected(PermissionDecision?[] decisions) =>
            decisions.Contains(PermissionDecision.Deny) ? PermissionDecision.Deny
            : decisions.Contains(PermissionDecision.Ask) ? PermissionDecision.Ask
            : null;

        var decisions = new[] { d1, d2, d3 };
        var forward = new ToolGuardChain(decisions.Select((d, i) => (IToolGuard)new StubGuard($"g{i}", d)).ToList());
        var backward = new ToolGuardChain(decisions.Select((d, i) => (IToolGuard)new StubGuard($"g{i}", d)).Reverse().ToList());

        Assert.Equal(Expected(decisions), forward.Check("read_file", null));
        Assert.Equal(Expected(decisions), backward.Check("read_file", null)); // 逆序装配同裁决
    }

    [Fact]
    public void Chain_DenyBeatsAsk_AllGuardsStillConsulted()
    {
        var ask = new StubGuard("ask", PermissionDecision.Ask);
        var deny = new StubGuard("deny", PermissionDecision.Deny);
        var chain = new ToolGuardChain(new IToolGuard[] { ask, deny });

        var verdict = chain.Check("read_file", null);

        Assert.Equal(PermissionDecision.Deny, verdict);
        Assert.Equal(1, ask.Calls);
        Assert.Equal(1, deny.Calls); // 全链扫描：后置守卫不再被短路跳过
    }

    [Fact]
    public void Chain_AllAbstain_ReturnsNull()
    {
        var chain = new ToolGuardChain(new IToolGuard[]
        {
            new StubGuard("g1", null),
            new StubGuard("g2", PermissionDecision.Allow),
        });

        Assert.Null(chain.Check("read_file", null));
    }

    [Fact]
    public void Chain_AllowFromGuard_DoesNotStopLaterGuards()
    {
        var allow = new StubGuard("allow", PermissionDecision.Allow);
        var ask = new StubGuard("ask", PermissionDecision.Ask);
        var chain = new ToolGuardChain(new IToolGuard[] { allow, ask });

        Assert.Equal(PermissionDecision.Ask, chain.Check("read_file", null));
        Assert.Equal(1, ask.Calls); // Allow 不是免检金牌，后续守卫仍执行
    }

    [Fact]
    public void Chain_DoomLoopAsk_DoesNotMaskSensitiveDeny_OnThirdRepeat()
    {
        // 审计击穿路径 A 回归钉子：同一 run_shell "type .env" 第 3 次时 doom-loop 升 Ask，
        // 旧"首个非 Allow 短路"实现此处会返回 Ask（可弹窗放行）——敏感文件零泄露被击穿。
        // 修复后 SensitiveFileGuard 的 Deny 必须在每一次调用都胜出。
        var chain = new ToolGuardChain(new IToolGuard[]
        {
            new DoomLoopGuard(threshold: 3), // 复刻被审计的装配序：doom 在前、sensitive 在后
            new SensitiveFileGuard(NewWorkspace()),
        });
        var args = Args(("command", "type .env"));

        Assert.Equal(PermissionDecision.Deny, chain.Check("run_shell", args)); // 第 1 次
        Assert.Equal(PermissionDecision.Deny, chain.Check("run_shell", args)); // 第 2 次
        // 第 3 次：doom-loop 返回 Ask 的同轮，最终裁决仍必须是 Deny
        Assert.Equal(PermissionDecision.Deny, chain.Check("run_shell", args));
    }

    [Fact]
    public void Chain_EstopDeny_BeatsOtherGuardsAsks()
    {
        // 审计击穿路径 B 回归钉子：急停期间管道/链式命令先被 classifier 判 Ask，
        // 旧短路实现 EstopGuard（链尾）的 Deny 被吞掉——急停期工具弹窗仍可被批准。
        var sentinel = Path.Combine(_dir, "estop_mask.flag");
        File.WriteAllText(sentinel, "STOP");
        var ws = NewWorkspace();
        var chain = new ToolGuardChain(new IToolGuard[]
        {
            new WorkspaceBoundaryGuard(ws),
            new CommandClassifierGuard(),
            new DoomLoopGuard(threshold: 2),
            new EstopGuard(sentinel, new EventBus()), // 复刻被审计的装配序：estop 链尾
        });

        Assert.Equal(PermissionDecision.Deny, chain.Check("run_shell", Args(("command", "type a | findstr b"))));
        Assert.Equal(PermissionDecision.Deny, chain.Check("run_shell", Args(("command", "dotnet build && dotnet test"))));

        // 顺序无关：estop 排链首同样 Deny
        var reversed = new ToolGuardChain(new IToolGuard[]
        {
            new EstopGuard(sentinel, new EventBus()),
            new CommandClassifierGuard(),
        });
        Assert.Equal(PermissionDecision.Deny, reversed.Check("run_shell", Args(("command", "a | b"))));
    }

    // ---------- ToolRouter 集成（守卫链挂 preCheck） ----------

    private static ToolDefinition Def(string name) => new()
    {
        Name = name,
        Description = name,
        ParametersJsonSchema = "{\"type\":\"object\"}",
    };

    [Fact]
    public async Task Router_Integration_BoundaryGuardAsks_UserAllows_Runs()
    {
        var ws = NewWorkspace();
        var policy = PermissionPolicy.CreateDefault(new EventBus());
        var box = new ScriptedToolbox("notes", Def("read_file"));
        box.SetResult("read_file", ToolInvokeResult.Ok("内容"));
        var broker = new ScriptedBroker(PermissionDecision.Allow);
        var chain = new ToolGuardChain(new IToolGuard[] { new WorkspaceBoundaryGuard(ws) });
        var router = new ToolRouter(RegistryWith(box), policy, broker, preCheck: chain.Check);

        var result = await router.InvokeAsync("read_file", "{\"path\":\"C:/Windows/system32/config\"}", CancellationToken.None);

        Assert.True(result.Success); // 用户在弹窗里放行了越界读
        Assert.Single(broker.Consultations); // 守卫 Ask 真实送到 broker
        Assert.Single(box.Invocations);
    }

    [Fact]
    public async Task Router_Integration_SensitiveGuardDenies_EnvRead_WithoutBroker()
    {
        var policy = PermissionPolicy.CreateDefault(new EventBus());
        var box = new ScriptedToolbox("notes", Def("read_file"));
        box.SetResult("read_file", ToolInvokeResult.Ok("SECRET"));
        var broker = new ScriptedBroker(PermissionDecision.Allow);
        var chain = new ToolGuardChain(new IToolGuard[] { new SensitiveFileGuard(NewWorkspace()) });
        var router = new ToolRouter(RegistryWith(box), policy, broker, preCheck: chain.Check);

        var result = await router.InvokeAsync("read_file", "{\"path\":\".env\"}", CancellationToken.None);

        Assert.True(result.Denied); // .env 零泄露：即使用户代理在也直接拒
        Assert.Contains("forbidden by policy", result.Output);
        Assert.Empty(broker.Consultations);
        Assert.Empty(box.Invocations);
    }

    private static ToolboxRegistry RegistryWith(ScriptedToolbox box)
    {
        var registry = new ToolboxRegistry();
        registry.Register(box);
        return registry;
    }
}
