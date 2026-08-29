using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AeroAgent.Moa.Gateway;

/// <summary>网关 sidecar 生命周期状态。</summary>
public enum GatewaySidecarState
{
    /// <summary>未启动（初始态或已停止）。</summary>
    Stopped = 0,

    /// <summary>进程已拉起，正在轮询健康探活。</summary>
    Starting = 1,

    /// <summary>进程存活且健康探活通过——可用。</summary>
    Running = 2,

    /// <summary>进程意外退出，watchdog 正在按退避重启——暂不可用。</summary>
    Degraded = 3,

    /// <summary>启动失败或重启次数耗尽——不可用，需人工介入。</summary>
    Failed = 4,
}

/// <summary>
/// sidecar 启动选项。网关是 moa-gateway-pro 的 FastAPI 进程，
/// 以 <c>&lt;python&gt; -m uvicorn moa_gateway.server:app --host H --port P</c> 真实拉起
/// （与官方 <c>moa serve</c> 内部命令一致，单进程便于整树回收）。
/// </summary>
public sealed record GatewaySidecarOptions
{
    /// <summary>Python 解释器（绝对路径或 PATH 名）。部署脚本建的 venv 解释器优先。</summary>
    public string PythonExecutable { get; init; } = "python";

    public string Host { get; init; } = "127.0.0.1";

    /// <summary>网关端口（moa-gateway-pro 默认 8910）。</summary>
    public int Port { get; init; } = 8910;

    /// <summary>子进程工作目录；null = 继承本进程。</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// 注入子进程的 <c>MOA_ADMIN_PASSWORD</c>（init-data/管理端需要；serve 本身不校验，
    /// 但官方 start.py 约定始终携带）。null = 不注入。
    /// </summary>
    public string? AdminPassword { get; init; }

    /// <summary>
    /// 注入子进程的 <c>MOA_GATEWAY_KEY</c>。注意 v3.1.1 运行期鉴权读的是
    /// config.yaml 的 auth.gateway_api_keys（部署脚本负责同步写入）；此处注入仅为
    /// 与官方 internal_callback/auto_setup 约定保持一致。null = 不注入。
    /// </summary>
    public string? GatewayApiKey { get; init; }

    /// <summary>追加注入子进程的环境变量（如真实模型 key：QWEN_API_KEY/DEEPSEEK_API_KEY/…）。</summary>
    public IReadOnlyDictionary<string, string> ExtraEnvironmentVariables { get; init; } =
        new Dictionary<string, string>();

    /// <summary>启动等待上限：进程拉起 + 健康探活通过的总时长。</summary>
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(90);

    /// <summary>启动期健康轮询间隔。</summary>
    public TimeSpan HealthPollInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>watchdog 开关：进程意外退出后是否自动重启。</summary>
    public bool WatchdogEnabled { get; init; } = true;

    /// <summary>watchdog 连续重启上限（耗尽即 Failed，不再假装能自愈）。</summary>
    public int MaxRestartAttempts { get; init; } = 3;

    /// <summary>重启前退避等待。</summary>
    public TimeSpan RestartBackoff { get; init; } = TimeSpan.FromSeconds(2);
}

/// <summary>已拉起的网关进程句柄（真实实现包 System.Diagnostics.Process；测试用假进程）。</summary>
public interface IGatewayProcessHandle
{
    int ProcessId { get; }

    bool HasExited { get; }

    /// <summary>退出码；未退出为 null。</summary>
    int? ExitCode { get; }

    /// <summary>进程输出尾部（诊断用；无捕获为 null）。</summary>
    string? OutputTail { get; }

    /// <summary>等待进程退出；token 取消时抛 <see cref="OperationCanceledException"/>。</summary>
    Task WaitForExitAsync(CancellationToken ct);

    /// <summary>杀死进程（真实实现为整棵进程树）。尽力而为，不抛出。</summary>
    void Kill();
}

/// <summary>网关进程拉起结果。</summary>
public sealed record GatewayLaunchResult(bool IsSuccess, IGatewayProcessHandle? Process, string? Error)
{
    public static GatewayLaunchResult Ok(IGatewayProcessHandle process) => new(true, process, null);
    public static GatewayLaunchResult Fail(string error) => new(false, null, error);
}

