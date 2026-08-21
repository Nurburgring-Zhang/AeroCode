using System.Net;
using AeroAgent.Moa.Gateway;
using Xunit;

namespace AeroCode.Tests.MoaTests;

/// <summary>
/// <see cref="GatewaySidecar"/> 生命周期状态机测试：
/// 未启动→启动中→运行→停止；启动失败（拉起器失败/不存在的可执行文件/进程早退/
/// 探活超时）一律如实 Failed 并留下原因；LaunchSpec 的参数/环境变量/工作目录传递；
/// watchdog 意外退出后按退避重启并恢复；ProbeAsync 探活失败显式 Degraded。
/// </summary>
public sealed class GatewaySidecarTests
{
    private static readonly Uri BaseUrl = new("http://127.0.0.1:18911");

    private static MoaGatewayClient MakeClient(GatewayFakeHttpHandler handler) =>
        new(new MoaGatewayClientOptions { BaseUrl = BaseUrl, HealthTimeout = TimeSpan.FromSeconds(2) }, handler);

    private static GatewayFakeHttpHandler HealthyHandler() =>
        new((req, _) => req.RequestUri!.AbsolutePath == "/health"
            ? GatewayTestData.JsonResponse(GatewayTestData.HealthJson)
            : GatewayTestData.JsonResponse("""{"detail":"Not Found"}""", HttpStatusCode.NotFound));

    private static GatewaySidecarOptions FastOptions(
        FakeGatewayLauncher launcher,
        string python = "python-test") =>
        new()
        {
            PythonExecutable = python,
            Host = "127.0.0.1",
            Port = 18911,
            StartupTimeout = TimeSpan.FromSeconds(5),
            HealthPollInterval = TimeSpan.FromMilliseconds(20),
            RestartBackoff = TimeSpan.FromMilliseconds(50),
            WatchdogEnabled = false, // 除 watchdog 专项测试外关闭，避免干扰
        };

    [Fact]
    public async Task InitialState_IsStopped_AndUnavailable()
    {
        using var handler = HealthyHandler();
        using var client = MakeClient(handler);
        var launcher = new FakeGatewayLauncher();
        await using var sidecar = new GatewaySidecar(client, FastOptions(launcher), launcher);

        Assert.Equal(GatewaySidecarState.Stopped, sidecar.State);
        Assert.False(sidecar.IsAvailable);
        Assert.Null(sidecar.ProcessId);
        Assert.False(await sidecar.ProbeAsync()); // 未启动时探活如实为 false
        Assert.Empty(launcher.Specs);             // 且没有偷偷拉起进程
    }

    [Fact]
    public async Task StartAsync_Success_TransitionsStartingThenRunning_AndAvailable()
    {
        using var handler = HealthyHandler();
        using var client = MakeClient(handler);
        var launcher = new FakeGatewayLauncher();
        var handle = new FakeGatewayProcessHandle();
        launcher.Enqueue(() => GatewayLaunchResult.Ok(handle));
        await using var sidecar = new GatewaySidecar(client, FastOptions(launcher), launcher);
        var observer = new SidecarStateObserver();
        sidecar.StateChanged += observer.OnStateChanged;

        var started = await sidecar.StartAsync();

        Assert.True(started, sidecar.LastError);
        Assert.Equal(new[] { GatewaySidecarState.Starting, GatewaySidecarState.Running }, observer.States);
        Assert.Equal(GatewaySidecarState.Running, sidecar.State);
        Assert.True(sidecar.IsAvailable);
        Assert.Equal(handle.ProcessId, sidecar.ProcessId);
        Assert.Null(sidecar.LastError);
        Assert.True(await sidecar.ProbeAsync());
    }

    [Fact]
    public async Task StartAsync_LauncherFails_FailedState_WithError_AndNoRetry()
    {
        using var handler = HealthyHandler();
        using var client = MakeClient(handler);
        var launcher = new FakeGatewayLauncher();
        launcher.Enqueue(() => GatewayLaunchResult.Fail("python vanished"));
        await using var sidecar = new GatewaySidecar(client, FastOptions(launcher), launcher);
        var observer = new SidecarStateObserver();
        sidecar.StateChanged += observer.OnStateChanged;

        var started = await sidecar.StartAsync();

        Assert.False(started);
        Assert.Equal(GatewaySidecarState.Failed, sidecar.State);
        Assert.False(sidecar.IsAvailable);
        Assert.Equal("python vanished", sidecar.LastError);
        Assert.Equal(new[] { GatewaySidecarState.Starting, GatewaySidecarState.Failed }, observer.States);
    }

