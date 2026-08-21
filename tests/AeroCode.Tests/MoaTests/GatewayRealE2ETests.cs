using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AeroAgent.Moa.Gateway;
using Xunit;

namespace AeroCode.Tests.MoaTests;

/// <summary>
/// 真实 moa-gateway-pro 网关 E2E：真实拉起 Python/uvicorn 进程（GatewaySidecar 真实拉起器）
/// → /health 探活 → /api/auth/login 登录拿 JWT → /v1/moa/execute（Bearer JWT）→
/// 断言无模型 key 环境下的 D6 显式 mock 标注（X-MOA-Mock 头 + body.mock 双通道一致）→ 停止进程。
///
/// 确定性说明（本机实测的 vendor 行为）：网关的 discovery 子系统默认会自动注册数十个
/// 免费真实端点，导致"无 key"环境也可能走真实模型。为把本测试钉在"无 key → 显式 mock"
/// 这一验收语义上，测试在启动前把 site-packages/config.yaml 备份并替换为 4 个 mock 端点
/// + 关闭 discovery 的最小配置，结束后如实恢复原文件。
///
/// 门控：仅当 AEROCODE_RUN_GATEWAY_TESTS=1 且本机存在装有 moa_gateway 的 Python 时运行
/// （scripts/gateway/setup_gateway.ps1 install 创建的 venv 或 AEROCODE_GATEWAY_PYTHON 指定）。
/// </summary>
public sealed class GatewayRealE2ETests
{
    private const string GateEnvVar = "AEROCODE_RUN_GATEWAY_TESTS";

    /// <summary>4 个无 key 端点（api_key_env 指向不存在的变量）+ 关闭 discovery 的确定性配置。</summary>
    private const string DeterministicTestConfig = """
        # AeroCode T2 E2E deterministic config (test fixture; restored after the test)
        auth:
          gateway_api_keys: []
          admin_username: admin
          admin_password: ''
        models:
        - id: qwen3-mock
          provider: openai
          model: qwen3-235b-a22b
          tier: standard
          api_base: https://dashscope.aliyuncs.com/compatible-mode/v1
          api_key_env: QWEN_API_KEY
          enabled: true
        - id: glm4-mock
          provider: zhipu
          model: glm-4-flash
          tier: lite
          api_base: https://open.bigmodel.cn/api/paas/v4
          api_key_env: ZHIPU_API_KEY
          enabled: true
        - id: deepseek-mock
          provider: deepseek
          model: deepseek-chat
          tier: standard
          api_base: https://api.deepseek.com/v1
          api_key_env: DEEPSEEK_API_KEY
          enabled: true
        - id: kimi-mock
          provider: moonshot
          model: kimi-k2-0711-preview
          tier: premium
          api_base: https://api.moonshot.cn/v1
          api_key_env: MOONSHOT_API_KEY
          enabled: true
        mock:
          mode: explicit
        discovery:
          enabled: false
          auto_configure: false
        moa:
          enabled: true
          default_preset: balanced
          presets:
            fast:
              enabled: true
              strategy: single
              reference_count: 1
              critic_rounds: 0
              tier: lite
              description: E2E deterministic mock preset
            balanced:
              enabled: true
              strategy: parallel
              reference_count: 3
              aggregator_tier: premium
              critic_rounds: 1
              description: E2E deterministic mock preset
        """;