/// <summary>拉起规格（由 sidecar 组装，launcher 只负责真实执行）。</summary>
public sealed record GatewayLaunchSpec(
    string FileName,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory,
    IReadOnlyDictionary<string, string> EnvironmentVariables,
    Uri HealthUrl);

/// <summary>网关进程拉起器抽象（生产 = <see cref="SystemProcessGatewayLauncher"/>；测试 = 假拉起器）。</summary>
public interface IGatewayProcessLauncher
{
    Task<GatewayLaunchResult> LaunchAsync(GatewayLaunchSpec spec, CancellationToken ct);
}

/// <summary>
/// 基于 <see cref="Process"/> 的真实拉起器：重定向 stdout/stderr 并环形缓冲尾部
/// （供失败诊断），<see cref="IGatewayProcessHandle.Kill"/> 杀整棵进程树
/// （uvicorn 可能派生 worker）。解释器不存在等 Win32 错误如实返回失败结果。
/// </summary>
public sealed class SystemProcessGatewayLauncher : IGatewayProcessLauncher
{
    private const int OutputTailLimit = 4_000;

    public Task<GatewayLaunchResult> LaunchAsync(GatewayLaunchSpec spec, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(spec);

        var psi = new ProcessStartInfo
        {
            FileName = spec.FileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in spec.Arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        if (!string.IsNullOrWhiteSpace(spec.WorkingDirectory))
        {
            psi.WorkingDirectory = spec.WorkingDirectory;
        }

        foreach (var (key, value) in spec.EnvironmentVariables)
        {
            psi.Environment[key] = value;
        }

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return Task.FromResult(GatewayLaunchResult.Fail(
                $"failed to start '{spec.FileName}': {ex.Message}"));
        }

        if (process is null)
        {
            return Task.FromResult(GatewayLaunchResult.Fail(
                $"failed to start '{spec.FileName}': Process.Start returned null"));
        }

        return Task.FromResult<GatewayLaunchResult>(
            GatewayLaunchResult.Ok(new SystemProcessHandle(process)));
    }

    private sealed class SystemProcessHandle : IGatewayProcessHandle
    {
        private readonly Process _process;
        private readonly object _tailLock = new();
        private readonly StringBuilder _tail = new();

        public SystemProcessHandle(Process process)
        {
            _process = process;
            _process.OutputDataReceived += (_, e) => AppendLine(e.Data);
            _process.ErrorDataReceived += (_, e) => AppendLine(e.Data);
            try
            {
                _process.BeginOutputReadLine();
                _process.BeginErrorReadLine();
            }
            catch (InvalidOperationException)
            {
                // 进程在挂事件期间已退出：尾部为空即可，不影响生命周期语义。
            }
        }

        public int ProcessId
        {
            get
            {
                try { return _process.Id; }
                catch (InvalidOperationException) { return -1; }
            }
        }

        public bool HasExited
        {
            get
            {
                try { return _process.HasExited; }
                catch (InvalidOperationException) { return true; }
            }
        }

        public int? ExitCode
        {
            get
            {
                try { return _process.HasExited ? _process.ExitCode : null; }
                catch (InvalidOperationException) { return null; }
            }
        }

        public string? OutputTail
        {
            get { lock (_tailLock) { return _tail.Length == 0 ? null : _tail.ToString(); } }
        }

        public Task WaitForExitAsync(CancellationToken ct) => _process.WaitForExitAsync(ct);

        public void Kill()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // 已退出或句柄失效：Kill 的语义是"确保它不在"，目的已达成。
            }
            finally
            {
                try { _process.Dispose(); } catch { /* 尽力释放句柄 */ }
            }
        }

        private void AppendLine(string? line)
        {
            if (line is null)
            {
                return;
            }

            lock (_tailLock)
            {
                _tail.AppendLine(line);
                if (_tail.Length > OutputTailLimit)
                {
                    _tail.Remove(0, _tail.Length - OutputTailLimit);
                }
            }
        }
    }
}

/// <summary>
/// moa-gateway-pro 网关进程的生命周期管理：真实拉起 Python/uvicorn 进程 →
/// 健康探活轮询（<c>GET /health</c> 通才算就绪）→ watchdog 检测意外退出并按退避重启。
/// 诚实性铁律：Python 不可用、启动失败、探活超时、进程退出——任何一种情况都
/// <c>LogWarning("[DEGRADED] …")</c> 且 <see cref="IsAvailable"/>=false，绝不假装在运行。
/// </summary>
public sealed class GatewaySidecar : IAsyncDisposable
{
    private readonly MoaGatewayClient _client;
    private readonly IGatewayProcessLauncher _launcher;
    private readonly GatewaySidecarOptions _options;
    private readonly ILogger<GatewaySidecar> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IGatewayProcessHandle? _process;
    private CancellationTokenSource? _watchdogCts;
    private Task? _watchdogTask;
    private bool _disposed;