    [Fact]
    public async Task StartAsync_NonexistentExecutable_RealLauncher_FailsHonestly()
    {
        // 真实 SystemProcessGatewayLauncher + 不存在的可执行文件：Win32 错误如实收敛为 Failed。
        using var handler = HealthyHandler();
        using var client = MakeClient(handler);
        var options = new GatewaySidecarOptions
        {
            PythonExecutable = @"C:\no-such-dir\python-does-not-exist-t2.exe",
            Port = 18911,
            StartupTimeout = TimeSpan.FromSeconds(5),
        };
        await using var sidecar = new GatewaySidecar(client, options); // 默认真实拉起器

        var started = await sidecar.StartAsync();

        Assert.False(started);
        Assert.Equal(GatewaySidecarState.Failed, sidecar.State);
        Assert.False(sidecar.IsAvailable);
        Assert.NotNull(sidecar.LastError);
        Assert.Contains("failed to start", sidecar.LastError);
        Assert.Contains("python-does-not-exist-t2.exe", sidecar.LastError);
    }

    [Fact]
    public async Task StartAsync_ProcessExitsDuringStartup_Failed_WithOutputTailDiagnostics()
    {
        using var handler = HealthyHandler();
        using var client = MakeClient(handler);
        var launcher = new FakeGatewayLauncher();
        var handle = new FakeGatewayProcessHandle
        {
            OutputTail = "ModuleNotFoundError: No module named 'moa_gateway'",
        };
        handle.SimulateExit(exitCode: 1); // 拉起即退出
        launcher.Enqueue(() => GatewayLaunchResult.Ok(handle));
        await using var sidecar = new GatewaySidecar(client, FastOptions(launcher), launcher);

        var started = await sidecar.StartAsync();

        Assert.False(started);
        Assert.Equal(GatewaySidecarState.Failed, sidecar.State);
        Assert.NotNull(sidecar.LastError);
        // 实测行为：WaitHealthyAsync 先写下更精确的 "exited during startup (code 1)"，
        // 随后 StartAsync 的统一失败原因将其覆盖——但进程输出尾部仍被拼入，诊断信息不丢。
        // （覆盖导致早退场景措辞偏泛化，已作为生产问题记录在报告。）
        Assert.Contains("did not become healthy", sidecar.LastError);
        Assert.Contains("ModuleNotFoundError", sidecar.LastError); // 输出尾部诊断可见
        Assert.Equal(1, handle.KillCount); // 早退进程句柄仍被回收
    }

    [Fact]
    public async Task StartAsync_HealthNeverPasses_FailedAfterTimeout_AndProcessKilled()
    {
        // 进程活着但 /health 始终 500：超时后如实 Failed，且回收进程不留孤儿。
        using var handler = new GatewayFakeHttpHandler(
            (_, _) => GatewayTestData.JsonResponse("""{"detail":"starting"}""", HttpStatusCode.InternalServerError));
        using var client = MakeClient(handler);
        var launcher = new FakeGatewayLauncher();
        var handle = new FakeGatewayProcessHandle();
        launcher.Enqueue(() => GatewayLaunchResult.Ok(handle));
        var options = FastOptions(launcher) with
        {
            StartupTimeout = TimeSpan.FromMilliseconds(300),
            HealthPollInterval = TimeSpan.FromMilliseconds(30),
        };
        await using var sidecar = new GatewaySidecar(client, options, launcher);

        var started = await sidecar.StartAsync();

        Assert.False(started);
        Assert.Equal(GatewaySidecarState.Failed, sidecar.State);
        Assert.Contains("did not become healthy", sidecar.LastError);
        Assert.Equal(1, handle.KillCount); // 启动失败的进程已被回收
        Assert.True(handler.Requests.Count >= 1); // 期间至少真实轮询了 1 次 /health（负载高时可能仅 1 次）
        Assert.All(handler.Requests, r => Assert.Equal("/health", r.Uri.AbsolutePath));
    }

    [Fact]
    public async Task LaunchSpec_CarriesArguments_WorkingDirectory_AndEnvironment()
    {
        using var handler = HealthyHandler();
        using var client = MakeClient(handler);
        var launcher = new FakeGatewayLauncher();
        launcher.Enqueue(() => GatewayLaunchResult.Fail("spec captured; stop here"));
        var workDir = Path.GetTempPath();
        var options = new GatewaySidecarOptions
        {
            PythonExecutable = "/opt/venv/bin/python",
            Host = "127.0.0.1",
            Port = 9911,
            WorkingDirectory = workDir,
            AdminPassword = "admin-pw-123",
            GatewayApiKey = "gw-key-456",
            ExtraEnvironmentVariables = new Dictionary<string, string>
            {
                ["QWEN_API_KEY"] = "sk-test",
            },
        };
        await using var sidecar = new GatewaySidecar(client, options, launcher);

        await sidecar.StartAsync(); // 预期失败（拉起器脚本即失败）——只为捕获 spec

        var spec = Assert.Single(launcher.Specs);
        Assert.Equal("/opt/venv/bin/python", spec.FileName);
        Assert.Equal(workDir, spec.WorkingDirectory);
        Assert.Equal(
            new[] { "-m", "uvicorn", "moa_gateway.server:app", "--host", "127.0.0.1", "--port", "9911" },
            spec.Arguments);
        Assert.Equal("admin-pw-123", spec.EnvironmentVariables["MOA_ADMIN_PASSWORD"]);
        Assert.Equal("gw-key-456", spec.EnvironmentVariables["MOA_GATEWAY_KEY"]);
        Assert.Equal("sk-test", spec.EnvironmentVariables["QWEN_API_KEY"]);
        Assert.Equal(new Uri(BaseUrl, "/health"), spec.HealthUrl);
    }