    [SkippableFact]
    public async Task RealGateway_StartLoginExecuteMockAnnotated_Stop()
    {
        Skip.IfNot(
            Environment.GetEnvironmentVariable(GateEnvVar) == "1",
            $"set {GateEnvVar}=1 to run the real-gateway E2E (requires scripts/gateway/setup_gateway.ps1 install)");

        var python = ResolveGatewayPython(out var resolveNote);
        Skip.If(python is null, resolveNote);

        var port = GetFreePort();
        var baseUrl = new Uri($"http://127.0.0.1:{port}");
        // ≥8 位且含字母+数字（网关 bootstrap 的强度校验）
        var adminPassword = "AeroCodeT2-" + Guid.NewGuid().ToString("N");

        var rootDir = GetGatewayRootDir(python!);
        Skip.If(rootDir is null, "cannot resolve moa_gateway ROOT_DIR");
        var configPath = Path.Combine(rootDir!, "config.yaml");
        var configBackup = configPath + ".t2bak";

        // 确定性夹具：备份现有配置 → 写入 mock-only 配置；清空陈旧库让本次密码生效
        if (File.Exists(configPath))
        {
            File.Copy(configPath, configBackup, overwrite: true);
        }

        File.WriteAllText(configPath, DeterministicTestConfig, Encoding.UTF8);
        TryDeleteStaleGatewayDb(rootDir!);

        var sidecarOptions = new GatewaySidecarOptions
        {
            PythonExecutable = python!,
            Host = "127.0.0.1",
            Port = port,
            AdminPassword = adminPassword,
            StartupTimeout = TimeSpan.FromSeconds(180),
            HealthPollInterval = TimeSpan.FromMilliseconds(500),
            WatchdogEnabled = false, // E2E 只验证单次拉起链路
            // 即使开发机环境里有真实 key，也强制置空 → 端点必然 mock-backed（确定性）
            ExtraEnvironmentVariables = new Dictionary<string, string>
            {
                ["QWEN_API_KEY"] = string.Empty,
                ["ZHIPU_API_KEY"] = string.Empty,
                ["DEEPSEEK_API_KEY"] = string.Empty,
                ["MOONSHOT_API_KEY"] = string.Empty,
            },
        };
        var clientOptions = new MoaGatewayClientOptions
        {
            BaseUrl = baseUrl,
            Timeout = TimeSpan.FromSeconds(120),
            HealthTimeout = TimeSpan.FromSeconds(3),
        };

        using var client = new MoaGatewayClient(clientOptions);
        await using var sidecar = new GatewaySidecar(client, sidecarOptions); // 真实 SystemProcessGatewayLauncher
        try
        {
            // ---- 1. 真实启动 ----
            var started = await sidecar.StartAsync();
            Assert.True(started, $"gateway failed to start: {sidecar.LastError}");
            Assert.Equal(GatewaySidecarState.Running, sidecar.State);
            Assert.True(sidecar.IsAvailable);
            Assert.NotNull(sidecar.ProcessId);

            // ---- 2. 健康探活（真实 HTTP）----
            var health = await client.HealthAsync();
            Assert.True(health.IsSuccess, health.Error);
            Assert.Equal("ok", health.Value!.Status);
            Assert.Equal("3.1.1", health.Value.Version);
            // 确定性配置：恰好 4 个端点，全部显式 mock（D6 可见性）
            Assert.Equal(4, health.Value.EndpointsTotal);
            Assert.Equal(4, health.Value.MockEndpointsCount);
            Assert.Equal(0, health.Value.RealEndpointsCount);
            Assert.Equal("explicit", health.Value.MockMode);

            // ---- 3. 登录拿 JWT（真实 /api/auth/login）----
            using var http = new HttpClient { BaseAddress = baseUrl, Timeout = TimeSpan.FromSeconds(30) };
            var loginResponse = await http.PostAsJsonAsync(
                "/api/auth/login", new { username = "admin", password = adminPassword });
            var loginBody = await loginResponse.Content.ReadAsStringAsync();
            Assert.True(
                HttpStatusCode.OK == loginResponse.StatusCode,
                $"login HTTP {(int)loginResponse.StatusCode}: {loginBody}");
            using var loginJson = JsonDocument.Parse(loginBody);
            var token = loginJson.RootElement.GetProperty("token").GetString();
            Assert.False(string.IsNullOrWhiteSpace(token));
            Assert.Equal("bearer", loginJson.RootElement.GetProperty("token_type").GetString());

            // ---- 4. 真实 execute（Bearer JWT；无 key → MockProvider 显式标注）----
            using var authedClient = new MoaGatewayClient(clientOptions with { ApiKey = token });
            var executed = await authedClient.ExecuteAsync(new MoaGatewayExecuteRequest
            {
                Query = "AeroCode T2 E2E: reply with one short sentence.",
                Preset = "fast",
            });

            Assert.True(executed.IsSuccess, executed.Error);
            Assert.Equal(200, executed.StatusCode);
            Assert.True(executed.Value!.Mock, "no model keys → result must be explicitly mock (body.mock)");
            Assert.True(executed.IsMock, "X-MOA-Mock header / body mock must surface as IsMock");
            Assert.False(string.IsNullOrWhiteSpace(executed.Value.FinalContent));
            Assert.NotEmpty(executed.Value.References);
            Assert.False(string.IsNullOrWhiteSpace(executed.Value.RequestId));
            Assert.True(executed.Value.TotalLatencyMs >= 0);
        }
        finally
        {
            // ---- 5. 必须停掉真实进程并恢复配置 ----
            await sidecar.StopAsync();
            try
            {
                if (File.Exists(configBackup))
                {
                    File.Move(configBackup, configPath, overwrite: true);
                }
                else
                {
                    File.Delete(configPath);
                }
            }
            catch (IOException)
            {
                // 恢复失败不吞测试结果：上面的断言已如实判定，此处仅尽力还原现场。
            }
        }

        Assert.Equal(GatewaySidecarState.Stopped, sidecar.State);
        Assert.Null(sidecar.ProcessId);
        AssertPortReleased(port);
    }