    /// <summary>
    /// 构造。健康探活复用 <paramref name="client"/>（其 BaseUrl 必须与
    /// <paramref name="options"/> 的 Host/Port 一致，由组合根保证）。
    /// </summary>
    public GatewaySidecar(
        MoaGatewayClient client,
        GatewaySidecarOptions options,
        IGatewayProcessLauncher? launcher = null,
        ILogger<GatewaySidecar>? logger = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _launcher = launcher ?? new SystemProcessGatewayLauncher();
        _logger = logger ?? NullLogger<GatewaySidecar>.Instance;
    }

    public GatewaySidecarState State { get; private set; } = GatewaySidecarState.Stopped;

    /// <summary>进程存活且已通过启动探活（watchdog 重启成功后同样恢复 true）。</summary>
    public bool IsAvailable => State == GatewaySidecarState.Running && _process is { HasExited: false };

    /// <summary>最近一次失败/降级原因（供 UI 徽标与日志追溯）。</summary>
    public string? LastError { get; private set; }

    /// <summary>watchdog 已执行的自动重启次数。</summary>
    public int RestartCount { get; private set; }

    /// <summary>当前网关进程 Id；未运行为 null。</summary>
    public int? ProcessId => _process is { HasExited: false } p ? p.ProcessId : null;

    /// <summary>状态变化通知（UI 网关徽标订阅）。</summary>
    public event Action<GatewaySidecarState>? StateChanged;