    [Fact]
    public async Task StopAsync_KillsProcess_TransitionsStopped_AndIsIdempotent()
    {
        using var handler = HealthyHandler();
        using var client = MakeClient(handler);
        var launcher = new FakeGatewayLauncher();
        var handle = new FakeGatewayProcessHandle();
        launcher.Enqueue(() => GatewayLaunchResult.Ok(handle));
        await using var sidecar = new GatewaySidecar(client, FastOptions(launcher), launcher);
        var observer = new SidecarStateObserver();
        sidecar.StateChanged += observer.OnStateChanged;

        Assert.True(await sidecar.StartAsync());
        await sidecar.StopAsync();

        Assert.Equal(GatewaySidecarState.Stopped, sidecar.State);
        Assert.False(sidecar.IsAvailable);
        Assert.Null(sidecar.ProcessId);
        Assert.Equal(1, handle.KillCount);
        Assert.Contains(GatewaySidecarState.Stopped, observer.States);

        await sidecar.StopAsync(); // 幂等：二次停止不抛、不重复杀
        Assert.Equal(1, handle.KillCount);
        Assert.Equal(GatewaySidecarState.Stopped, sidecar.State);
    }

    [Fact]
    public async Task StartAsync_WhenAlreadyRunning_DoesNotLaunchSecondProcess()
    {
        using var handler = HealthyHandler();
        using var client = MakeClient(handler);
        var launcher = new FakeGatewayLauncher();
        launcher.Enqueue(() => GatewayLaunchResult.Ok(new FakeGatewayProcessHandle()));
        await using var sidecar = new GatewaySidecar(client, FastOptions(launcher), launcher);

        Assert.True(await sidecar.StartAsync());
        Assert.True(await sidecar.StartAsync()); // 重复启动：直接返回 Running

        Assert.Single(launcher.Specs); // 只拉起了一次
        Assert.Equal(GatewaySidecarState.Running, sidecar.State);
    }

    [Fact]
    public async Task Watchdog_RestartsAfterUnexpectedExit_RecoversToRunning()
    {
        using var handler = HealthyHandler();
        using var client = MakeClient(handler);
        var launcher = new FakeGatewayLauncher();
        var first = new FakeGatewayProcessHandle();
        var second = new FakeGatewayProcessHandle();
        launcher.Enqueue(() => GatewayLaunchResult.Ok(first));
        launcher.Enqueue(() => GatewayLaunchResult.Ok(second));
        var options = FastOptions(launcher) with { WatchdogEnabled = true };
        await using var sidecar = new GatewaySidecar(client, options, launcher);
        var observer = new SidecarStateObserver();
        sidecar.StateChanged += observer.OnStateChanged;

        Assert.True(await sidecar.StartAsync());

        first.SimulateExit(exitCode: 3); // 意外退出 → watchdog 应降级并按退避重启

        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline &&
               (sidecar.State != GatewaySidecarState.Running || sidecar.RestartCount != 1))
        {
            await Task.Delay(50);
        }

        Assert.Equal(1, sidecar.RestartCount);
        Assert.Equal(GatewaySidecarState.Running, sidecar.State);
        Assert.True(sidecar.IsAvailable);
        Assert.Equal(second.ProcessId, sidecar.ProcessId);
        // 状态序列必须如实经过 Degraded（不允许悄悄从 Running 直接跳回 Running）
        Assert.Contains(GatewaySidecarState.Degraded, observer.States);