    // ---------------- 辅助 ----------------

    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AeroCode.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }

    /// <summary>定位装有 moa_gateway 的 Python：优先 AEROCODE_GATEWAY_PYTHON，其次部署脚本建的 venv。</summary>
    private static string? ResolveGatewayPython(out string note)
    {
        var candidates = new List<string>();
        var explicitPython = Environment.GetEnvironmentVariable("AEROCODE_GATEWAY_PYTHON");
        if (!string.IsNullOrWhiteSpace(explicitPython))
        {
            candidates.Add(explicitPython);
        }

        var repoRoot = FindRepoRoot();
        if (repoRoot is not null)
        {
            candidates.Add(Path.Combine(repoRoot, "scripts", "gateway", ".venv", "Scripts", "python.exe"));
        }

        foreach (var candidate in candidates)
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            if (RunPython(candidate, "import moa_gateway", out _) == 0)
            {
                note = string.Empty;
                return candidate;
            }
        }

        note = "no Python with moa_gateway found (run scripts/gateway/setup_gateway.ps1 install " +
               "or set AEROCODE_GATEWAY_PYTHON)";
        return null;
    }

    /// <summary>网关 ROOT_DIR（vendor 契约：包目录的父目录；config.yaml 与 data/ 均在此）。</summary>
    private static string? GetGatewayRootDir(string python)
    {
        var exit = RunPython(
            python,
            "import moa_gateway, pathlib; print(pathlib.Path(moa_gateway.__file__).resolve().parent.parent)",
            out var rootDir);
        return exit == 0 && !string.IsNullOrWhiteSpace(rootDir) ? rootDir.Trim() : null;
    }

    /// <summary>
    /// 网关 admin 用户仅在首次启动播种（vendor 契约），旧库会让本次 MOA_ADMIN_PASSWORD 失效——
    /// 删除陈旧 data/config.db 保证 E2E 确定性。
    /// </summary>
    private static void TryDeleteStaleGatewayDb(string rootDir)
    {
        var dbPath = Path.Combine(rootDir, "data", "config.db");
        if (File.Exists(dbPath))
        {
            try
            {
                File.Delete(dbPath);
            }
            catch (IOException)
            {
                // 尽力而为：删不掉时登录会因旧密码失败，测试如实断言失败而非伪造通过。
            }
        }
    }

    private static int RunPython(string python, string code, out string stdout)
    {
        var psi = new ProcessStartInfo
        {
            FileName = python,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(code);
        using var process = Process.Start(psi);
        if (process is null)
        {
            stdout = string.Empty;
            return -1;
        }

        var output = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        if (!process.WaitForExit(TimeSpan.FromSeconds(30)))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* 尽力 */ }
            stdout = string.Empty;
            return -1;
        }

        stdout = output;
        return process.ExitCode;
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static void AssertPortReleased(int port)
    {
        using var probe = new TcpClient();
        try
        {
            var connected = probe.ConnectAsync(IPAddress.Loopback, port).Wait(TimeSpan.FromSeconds(3));
            Assert.False(connected, $"port {port} still accepting connections after StopAsync");
        }
        catch (AggregateException ex) when (ex.InnerException is SocketException)
        {
            // 连接被拒绝 = 端口确已释放（停止后的预期状态），如实通过。
        }
    }
}