    /// <summary>
    /// 拉起网关并等待健康就绪。成功返回 true（<see cref="State"/>=Running）；
    /// 任何失败返回 false 并置 [DEGRADED]/Failed 语义——绝不静默冒充在运行。
    /// </summary>
    public async Task<bool> StartAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(ct);
        try
        {
            if (State is GatewaySidecarState.Running or GatewaySidecarState.Starting)
            {
                return State == GatewaySidecarState.Running;
            }

            // 从 Degraded/Failed 态重启时，旧 watchdog 与旧进程必须先清场，否则可能
            // 拉起第二个 uvicorn 导致端口冲突，并把原进程孤儿化（StopAsync 再杀不到）。
            if (_watchdogTask is not null)
            {
                StopWatchdog();
                try { await _watchdogTask; } catch { /* 旧 watchdog 收尾异常不影响重启 */ }
                _watchdogTask = null;
            }

            if (_process is { HasExited: false } oldProcess)
            {
                _logger.LogWarning(
                    "[DEGRADED] MOA gateway sidecar restart requested while old process still alive (pid {Pid}); killing it before relaunch.",
                    oldProcess.ProcessId);
                oldProcess.Kill();
            }

            _process = null;
            Transition(GatewaySidecarState.Starting, error: null);
            var launched = await LaunchCoreAsync(ct);
            if (!launched.IsSuccess)
            {
                _logger.LogWarning(
                    "[DEGRADED] MOA gateway sidecar failed to launch: {Error}. In-process orchestration remains the fallback.",
                    launched.Error);
                Transition(GatewaySidecarState.Failed, launched.Error);
                return false;
            }

            _process = launched.Process;
            var proc = _process!;
            bool ready;
            try
            {
                ready = await WaitHealthyAsync(proc, _options.StartupTimeout, ct);
            }
            catch (OperationCanceledException)
            {
                // 启动被取消：回收刚拉起的进程，不留孤儿，再如实向上抛取消。
                proc.Kill();
                _process = null;
                Transition(GatewaySidecarState.Stopped, "startup cancelled");
                throw;
            }

            if (!ready)
            {
                var tail = proc.OutputTail;
                proc.Kill();
                _process = null;
                var reason = $"gateway did not become healthy within {_options.StartupTimeout.TotalSeconds:0.#}s"
                             + (tail is null ? string.Empty : $"; output tail: {tail}");
                _logger.LogWarning("[DEGRADED] MOA gateway sidecar startup probe failed: {Reason}", reason);
                Transition(GatewaySidecarState.Failed, reason);
                return false;
            }

            Transition(GatewaySidecarState.Running, error: null);
            _logger.LogInformation(
                "MOA gateway sidecar running (pid {Pid}, {Url})",
                _process!.ProcessId, _client.Options.BaseUrl);

            if (_options.WatchdogEnabled)
            {
                StartWatchdog();
            }

            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>停止网关：先收拢 watchdog（防止停止期间并发重启孤儿进程），再杀整棵进程树。幂等。</summary>
    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try
        {
            var watchdog = _watchdogTask;
            StopWatchdog();
            if (watchdog is not null)
            {
                try { await watchdog; } catch { /* watchdog 收尾异常不影响停止 */ }
                _watchdogTask = null;
            }

            if (_process is not null)
            {
                _process.Kill();
                _process = null;
            }

            Transition(GatewaySidecarState.Stopped, error: null);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 按需探活（UI/门面对可用性的即时核实）。进程不在或健康检查失败 → false，
    /// 且把状态如实改为 Degraded（进程存活但探活失败时）；探活成功且此前为 Degraded
    /// 则恢复为 Running，避免门面长期误回退。
    /// </summary>
    public async Task<bool> ProbeAsync(CancellationToken ct = default)
    {
        var process = _process;
        if (State == GatewaySidecarState.Stopped || process is null || process.HasExited)
        {
            return false;
        }

        var health = await _client.HealthAsync(ct);
        if (health.IsSuccess)
        {
            if (State == GatewaySidecarState.Degraded)
            {
                Transition(GatewaySidecarState.Running, error: null);
                _logger.LogInformation(
                    "MOA gateway sidecar recovered to Running after probe succeeded (pid {Pid}).",
                    process.ProcessId);
            }

            return true;
        }

        if (State == GatewaySidecarState.Running)
        {
            var reason = $"health probe failed: {health.Error}";
            _logger.LogWarning("[DEGRADED] MOA gateway health probe failed: {Error}", health.Error);
            Transition(GatewaySidecarState.Degraded, reason);
        }

        return false;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopWatchdog();
        _process?.Kill();
        _process = null;
        if (_watchdogTask is not null)
        {
            try { await _watchdogTask; } catch { /* watchdog 收尾异常不影响释放 */ }
        }

        _gate.Dispose();
    }

    // ---------------- 内部实现 ----------------

    private async Task<GatewayLaunchResult> LaunchCoreAsync(CancellationToken ct)
    {
        var spec = new GatewayLaunchSpec(
            FileName: _options.PythonExecutable,
            Arguments: new[]
            {
                "-m", "uvicorn", "moa_gateway.server:app",
                "--host", _options.Host,
                "--port", _options.Port.ToString(),
            },
            WorkingDirectory: _options.WorkingDirectory,
            EnvironmentVariables: BuildEnvironment(),
            HealthUrl: new Uri(_client.Options.BaseUrl, "/health"));

        _logger.LogInformation(
            "Starting MOA gateway: {Python} {Args} (health: {HealthUrl})",
            spec.FileName, string.Join(' ', spec.Arguments), spec.HealthUrl);

        return await _launcher.LaunchAsync(spec, ct);
    }

    private Dictionary<string, string> BuildEnvironment()
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(_options.AdminPassword))
        {
            env["MOA_ADMIN_PASSWORD"] = _options.AdminPassword!;
        }

        if (!string.IsNullOrWhiteSpace(_options.GatewayApiKey))
        {
            env["MOA_GATEWAY_KEY"] = _options.GatewayApiKey!;
        }

        foreach (var (key, value) in _options.ExtraEnvironmentVariables)
        {
            env[key] = value;
        }

        return env;
    }

    /// <summary>轮询 /health 直到成功、超时或进程提前退出（提前退出时带上输出尾部诊断）。</summary>
    private async Task<bool> WaitHealthyAsync(
        IGatewayProcessHandle process, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!ct.IsCancellationRequested && DateTimeOffset.UtcNow < deadline)
        {
            if (process.HasExited)
            {
                LastError = $"gateway process exited during startup (code {process.ExitCode?.ToString() ?? "?"})"
                            + (process.OutputTail is null ? string.Empty : $"; output tail: {process.OutputTail}");
                return false;
            }

            var health = await _client.HealthAsync(ct);
            if (health.IsSuccess)
            {
                return true;
            }

            await Task.Delay(_options.HealthPollInterval, ct);
        }

