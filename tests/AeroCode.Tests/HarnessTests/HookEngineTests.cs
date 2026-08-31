// Copyright (c) AeroCode
// HookEngine 真实行为测试（批次 B G4，builder-γ）：零 mock——
// 触发=真实子进程写真实文件；stdin=ps1 真读管道；超时=真杀 25s 进程（2s 注入超时）；
// 截断=真打 100k 行输出；坏配置=真实 hooks.json 拒载。
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AeroCode.Harness.EventBus;
using AeroCode.Harness.Hooks;
using Xunit;

namespace AeroCode.Tests.HarnessTests;

public sealed class HookEngineTests : IDisposable
{
    private readonly string _dir;
    private readonly string _hooksPath;

    public HookEngineTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "aerocode-hook-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _hooksPath = Path.Combine(_dir, "hooks.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort 清理 */ }
    }

    private string MarkerPath(string name) => Path.Combine(_dir, name);

    /// <summary>写一份 hooks.json 并加载到新引擎。</summary>
    private HookEngine NewEngine(string hooksJson, EventBus? bus = null)
    {
        File.WriteAllText(_hooksPath, hooksJson);
        var engine = new HookEngine(bus ?? new EventBus());
        engine.LoadFrom(_hooksPath);
        return engine;
    }

    private static string HooksJson(params object[] defs)
        => "[" + string.Join(",", defs.Select(d => JsonSerializer.Serialize(d))) + "]";

    private static object Hook(string id, string evt, string command, string? match = null, int timeoutSec = 30, bool enabled = true)
    {
        var o = new System.Collections.Generic.Dictionary<string, object?>
        {
            ["id"] = id, ["event"] = evt, ["command"] = command, ["timeoutSec"] = timeoutSec, ["enabled"] = enabled,
        };
        if (match is not null)
        {
            o["match"] = match;
        }

        return o;
    }

    /// <summary>轮询直到条件成立（引擎 Dispatch 为异步派发不等待，观测点用真实副作用/事件）。</summary>
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

    // ---- 加载与校验（fail-safe） ----

    [Fact]
    public void Load_ValidConfig_ReturnsCountAndSnapshot()
    {
        using var engine = NewEngine(HooksJson(
            Hook("h1", "ToolCallEvent", "echo one"),
            Hook("h2", "ToolResultEvent", "echo two", match: "ok", enabled: false)));

        Assert.Equal(2, engine.Hooks.Count);
        Assert.Equal("h1", engine.Hooks[0].Id);
        Assert.True(engine.Hooks[0].Enabled);
        Assert.False(engine.Hooks[1].Enabled); // 禁用钩子保留配置
        Assert.Equal("ok", engine.Hooks[1].Match);
    }

    [Fact]
    public void Load_MissingFile_ThrowsInvalidDataException()
    {
        using var engine = new HookEngine(new EventBus());
        Assert.Throws<InvalidDataException>(() => engine.LoadFrom(Path.Combine(_dir, "nope.json")));
    }

    [Fact]
    public void Load_BadJson_Throws_AndKeepsPreviousGoodConfig()
    {
        using var engine = NewEngine(HooksJson(Hook("good", "ToolCallEvent", "echo keep")));
        Assert.Single(engine.Hooks);

        File.WriteAllText(_hooksPath, "{ this is not json ]");
        Assert.Throws<InvalidDataException>(() => engine.LoadFrom(_hooksPath));

        // fail-safe：拒载坏配置，上一份有效配置原样保留（不半载、不清空）
        Assert.Single(engine.Hooks);
        Assert.Equal("good", engine.Hooks[0].Id);
    }

    [Fact]
    public void Load_EntryMissingCommand_RejectsWholeFile_NoHalfLoad()
    {
        // 一条合法 + 一条缺 command：fail-safe = 整份拒载，绝不带着半截配置跑
        var json = JsonSerializer.Serialize(new[]
        {
            new { id = "valid", @event = "ToolCallEvent", command = (string?)"echo hi", timeoutSec = 30 },
            new { id = "broken", @event = "ToolCallEvent", command = (string?)null, timeoutSec = 30 },
        });
        File.WriteAllText(_hooksPath, json);

        using var engine = new HookEngine(new EventBus());
        Assert.Throws<InvalidDataException>(() => engine.LoadFrom(_hooksPath));
        Assert.Empty(engine.Hooks);
    }

    [Fact]
    public void Load_MissingEventField_ThrowsInvalidDataException()
    {
        var json = JsonSerializer.Serialize(new[] { new { id = "x", command = "echo hi" } });
        File.WriteAllText(_hooksPath, json);
        using var engine = new HookEngine(new EventBus());
        Assert.Throws<InvalidDataException>(() => engine.LoadFrom(_hooksPath));
    }

    [Fact]
    public void Load_DuplicateId_ThrowsInvalidDataException()
    {
        File.WriteAllText(_hooksPath, HooksJson(
            Hook("dup", "ToolCallEvent", "echo a"),
            Hook("dup", "ToolResultEvent", "echo b")));

        using var engine = new HookEngine(new EventBus());
        Assert.Throws<InvalidDataException>(() => engine.LoadFrom(_hooksPath));
        Assert.Empty(engine.Hooks);
    }

    [Fact]
    public void Load_NonPositiveTimeout_ThrowsInvalidDataException()
    {
        var json = JsonSerializer.Serialize(new[] { new { id = "t", @event = "ToolCallEvent", command = "echo hi", timeoutSec = -5 } });
        File.WriteAllText(_hooksPath, json);
        using var engine = new HookEngine(new EventBus());
        Assert.Throws<InvalidDataException>(() => engine.LoadFrom(_hooksPath));
    }

    // ---- 触发与执行（真实进程） ----

    [Fact]
    public async Task Dispatch_MatchingEvent_RunsRealCommand_WritesMarkerFile()
    {
        var marker = MarkerPath("triggered.txt");
        using var engine = NewEngine(HooksJson(Hook("t1", "ToolCallEvent", $"echo triggered> \"{marker}\"")));

        engine.Dispatch("ToolCallEvent", """{"ToolName":"run_shell"}""");

        await WaitUntilAsync(() => File.Exists(marker), TimeSpan.FromSeconds(15), "marker file written by hook command");
        // 子进程写盘先于父进程追加 Runs 记录：全量并行负载下两者间有可见窗口，
        // 先等 Runs 落账再断言（断言本身不变，只消除竞态）。
        await WaitUntilAsync(() => engine.Runs.Count > 0, TimeSpan.FromSeconds(60), "hook run record appended after process exit");
        Assert.Contains("triggered", File.ReadAllText(marker));
        var run = Assert.Single(engine.Runs);
        Assert.Equal("t1", run.HookId);
        Assert.True(run.Success, $"stderr: {run.StdErr}");
        Assert.Equal(0, run.ExitCode);
    }

    [Fact]
    public async Task Dispatch_EventJsonReachesHookViaStdin()
    {
        var outPath = MarkerPath("stdin.json");
        var ps1 = Path.Combine(_dir, "stdin_hook.ps1");
        // 真实从 stdin 读到 EOF 再落盘——stdin 未通则文件不会出现
        File.WriteAllText(ps1, "param($outPath)\n$input | Set-Content -LiteralPath $outPath -Encoding UTF8\n");
        var command = $"powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"{ps1}\" \"{outPath}\"";
        using var engine = NewEngine(HooksJson(Hook("stdin-hook", "ToolResultEvent", command, timeoutSec: 30)));

        engine.Dispatch("ToolResultEvent", """{"ToolName":"stdin_probe","Success":true}""");

        await WaitUntilAsync(() => File.Exists(outPath), TimeSpan.FromSeconds(30), "stdin json captured by hook process");
        var captured = File.ReadAllText(outPath);
        Assert.Contains("\"ToolName\":\"stdin_probe\"", captured);
        Assert.Contains("\"Success\":true", captured);
    }

    [Fact]
    public async Task Dispatch_MatchFilter_MismatchSkips_MatchRuns()
    {
        var marker = MarkerPath("matched.txt");
        using var engine = NewEngine(HooksJson(Hook("m1", "ToolCallEvent", $"echo hit> \"{marker}\"", match: "needle-token")));

        // 不含 needle-token：不触发
        engine.Dispatch("ToolCallEvent", """{"ToolName":"other"}""");
        await Task.Delay(1500);
        Assert.False(File.Exists(marker), "hook must NOT run when match substring is absent");
        Assert.Empty(engine.Runs);

        // 含 needle-token：触发
        engine.Dispatch("ToolCallEvent", """{"ToolName":"x","note":"needle-token here"}""");
        await WaitUntilAsync(() => File.Exists(marker), TimeSpan.FromSeconds(15), "marker after matching dispatch");
        Assert.Contains("hit", File.ReadAllText(marker));
    }

    [Fact]
    public async Task Dispatch_DisabledHook_DoesNotRun()
    {
        var marker = MarkerPath("disabled.txt");
        using var engine = NewEngine(HooksJson(Hook("off", "ToolCallEvent", $"echo never> \"{marker}\"", enabled: false)));

        engine.Dispatch("ToolCallEvent", """{"ToolName":"whatever"}""");
        await Task.Delay(1500);

        Assert.False(File.Exists(marker), "disabled hook must not run");
        Assert.Empty(engine.Runs);
    }

    [Fact]
    public async Task Dispatch_WrongEventName_DoesNotRun()
    {
        var marker = MarkerPath("wrongevt.txt");
        using var engine = NewEngine(HooksJson(Hook("w1", "SessionEndEvent", $"echo wrong> \"{marker}\"")));

        engine.Dispatch("ToolCallEvent", "{}");
        await Task.Delay(1200);
        Assert.False(File.Exists(marker), "hook bound to another event must not run");
    }

    [Fact]
    public async Task Dispatch_Timeout_KillsProcessTree_AndReportsFailure()
    {
        using var engine = NewEngine(HooksJson(Hook(
            "slow", "ToolCallEvent", "powershell.exe -NoProfile -Command \"Start-Sleep -Seconds 25\"", timeoutSec: 2)));
        var sw = System.Diagnostics.Stopwatch.StartNew();

        engine.Dispatch("ToolCallEvent", "{}");

        await WaitUntilAsync(() => engine.Runs.Count > 0, TimeSpan.FromSeconds(20), "timeout run recorded");
        sw.Stop();
        var run = Assert.Single(engine.Runs);
        Assert.False(run.Success, "timed-out hook must be reported as failure");
        Assert.True(run.TimedOut, "run must be flagged TimedOut (honest, not faked as normal exit)");
        Assert.Contains("timed out", run.StdErr);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15),
            $"2s injected timeout must kill the 25s process far earlier; took {sw.Elapsed}");
    }

    [Fact]
    public async Task Dispatch_HugeStdout_TruncatedAt50KB()
    {
        // 单次 60KB 写（毫秒级完成，远超 50KB 截断帽）：全量并行下逐行 2 万行管道的墙钟
        // 曾被拉长到突破钩子 60s 超时窗（δ3 已从 10 万行降到 2 万行，重负载下仍临界）；
        // 一次性大写不改变被验证的截断语义（50KB 帽 + 截断标注 + 成功退出）。
        using var engine = NewEngine(HooksJson(Hook(
            "loud", "ToolCallEvent",
            "powershell.exe -NoProfile -Command \"'x' * 60000\"",
            timeoutSec: 60)));

        engine.Dispatch("ToolCallEvent", "{}");

        // 等待窗与钩子自身超时（timeoutSec=60）对齐；截断/成功断言不变。
        await WaitUntilAsync(() => engine.Runs.Count > 0, TimeSpan.FromSeconds(60), "loud run recorded");
        var run = Assert.Single(engine.Runs);
        Assert.True(run.Success, $"stderr: {run.StdErr}");
        Assert.True(run.StdOut.Length > 49_000, $"expected near-cap output, got {run.StdOut.Length}");
        Assert.True(run.StdOut.Length <= HookEngine.MaxCharsPerStream + 200,
            $"output must be capped near {HookEngine.MaxCharsPerStream}, got {run.StdOut.Length}");
        Assert.Contains("[aerocode] output truncated at 50000 chars", run.StdOut);
    }

    [Fact]
    public async Task Dispatch_FailingCommand_ReportsSuccessFalse_NotException()
    {
        using var engine = NewEngine(HooksJson(Hook("fail", "ToolCallEvent", "cmd /c exit 3")));

        engine.Dispatch("ToolCallEvent", "{}");

        await WaitUntilAsync(() => engine.Runs.Count > 0, TimeSpan.FromSeconds(15), "failing run recorded");
        var run = Assert.Single(engine.Runs);
        Assert.False(run.Success);
        Assert.Equal(3, run.ExitCode);
        Assert.False(run.TimedOut);
    }

    // ---- EventBus 接线：HookExecutedEvent 发布 + 防自递归 ----

    [Fact]
    public async Task Engine_PublishesHookExecutedEvent_AndDoesNotRecurseIntoHooks()
    {
        var bus = new EventBus();
        var marker = MarkerPath("recurse.txt");
        HookExecutedEvent? executed = null;
        bus.Subscribe<HookExecutedEvent>(e => executed = e);

        File.WriteAllText(_hooksPath, HooksJson(Hook("pub", "ToolResultEvent", $"echo published> \"{marker}\"")));
        using var engine = new HookEngine(bus);
        engine.LoadFrom(_hooksPath);

        engine.Dispatch("ToolResultEvent", "{}");

        await WaitUntilAsync(() => executed is not null, TimeSpan.FromSeconds(15), "HookExecutedEvent published");
        Assert.Equal("pub", executed!.HookId);
        Assert.Equal("ToolResultEvent", executed.EventName);
        Assert.True(executed.Success);
        Assert.True(File.Exists(marker));

        // 防自递归：HookExecutedEvent 不进默认钩子分发——发布后钩子只跑过这一次
        await Task.Delay(1000);
        Assert.Equal(1, engine.Runs.Count(r => r.HookId == "pub"));
    }

    [Fact]
    public async Task Engine_WiresDefaultHarnessEvents_ToolCallEventDispatchesHook()
    {
        var bus = new EventBus();
        var marker = MarkerPath("wired.txt");
        File.WriteAllText(_hooksPath, HooksJson(Hook("wired", "ToolCallEvent", $"echo wired> \"{marker}\"")));
        using var engine = new HookEngine(bus);
        engine.LoadFrom(_hooksPath);

        // 构造期接线：往总线发真实 Harness 事件即应触发钩子（事件名=记录类型名）
        bus.Publish(new ToolCallEvent("run_shell", new System.Collections.Generic.Dictionary<string, object?>(), DateTime.UtcNow));

        await WaitUntilAsync(() => File.Exists(marker), TimeSpan.FromSeconds(15), "hook fired via EventBus wiring");
        Assert.Contains("wired", File.ReadAllText(marker));
    }
}
