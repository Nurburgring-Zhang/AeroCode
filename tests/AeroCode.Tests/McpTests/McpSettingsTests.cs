using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AeroCode.App.Configuration;
using AeroCode.App.Services;
using AeroCode.Mcp.Client;
using Xunit;

namespace AeroCode.Tests.McpTests;

/// <summary>settings.json 的 mcpServers 段读写回环（S5：MCP server 配置持久化）。</summary>
public sealed class McpSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "aerocode-mcp-settings-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响测试结论
        }
    }

    private SettingsService NewService() => new(new AppDataPaths(_root));

    [Fact]
    public async Task McpServers_SaveLoadRoundTrip_AllFieldsPreserved()
    {
        var writer = NewService();
        await writer.LoadAsync();
        writer.Current.McpServers.Add(new McpServerConfig
        {
            Id = "aerocode",
            DisplayName = "AeroCode 笔记",
            Command = "dotnet",
            Arguments = { "exec", @"D:\mcp\aerocode-mcp.dll" },
            EnvironmentVariables = new() { ["AEROCODE_DB_PATH"] = @"D:\tmp\e2e.db" },
            WorkingDirectory = @"D:\mcp",
            Enabled = true,
        });
        writer.Current.McpServers.Add(new McpServerConfig
        {
            Id = "disabled-one",
            Command = "node",
            Arguments = { "server.js" },
            Enabled = false,
        });
        await writer.SaveAsync();

        var reader = NewService();
        await reader.LoadAsync();

        Assert.Equal(2, reader.Current.McpServers.Count);
        var first = reader.Current.McpServers[0];
        Assert.Equal("aerocode", first.Id);
        Assert.Equal("AeroCode 笔记", first.DisplayName);
        Assert.Equal("dotnet", first.Command);
        Assert.Equal(new[] { "exec", @"D:\mcp\aerocode-mcp.dll" }, first.Arguments.ToArray());
        Assert.Equal(@"D:\tmp\e2e.db", first.EnvironmentVariables!["AEROCODE_DB_PATH"]);
        Assert.Equal(@"D:\mcp", first.WorkingDirectory);
        Assert.True(first.Enabled);

        var second = reader.Current.McpServers[1];
        Assert.Equal("disabled-one", second.Id);
        Assert.False(second.Enabled);
        Assert.Null(second.EnvironmentVariables);
    }

    [Fact]
    public async Task FreshSettings_McpServersDefaultsToEmpty()
    {
        var service = NewService();
        await service.LoadAsync(); // 无文件 → 落默认配置

        Assert.Empty(service.Current.McpServers);
        Assert.True(File.Exists(Path.Combine(_root, "settings.json")));

        // 重新加载：默认文件里的 mcpServers 段应为空数组而非丢失。
        var again = NewService();
        await again.LoadAsync();
        Assert.Empty(again.Current.McpServers);
    }
}