        await sidecar.StopAsync();
        Assert.Equal(GatewaySidecarState.Stopped, sidecar.State);
        Assert.True(second.KillCount >= 1);
    }

    [Fact]
    public async Task ProbeAsync_HealthFailure_TransitionsRunningToDegraded()
    {
        var healthy = true;
        using var handler = new GatewayFakeHttpHandler((req, _) =>
        {
            if (req.RequestUri!.AbsolutePath != "/health")
            {
                return GatewayTestData.JsonResponse("""{"detail":"Not Found"}""", HttpStatusCode.NotFound);
            }

            return healthy
                ? GatewayTestData.JsonResponse(GatewayTestData.HealthJson)
                : GatewayTestData.JsonResponse("""{"detail":"exploded"}""", HttpStatusCode.InternalServerError);
        });
        using var client = MakeClient(handler);
        var launcher = new FakeGatewayLauncher();
        launcher.Enqueue(() => GatewayLaunchResult.Ok(new FakeGatewayProcessHandle()));
        await using var sidecar = new GatewaySidecar(client, FastOptions(launcher), launcher);

        Assert.True(await sidecar.StartAsync());
        Assert.True(await sidecar.ProbeAsync());

        healthy = false;
        Assert.False(await sidecar.ProbeAsync());

        Assert.Equal(GatewaySidecarState.Degraded, sidecar.State);
        Assert.False(sidecar.IsAvailable);
        Assert.NotNull(sidecar.LastError);
        Assert.Contains("health probe failed", sidecar.LastError);
        Assert.Contains("500", sidecar.LastError);
    }

    [Fact]
    public async Task ProbeAsync_HealthRecovers_TransitionsDegradedToRunning()
    {
        var healthy = true;
        using var handler = new GatewayFakeHttpHandler((req, _) =>
        {
            if (req.RequestUri!.AbsolutePath != "/health")
            {
                return GatewayTestData.JsonResponse("""{"detail":"Not Found"}""", HttpStatusCode.NotFound);
            }

            return healthy
                ? GatewayTestData.JsonResponse(GatewayTestData.HealthJson)
                : GatewayTestData.JsonResponse("""{"detail":"exploded"}""", HttpStatusCode.InternalServerError);
        });
        using var client = MakeClient(handler);
        var launcher = new FakeGatewayLauncher();
        launcher.Enqueue(() => GatewayLaunchResult.Ok(new FakeGatewayProcessHandle()));
        await using var sidecar = new GatewaySidecar(client, FastOptions(launcher), launcher);
        var observer = new SidecarStateObserver();
        sidecar.StateChanged += observer.OnStateChanged;

        Assert.True(await sidecar.StartAsync());
        healthy = false;
        Assert.False(await sidecar.ProbeAsync());
        Assert.Equal(GatewaySidecarState.Degraded, sidecar.State);
        Assert.False(sidecar.IsAvailable);

        healthy = true;
        Assert.True(await sidecar.ProbeAsync());

        Assert.Equal(GatewaySidecarState.Running, sidecar.State);
        Assert.True(sidecar.IsAvailable);
        Assert.Contains(GatewaySidecarState.Degraded, observer.States);
    }

    [Fact]
    public async Task StartAsync_FromDegraded_KillsOldProcess_BeforeRelaunch()
    {
        // 模拟 Degraded 态（进程存活但探活失败）后调用 StartAsync：
        // 必须先杀掉旧进程，再拉起新进程，且旧进程句柄不会被孤儿化。
        var healthy = true;
        using var handler = new GatewayFakeHttpHandler((req, _) =>
        {
            return req.RequestUri!.AbsolutePath == "/health"
                ? (healthy
                    ? GatewayTestData.JsonResponse(GatewayTestData.HealthJson)
                    : GatewayTestData.JsonResponse("""{"detail":"exploded"}""", HttpStatusCode.InternalServerError))
                : GatewayTestData.JsonResponse("""{"detail":"Not Found"}""", HttpStatusCode.NotFound);
        });
        using var client = MakeClient(handler);
        var launcher = new FakeGatewayLauncher();
        var first = new FakeGatewayProcessHandle();
        var second = new FakeGatewayProcessHandle();
        launcher.Enqueue(() => GatewayLaunchResult.Ok(first));
        launcher.Enqueue(() => GatewayLaunchResult.Ok(second));
        var options = FastOptions(launcher) with { WatchdogEnabled = false };
        await using var sidecar = new GatewaySidecar(client, options, launcher);

        Assert.True(await sidecar.StartAsync());
        Assert.Equal(first.ProcessId, sidecar.ProcessId);

        // 通过探活失败把 sidecar 自然踩进 Degraded（不触发 watchdog）
        healthy = false;
        Assert.False(await sidecar.ProbeAsync());
        Assert.Equal(GatewaySidecarState.Degraded, sidecar.State);

        healthy = true;
        Assert.True(await sidecar.StartAsync());

        Assert.Equal(1, first.KillCount); // 旧进程在重启前被杀掉，不会孤儿化
        Assert.Equal(2, launcher.Specs.Count); // 确实拉起了第二个进程
        Assert.Equal(second.ProcessId, sidecar.ProcessId);
        Assert.Equal(GatewaySidecarState.Running, sidecar.State);
        Assert.True(sidecar.IsAvailable);
    }
}
