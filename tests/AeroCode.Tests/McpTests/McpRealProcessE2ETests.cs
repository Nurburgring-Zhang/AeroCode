using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using AeroCode.App.Mcp;
using AeroCode.Mcp.Client;
using Xunit;

namespace AeroCode.Tests.McpTests;

/// <summary>
/// E2E 基础设施：定位 dotnet 宿主与 aerocode-mcp 构建产物。
/// 任一缺失 → 测试 SkippableFact 如实跳过，不伪造通过。
/// </summary>
internal static class McpE2E
{
    public static string? FindDotNetExe()
    {
        var exeName = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";

        var root = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrEmpty(root))
        {
            var candidate = Path.Combine(root, exeName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        // dotnet test 场景：当前进程宿主通常就是 dotnet 本身。
        var processHost = Environment.ProcessPath;
        if (processHost is not null
            && Path.GetFileName(processHost).StartsWith("dotnet", StringComparison.OrdinalIgnoreCase)
            && File.Exists(processHost))
        {
            return processHost;
        }

        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), exeName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // PATH 中的非法条目：跳过
            }
        }

        return null;
    }

    /// <summary>从测试输出目录上溯仓库根（AeroCode.sln 标记），取 Mcp 项目构建产物。</summary>
    public static string? FindMcpServerDll()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AeroCode.sln")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            return null;
        }

        // BaseDirectory = <repo>\tests\AeroCode.Tests\bin\<Configuration>\net9.0\
        // DirectoryInfo(BaseDirectory) 即 net9.0 目录，其 Parent 就是 Configuration 目录。
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Debug";
        var dll = Path.Combine(
            dir.FullName, "src", "AeroCode.Mcp", "bin", configuration, "net9.0", "aerocode-mcp.dll");
        return File.Exists(dll) ? dll : null;
    }

    public static McpServerConfig MakeConfig(string dotnetExe, string mcpDll, string dbPath, string serverId)
    {
        var config = new McpServerConfig
        {
            Id = serverId,
            Command = dotnetExe,
            Arguments = { "exec", mcpDll },
        };
        // 临时库隔离（不碰用户真实笔记）+ 宿主根（dotnet 装在非标准位置时必须）。
        config.EnvironmentVariables = new Dictionary<string, string>
        {
            ["AEROCODE_DB_PATH"] = dbPath,
            ["DOTNET_ROOT"] = Path.GetDirectoryName(dotnetExe)!,
        };
        return config;
    }

    /// <summary>wmic 枚举命令行含 aerocode-mcp.dll 的 dotnet 进程 PID（仅本仓构建产物路径）。</summary>
    public static List<int> FindServerProcessIds()
    {
        var pids = new List<int>();
        var wmic = Path.Combine(Environment.SystemDirectory, "wbem", "wmic.exe");
        if (!File.Exists(wmic))
        {
            return pids;
        }

        var output = RunCaptured(wmic, "process where \"name='dotnet.exe'\" get CommandLine,ProcessId /format:list");
        if (output is null)
        {
            return pids;
        }

        string? commandLine = null;
        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.Trim().TrimEnd('\r');
            if (line.StartsWith("CommandLine=", StringComparison.Ordinal))
            {
                commandLine = line["CommandLine=".Length..];
            }
            else if (line.StartsWith("ProcessId=", StringComparison.Ordinal))
            {
                if (commandLine is not null
                    && commandLine.Contains("aerocode-mcp.dll", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(line["ProcessId=".Length..], out var pid))
                {
                    pids.Add(pid);
                }

                commandLine = null;
            }
        }

        return pids;
    }

    /// <summary>taskkill 杀掉指定 PID。任一成功返回 true。</summary>
    public static bool KillProcessIds(IEnumerable<int> pids)
    {
        var any = false;
        foreach (var pid in pids)
        {
            var output = RunCaptured("taskkill", $"/F /PID {pid}");
            any |= output is not null && !output.Contains("ERROR", StringComparison.OrdinalIgnoreCase);
        }

        return any;
    }

    private static string? RunCaptured(string fileName, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi);
            if (process is null)
            {
                return null;
            }

            using var reader = new StreamReader(process.StandardOutput.BaseStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var text = reader.ReadToEnd();
            process.WaitForExit(15000);
            return text;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>
/// S5 真实进程 E2E：经官方 SDK stdio 传输拉起 aerocode-mcp 子进程，
/// 验证工具发现、真实 DB 往返、重启容错与自动重连。
/// 宿主/产物缺失时 SkippableFact 如实跳过。
/// </summary>
public sealed class McpRealProcessE2ETests : IDisposable
{
    private static readonly string[] ExpectedTools =
    {
        "list_notes", "get_note", "create_note", "update_note", "delete_note", "search_notes",
        "list_notebooks", "create_notebook", "list_tags", "set_note_tags", "get_notes_by_tag", "toggle_pin",
    };

    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(), "aerocode-mcp-e2e-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        // 子进程刚被终止时可能仍短暂持有 SQLite 文件句柄：重试几次，
        // 清理失败绝不让测试结论翻车。
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (Directory.Exists(_tempDir))
                {
                    Directory.Delete(_tempDir, recursive: true);
                }

                return;
            }
            catch (Exception) when (attempt < 2)
            {
                System.Threading.Thread.Sleep(150);
            }
            catch (Exception)
            {
                // 放弃清理：临时目录留在 %TEMP%，不影响测试结论
            }
        }
    }

    private string DbPath => Path.Combine(_tempDir, "e2e.db");

    /// <summary>list_notes 的业务错误以 "Error: ..." 文本返回且 IsError=false，
    /// 只断言 !IsError 会空过——必须解析出真实 JSON 与 count 字段。</summary>
    private static void AssertListNotesOk(McpCallOutcome outcome)
    {
        Assert.False(outcome.IsError, outcome.Text);
        using var doc = JsonDocument.Parse(outcome.Text);
        Assert.True(doc.RootElement.TryGetProperty("count", out _),
            $"list_notes 未返回真实结果：{outcome.Text}");
    }

    private (string Dotnet, string Dll)? RequireHost()
    {
        Directory.CreateDirectory(_tempDir);
        var dotnet = McpE2E.FindDotNetExe();
        var dll = McpE2E.FindMcpServerDll();
        if (dotnet is null || dll is null)
        {
            return null;
        }

        return (dotnet, dll);
    }

    [SkippableFact]
    public async Task RealProcess_ListTools_ReturnsThe12NoteTools()
    {
        var host = RequireHost();
        Skip.If(host is null, "未找到 dotnet 宿主或 aerocode-mcp.dll 构建产物，如实跳过 E2E");

        var config = McpE2E.MakeConfig(host!.Value.Dotnet, host.Value.Dll, DbPath, "aerocode");
        await using var gateway = new McpGateway(config);

        var tools = await gateway.ListToolsAsync();

        Assert.Equal(12, tools.Count);
        Assert.Equal(
            ExpectedTools.OrderBy(n => n, StringComparer.Ordinal),
            tools.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.All(tools, t => Assert.False(string.IsNullOrWhiteSpace(t.Description)));
        Assert.All(tools, t => Assert.StartsWith("{", t.ParametersJsonSchema.TrimStart(), StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task RealProcess_CreateThenGetNote_RoundTripsViaTempDb()
    {
        var host = RequireHost();
        Skip.If(host is null, "未找到 dotnet 宿主或 aerocode-mcp.dll 构建产物，如实跳过 E2E");

        var config = McpE2E.MakeConfig(host!.Value.Dotnet, host.Value.Dll, DbPath, "aerocode");
        await using var gateway = new McpGateway(config);

        var created = await gateway.CallToolAsync("create_note", new Dictionary<string, object?>
        {
            ["title"] = "MCP E2E 笔记",
            ["content"] = "# 标题\n\n真实进程写入的正文。",
        });
        Assert.False(created.IsError, created.Text);
        using var createdDoc = JsonDocument.Parse(created.Text);
        Assert.True(createdDoc.RootElement.GetProperty("ok").GetBoolean());
        var noteId = createdDoc.RootElement.GetProperty("id").GetInt64();
        Assert.True(noteId > 0);

        var fetched = await gateway.CallToolAsync("get_note", new Dictionary<string, object?>
        {
            ["id"] = noteId,
        });
        Assert.False(fetched.IsError, fetched.Text);
        using var fetchedDoc = JsonDocument.Parse(fetched.Text);
        Assert.Equal("MCP E2E 笔记", fetchedDoc.RootElement.GetProperty("Title").GetString());
        Assert.Contains("真实进程写入的正文", fetchedDoc.RootElement.GetProperty("Content").GetString());

        // 真实持久化：临时库文件必须存在（SQLite 主库文件）。
        Assert.True(File.Exists(DbPath));
    }

    [SkippableFact]
    public async Task RealProcess_RestartAsync_NextCallSpawnsNewProcessAndSucceeds()
    {
        var host = RequireHost();
        Skip.If(host is null, "未找到 dotnet 宿主或 aerocode-mcp.dll 构建产物，如实跳过 E2E");

        var config = McpE2E.MakeConfig(host!.Value.Dotnet, host.Value.Dll, DbPath, "aerocode");
        await using var gateway = new McpGateway(config);

        var before = await gateway.CallToolAsync("list_notes", null);
        AssertListNotesOk(before);
        var pidsBefore = McpE2E.FindServerProcessIds();
        Skip.If(pidsBefore.Count == 0, "wmic 不可用或无法定位服务器进程，重启断言如实跳过");

        await gateway.RestartAsync();

        var after = await gateway.CallToolAsync("list_notes", null); // 自动重建子进程
        AssertListNotesOk(after);
        var pidsAfter = McpE2E.FindServerProcessIds();
        Assert.Contains(pidsAfter, pid => !pidsBefore.Contains(pid)); // 是新进程，不是旧连接
    }

    [SkippableFact]
    public async Task RealProcess_ServerKilledExternally_NextCallAutoReconnects()
    {
        var host = RequireHost();
        Skip.If(host is null, "未找到 dotnet 宿主或 aerocode-mcp.dll 构建产物，如实跳过 E2E");

        var config = McpE2E.MakeConfig(host!.Value.Dotnet, host.Value.Dll, DbPath, "aerocode");
        await using var gateway = new McpGateway(config);

        var warmup = await gateway.CallToolAsync("list_notes", null);
        AssertListNotesOk(warmup);

        var pids = McpE2E.FindServerProcessIds();
        Skip.If(pids.Count == 0, "wmic 不可用或无法定位服务器进程，杀进程重连断言如实跳过");
        Assert.True(McpE2E.KillProcessIds(pids), "taskkill 终止服务器进程失败");

        // 进程已死：下一次调用必须自动重连并成功（而不是挂起或静默失败）。
        var healed = await gateway.CallToolAsync("list_notes", null);
        AssertListNotesOk(healed);
        Assert.Contains(McpE2E.FindServerProcessIds(), pid => !pids.Contains(pid));
    }

    [SkippableFact]
    public async Task RealProcess_McpToolbox_PrefixedNames_RouteToEndToEnd()
    {
        var host = RequireHost();
        Skip.If(host is null, "未找到 dotnet 宿主或 aerocode-mcp.dll 构建产物，如实跳过 E2E");

        var config = McpE2E.MakeConfig(host!.Value.Dotnet, host.Value.Dll, DbPath, "aerocode");
        await using var toolbox = new McpToolbox(new[] { new McpGateway(config) });

        await toolbox.DiscoverAsync();

        Assert.Equal(12, toolbox.Definitions.Count);
        Assert.All(toolbox.Definitions, d => Assert.StartsWith("aerocode_", d.Name, StringComparison.Ordinal));
        Assert.True(toolbox.TryGetRoute("aerocode_create_note", out var serverId, out var remoteName));
        Assert.Equal("aerocode", serverId);
        Assert.Equal("create_note", remoteName);

        var created = await toolbox.InvokeAsync(
            "aerocode_create_note",
            "{\"title\":\"经工具箱写入\",\"content\":\"toolbox e2e\"}",
            System.Threading.CancellationToken.None);
        Assert.True(created.Success, created.Error);
        using var doc = JsonDocument.Parse(created.Output);
        var noteId = doc.RootElement.GetProperty("id").GetInt64();

        var fetched = await toolbox.InvokeAsync(
            "aerocode_get_note",
            $"{{\"id\":{noteId}}}",
            System.Threading.CancellationToken.None);
        Assert.True(fetched.Success, fetched.Error);
        // 服务器把非 ASCII 转义为 \uXXXX，断言走解析后的字段而非原始子串。
        using var fetchedDoc = JsonDocument.Parse(fetched.Output);
        Assert.Equal("经工具箱写入", fetchedDoc.RootElement.GetProperty("Title").GetString());
    }
}
