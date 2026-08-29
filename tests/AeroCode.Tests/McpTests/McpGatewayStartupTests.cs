// Copyright (c) AeroCode V3.0
// McpGateway 启动与超时路径测试：不需要真实 MCP 服务器即可验证的失败语义。
// 异常类型依据 ModelContextProtocol 1.0.0 实际行为（stdio 传输启动失败抛
// IOException("Failed to start MCP server process.")，握手超时抛 TimeoutException）。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AeroCode.Mcp.Client;
using Xunit;

namespace AeroCode.Tests.McpTests;

/// <summary>
/// McpGateway 启动期行为测试（与 McpRealProcessE2ETests 的诚实门控同理，不伪造通过）：
/// 1) 指向不存在的命令 → 首次调用立即抛出可观察异常，不静默、不挂起，且不缓存坏客户端；
/// 2) InitializationTimeout 可注入生效：200ms 超时 + 永不完成握手的静默进程 → 远小于默认 30s 即失败；
/// 3) InitializationTimeout / CallTimeout 的默认值与可注入性。
/// 说明：CallTimeout 的运行时触发路径（RunWithCallTimeoutAsync）只在连接成功后的
/// 工具调用/发现上生效；McpGateway 为 sealed 且 McpClient 由 SDK 内部创建，
/// 无法在不连真实 MCP server 的情况下注入假客户端来触发"已连接但工具挂死"，
/// 因此此处只断言属性语义，不为它造假路径。真实进程的发现/调用/重连路径由
/// McpRealProcessE2ETests 覆盖（"已连接服务器上的工具挂死"目前尚无 E2E 用例，如实留白）。
/// </summary>
public sealed class McpGatewayStartupTests
{
    /// <summary>遍历异常链（含 AggregateException 展开），寻找满足条件的异常。</summary>
    private static bool ChainContains(Exception ex, Func<Exception, bool> predicate)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (predicate(e)) return true;
            if (e is AggregateException agg)
            {
                foreach (var inner in agg.InnerExceptions)
                {
                    if (ChainContains(inner, predicate)) return true;
                }
            }
        }

        return false;
    }

    /// <summary>把异常链拍平成字符串，便于断言失败时定位 SDK 行为变化。</summary>
    private static string DumpChain(Exception ex)
    {
        var parts = new List<string>();
        for (var e = ex; e is not null; e = e.InnerException)
        {
            parts.Add($"{e.GetType().FullName}: {e.Message}");
        }

        return string.Join(" ---> ", parts);
    }

    /// <summary>
    /// 不存在的命令：stdio 传输无法拉起可用的 MCP 服务器进程，首次 ListToolsAsync
    /// 必须立即抛出可观察异常。SDK 1.0.0 实测有两种诚实的失败签名（随平台/解析方式）：
    /// 直接 spawn 失败 → IOException("Failed to start MCP server process.")；
    /// Windows 下命令经 shell 解析、shell 以 exit code 1 报告命令不存在 →
    /// IOException("MCP server process exited unexpectedly (exit code: 1)")（带 stderr 尾巴）。
    /// 两者都满足"立即可观察"契约，故都接受；同时注入 5s InitializationTimeout 兜底：
    /// 即便未来 SDK 改为吞掉启动错误等握手，也被限在 5s 内。
    /// 另断言二次调用仍失败：连接失败后 _client 保持 null，不缓存坏状态。
    /// </summary>
    [Fact]
    public async Task NonexistentCommand_FirstCallFailsImmediatelyAndObservably()
    {
        var config = new McpServerConfig
        {
            Id = "nonexistent-test",
            Command = "nonexistent-aerocode-cmd-xyz",
        };
        await using var gateway = new McpGateway(config)
        {
            InitializationTimeout = TimeSpan.FromSeconds(5),
        };

        var sw = Stopwatch.StartNew();
        var ex = await Record.ExceptionAsync(() => gateway.ListToolsAsync());
        sw.Stop();

        Assert.NotNull(ex);
        Assert.False(ex is OperationCanceledException,
            "未传入任何取消令牌，不应出现取消异常");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10),
            $"进程启动失败应立即返回，实际耗时 {sw.Elapsed}");
        Assert.True(
            ChainContains(ex!, e => e is IOException io
                && (io.Message.Contains("Failed to start MCP server process", StringComparison.Ordinal)
                    || io.Message.Contains("MCP server process exited unexpectedly", StringComparison.Ordinal))),
            $"异常链中未找到预期的进程启动失败根因，实际异常链：{DumpChain(ex!)}");

        // 失败不被缓存：第二次调用重新尝试启动并同样失败（而不是返回上次的坏客户端或永久阻塞）
        var ex2 = await Record.ExceptionAsync(() => gateway.ListToolsAsync());
        Assert.NotNull(ex2);
    }

    /// <summary>
    /// InitializationTimeout 注入生效：子进程能启动但永不完成 stdio MCP 握手
    /// （Windows 用 powershell Start-Sleep，其余平台用 sleep，全程静默）。
    /// 注入 200ms 后必须抛 TimeoutException("Initialization timed out")，
    /// 整体远小于网关默认 30s——若注入无效，将等到 SDK 默认 60s，用例必然超时失败。
    /// 注：握手失败后 _client 为 null，本测试结束时网关 Dispose 不会杀子进程；
    /// 假进程自身在 30s 内退出，不残留。
    /// </summary>
    [Fact]
    public async Task InitializationTimeout_Injected_FailsFastAgainstSilentProcess()
    {
        var (command, arguments) = OperatingSystem.IsWindows()
            ? ("powershell.exe", new List<string> { "-NoProfile", "-Command", "Start-Sleep -Seconds 30" })
            : ("sleep", new List<string> { "30" });
        var config = new McpServerConfig
        {
            Id = "silent-hang-test",
            Command = command,
            Arguments = arguments,
        };
        await using var gateway = new McpGateway(config)
        {
            InitializationTimeout = TimeSpan.FromMilliseconds(200),
        };

        // 防御性令牌：注入万一失效时，用例在 20s 内以取消失败，而不是空等 SDK 默认 60s。
        using var safetyNet = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var sw = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<TimeoutException>(
            () => gateway.ListToolsAsync(safetyNet.Token));
        sw.Stop();

        Assert.Contains("Initialization timed out", ex.Message);
        // 200ms 注入 + 进程启动开销，正常 ~1-2s；给足 CI 抖动余量，
        // 但仍远低于默认 30s——注入不生效时该断言必然失败。
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10),
            $"握手超时应由注入的 200ms 决定，实际耗时 {sw.Elapsed}，疑似 InitializationTimeout 注入未生效");
    }

    /// <summary>
    /// 超时属性的默认值与可注入性：
    /// InitializationTimeout 默认 30s、CallTimeout 默认 2min，均可经 init 属性覆写。
    /// CallTimeout 的实际触发（已连接服务器上工具无响应 → TimeoutException）见类注释：
    /// 无法在不连真实 MCP server 时诚实触发，故不造假断言；真实进程路径由
    /// McpRealProcessE2ETests 覆盖。
    /// </summary>
    [Fact]
    public void TimeoutProperties_HaveDocumentedDefaults_AndAreInjectable()
    {
        var config = new McpServerConfig { Id = "timeout-props", Command = "dotnet" };

        var gateway = new McpGateway(config);
        Assert.Equal(TimeSpan.FromSeconds(30), gateway.InitializationTimeout);
        Assert.Equal(TimeSpan.FromMinutes(2), gateway.CallTimeout);
        Assert.Equal("timeout-props", gateway.ServerId);

        var injected = new McpGateway(config)
        {
            InitializationTimeout = TimeSpan.FromMilliseconds(200),
            CallTimeout = TimeSpan.FromSeconds(5),
        };
        Assert.Equal(TimeSpan.FromMilliseconds(200), injected.InitializationTimeout);
        Assert.Equal(TimeSpan.FromSeconds(5), injected.CallTimeout);
    }
}