        return false;
    }

    private void StartWatchdog()
    {
        StopWatchdog();
        _watchdogCts = new CancellationTokenSource();
        var token = _watchdogCts.Token;
        _watchdogTask = Task.Run(() => WatchdogLoopAsync(token), token);
    }

    private void StopWatchdog()
    {
        _watchdogCts?.Cancel();
        _watchdogCts?.Dispose();
        _watchdogCts = null;
    }

    private async Task WatchdogLoopAsync(CancellationToken token)
    {
        var consecutiveRestarts = 0;
        while (!token.IsCancellationRequested)
        {
            var watched = _process;
            if (watched is null)
            {
                return; // StopAsync 已接管。
            }

            try
            {
                await watched.WaitForExitAsync(token);
            }
            catch (OperationCanceledException)
            {
                return; // 正常停止：不重启。
            }

            if (token.IsCancellationRequested)
            {
                return;
            }

            // ---- 意外退出：显式降级 + 有限重启 ----
            consecutiveRestarts++;
            var reason = $"gateway process (pid {watched.ProcessId}) exited unexpectedly " +
                         $"(code {watched.ExitCode?.ToString() ?? "?"})";
            _logger.LogWarning(
                "[DEGRADED] MOA {Reason}; restart attempt {Attempt}/{Max}.",
                reason, consecutiveRestarts, _options.MaxRestartAttempts);
            Transition(GatewaySidecarState.Degraded, reason);

            if (consecutiveRestarts > _options.MaxRestartAttempts)
            {
                var exhausted = $"watchdog exhausted {_options.MaxRestartAttempts} restart attempts; gateway left down";
                _logger.LogWarning("[DEGRADED] MOA gateway {Exhausted}.", exhausted);
                Transition(GatewaySidecarState.Failed, exhausted);
                return;
            }

            try
            {
                await Task.Delay(_options.RestartBackoff, token);

                var relaunched = await LaunchCoreAsync(token);
                if (token.IsCancellationRequested)
                {
                    // 停止与重启竞争：以停止为准，回收刚拉起的进程，不留孤儿。
                    relaunched.Process?.Kill();
                    return;
                }

                if (!relaunched.IsSuccess)
                {
                    _logger.LogWarning(
                        "[DEGRADED] MOA gateway relaunch failed: {Error}", relaunched.Error);
                    Transition(GatewaySidecarState.Degraded, relaunched.Error);
                    continue; // 旧句柄已退出，下一轮循环消耗一次重试预算。
                }

                _process = relaunched.Process;
                var ready = await WaitHealthyAsync(_process!, _options.StartupTimeout, token);
                if (ready)
                {
                    RestartCount++;
                    consecutiveRestarts = 0; // 连续重启计数在成功恢复后清零，符合"连续"语义。
                    Transition(GatewaySidecarState.Running, error: null);
                    _logger.LogInformation(
                        "MOA gateway sidecar recovered (pid {Pid}) after restart {Count}",
                        _process!.ProcessId, RestartCount);
                }
                else
                {
                    // 保留已杀句柄：下一轮循环照常消耗重试预算，而不是悄悄放弃。
                    _process!.Kill();
                    _logger.LogWarning(
                        "[DEGRADED] MOA gateway relaunch did not become healthy within {Timeout}s",
                        _options.StartupTimeout.TotalSeconds);
                }
            }
            catch (OperationCanceledException)
            {
                return; // 正常停止：探活/退避被取消，不重启。
            }
        }
    }

    private readonly object _stateLock = new();

    private void Transition(GatewaySidecarState next, string? error)
    {
        // watchdog 循环与 Start/Stop 调用方并发触发状态迁移：
        // check-then-set 必须在同一锁内原子完成，避免丢失/乱序写入。
        GatewaySidecarState changedTo;
        lock (_stateLock)
        {
            if (error is not null)
            {
                LastError = error;
            }

            if (State == next)
            {
                return;
            }

            State = next;
            changedTo = next;
        }

        // 事件在锁外派发：订阅者异常与耗时不得干扰生命周期管理，也不得反向死锁。
        try
        {
            StateChanged?.Invoke(changedTo);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("gateway sidecar state subscriber threw: {Error}", ex.Message);
        }
    }
}
