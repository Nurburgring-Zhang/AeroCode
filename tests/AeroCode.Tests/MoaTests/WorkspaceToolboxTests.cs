// Copyright (c) AeroCode
// WorkspaceToolbox 八工具真实行为验证：真实临时目录读写/真实子进程执行，零 mock。
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AeroAgent.Moa.Tools;
using AeroAgent.Moa.Tools.Workspace;
using Xunit;

namespace AeroCode.Tests.MoaTests;

/// <summary>
/// 工作区工具域行为钉子：每个用例断言真实落盘内容/退出码/错误消息，
/// 不做"只断言不抛异常"的空心断言。run_shell 直接经工具域真实执行
/// （权限裁决在 ToolRouter/PermissionPolicy 层，不在本域内）。
/// </summary>
public sealed class WorkspaceToolboxTests : IDisposable
{
    private readonly string _dir;
    private readonly WorkspaceContext _ws;
    private readonly ShellRunner _shell;
    private readonly WorkspaceToolbox _box;

    public WorkspaceToolboxTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"wsbox_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _ws = new WorkspaceContext(_dir);
        _shell = new ShellRunner(_dir, TimeSpan.FromSeconds(30));
        _box = new WorkspaceToolbox(_ws, _shell);
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

    private static string J(string path, string extra = "") =>
        $"{{\"path\":\"{path.Replace("\\", "\\\\")}\"{extra}}}";

    private async Task<ToolInvokeResult> InvokeAsync(string tool, string argsJson) =>
        await _box.InvokeAsync(tool, argsJson, CancellationToken.None);

    private async Task<ToolInvokeResult> InvokeOkAsync(string tool, string argsJson)
    {
        var r = await InvokeAsync(tool, argsJson);
        Assert.True(r.Success, $"工具 {tool} 应成功，实际失败：{r.Error}");
        return r;
    }

    // ---------- read_file ----------

    [Fact]
    public async Task ReadFile_ReturnsNumberedLines()
    {
        File.WriteAllLines(Path.Combine(_dir, "a.txt"), new[] { "first", "second", "third" });

        var r = await InvokeOkAsync("read_file", J("a.txt"));

        Assert.Contains("1: first", r.Output);
        Assert.Contains("2: second", r.Output);
        Assert.Contains("3: third", r.Output);
        // 未触达尾部截断提示时不得出现提示行
        Assert.DoesNotContain("[aerocode] showing lines", r.Output);
    }

    [Fact]
    public async Task ReadFile_OffsetLimit_ReturnsExactWindow()
    {
        File.WriteAllLines(Path.Combine(_dir, "a.txt"), new[] { "l1", "l2", "l3", "l4", "l5" });

        var r = await InvokeOkAsync("read_file", J("a.txt", ",\"offset\":2,\"limit\":2"));

        Assert.Contains("2: l2", r.Output);
        Assert.Contains("3: l3", r.Output);
        Assert.DoesNotContain("1: l1", r.Output);
        Assert.DoesNotContain("4: l4", r.Output);
        Assert.Contains("showing lines 2-3 of 5", r.Output);
    }

    [Fact]
    public async Task ReadFile_MissingFile_FailsWithDisplayPath()
    {
        var r = await InvokeAsync("read_file", J("nope.txt"));

        Assert.False(r.Success);
        Assert.Contains("File not found", r.Output);
        Assert.Contains("nope.txt", r.Output);
    }

    [Fact]
    public async Task ReadFile_OversizeFile_RejectedWithGrepAdvice()
    {
        // 真实 8MB+1 字节文件：用 SetLength 即时撑大，不逐字节写。
        var path = Path.Combine(_dir, "big.bin");
        using (var fs = File.Create(path))
        {
            fs.SetLength(WorkspaceToolbox.MaxReadBytes + 1);
        }

        var r = await InvokeAsync("read_file", J("big.bin"));

        Assert.False(r.Success);
        Assert.Contains("File too large", r.Output);
        Assert.Contains("grep_search", r.Output);
    }

    [Theory]
    [InlineData("{\"path\":\"a.txt\",\"offset\":0}", "offset is 1-based")]
    [InlineData("{\"path\":\"a.txt\",\"limit\":0}", "limit must be within 1..5000")]
    [InlineData("{\"path\":\"a.txt\",\"limit\":5001}", "limit must be within 1..5000")]
    [InlineData("{}", "requires 'path'")]
    public async Task ReadFile_BadArguments_FailsHonest(string argsJson, string expectedMessage)
    {
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "x");

        var r = await InvokeAsync("read_file", argsJson);

        Assert.False(r.Success);
        Assert.Contains(expectedMessage, r.Output);
    }

    [Fact]
    public async Task ReadFile_OutsideWorkspace_FailsWithoutExecution()
    {
        var r = await InvokeAsync("read_file", J("../../outside.txt"));

        Assert.False(r.Success);
        Assert.Contains("outside the workspace root", r.Output);
    }

    // ---------- write_file ----------

    [Fact]
    public async Task WriteFile_CreatesFile_ContentRoundTrips()
    {
        var r = await InvokeOkAsync("write_file", J("new.txt", ",\"content\":\"hello 工作区\""));

        Assert.Equal("hello 工作区", File.ReadAllText(Path.Combine(_dir, "new.txt")));
        Assert.Contains("Wrote 9 chars to new.txt", r.Output);
    }

    [Fact]
    public async Task WriteFile_Overwrite_ReplacesContent()
    {
        await InvokeOkAsync("write_file", J("f.txt", ",\"content\":\"v1\""));
        await InvokeOkAsync("write_file", J("f.txt", ",\"content\":\"v2\""));

        Assert.Equal("v2", File.ReadAllText(Path.Combine(_dir, "f.txt")));
    }

    [Fact]
    public async Task WriteFile_CreatesMissingDirectories()
    {
        await InvokeOkAsync("write_file", J("deep/nested/dir/file.txt", ",\"content\":\"x\""));

        Assert.True(File.Exists(Path.Combine(_dir, "deep", "nested", "dir", "file.txt")));
    }

    [Fact]
    public async Task WriteFile_EmptyContentAllowed()
    {
        var r = await InvokeOkAsync("write_file", J("empty.txt", ",\"content\":\"\""));

        Assert.True(r.Success);
        Assert.Equal(string.Empty, File.ReadAllText(Path.Combine(_dir, "empty.txt")));
    }

    [Fact]
    public async Task WriteFile_ExcludedPath_RefusedWithoutForce()
    {
        var r = await InvokeAsync("write_file", J("bin/out.txt", ",\"content\":\"x\""));

        Assert.False(r.Success);
        Assert.Contains("Refusing to write", r.Output);
        Assert.Contains("force", r.Output);
        Assert.False(File.Exists(Path.Combine(_dir, "bin", "out.txt")));
    }

    [Fact]
    public async Task WriteFile_ExcludedPath_ForceTrue_Writes()
    {
        var r = await InvokeOkAsync("write_file", J("bin/out.txt", ",\"content\":\"x\",\"force\":true"));

        Assert.True(File.Exists(Path.Combine(_dir, "bin", "out.txt")));
        Assert.Contains("Wrote", r.Output);
    }

    [Fact]
    public async Task WriteFile_EnvFile_RefusedUnlessForce()
    {
        var refused = await InvokeAsync("write_file", J(".env", ",\"content\":\"K=V\""));
        Assert.False(refused.Success);
        Assert.Contains("Refusing to write", refused.Output);

        await InvokeOkAsync("write_file", J(".env", ",\"content\":\"K=V\",\"force\":true"));
        Assert.True(File.Exists(Path.Combine(_dir, ".env")));
    }

    // ---------- edit_file ----------

    [Fact]
    public async Task EditFile_UniqueOccurrence_ReplacesOnce()
    {
        File.WriteAllText(Path.Combine(_dir, "code.cs"), "var greeting = \"hello world\";");

        var r = await InvokeOkAsync("edit_file", J("code.cs", ",\"old_string\":\"world\",\"new_string\":\"there\""));

        Assert.Equal("var greeting = \"hello there\";", File.ReadAllText(Path.Combine(_dir, "code.cs")));
        Assert.Contains("Edited code.cs (1 replacement(s)", r.Output);
    }

    [Fact]
    public async Task EditFile_MultipleOccurrences_FailsAndLeavesFileUntouched()
    {
        File.WriteAllText(Path.Combine(_dir, "dup.txt"), "a a");

        var r = await InvokeAsync("edit_file", J("dup.txt", ",\"old_string\":\"a\",\"new_string\":\"b\""));

        Assert.False(r.Success);
        Assert.Contains("occurs 2 times", r.Output);
        Assert.Equal("a a", File.ReadAllText(Path.Combine(_dir, "dup.txt")));
    }

    [Fact]
    public async Task EditFile_ReplaceAll_True_ReplacesEveryOccurrence()
    {
        File.WriteAllText(Path.Combine(_dir, "dup.txt"), "a a");

        var r = await InvokeOkAsync(
            "edit_file", J("dup.txt", ",\"old_string\":\"a\",\"new_string\":\"b\",\"replace_all\":true"));

        Assert.Equal("b b", File.ReadAllText(Path.Combine(_dir, "dup.txt")));
        Assert.Contains("(2 replacement(s)", r.Output);
    }

    [Fact]
    public async Task EditFile_OldStringNotFound_Fails()
    {
        File.WriteAllText(Path.Combine(_dir, "f.txt"), "real content");

        var r = await InvokeAsync("edit_file", J("f.txt", ",\"old_string\":\"absent\",\"new_string\":\"x\""));

        Assert.False(r.Success);
        Assert.Contains("old_string not found", r.Output);
    }

    [Fact]
    public async Task EditFile_MissingFile_Fails()
    {
        var r = await InvokeAsync("edit_file", J("ghost.txt", ",\"old_string\":\"a\",\"new_string\":\"b\""));

        Assert.False(r.Success);
        Assert.Contains("File not found", r.Output);
    }

    // ---------- delete_file ----------

    [Fact]
    public async Task DeleteFile_RemovesFile()
    {
        var path = Path.Combine(_dir, "junk.txt");
        File.WriteAllText(path, "x");

        var r = await InvokeOkAsync("delete_file", J("junk.txt"));

        Assert.False(File.Exists(path));
        Assert.Contains("Deleted file junk.txt", r.Output);
    }

    [Fact]
    public async Task DeleteFile_DirectoryWithoutRecursive_Refused()
    {
        var sub = Path.Combine(_dir, "sub");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "keep.txt"), "x");

        var r = await InvokeAsync("delete_file", J("sub"));

        Assert.False(r.Success);
        Assert.Contains("recursive: true", r.Output);
        Assert.True(Directory.Exists(sub));
        Assert.True(File.Exists(Path.Combine(sub, "keep.txt")));
    }

    [Fact]
    public async Task DeleteFile_RecursiveTrue_RemovesTree()
    {
        var sub = Path.Combine(_dir, "sub");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "child.txt"), "x");

        var r = await InvokeOkAsync("delete_file", J("sub", ",\"recursive\":true"));

        Assert.False(Directory.Exists(sub));
        Assert.Contains("Deleted directory sub (recursive)", r.Output);
    }

    [Fact]
    public async Task DeleteFile_MissingPath_Fails()
    {
        var r = await InvokeAsync("delete_file", J("nothing_here.txt"));

        Assert.False(r.Success);
        Assert.Contains("Path not found", r.Output);
    }

    [Fact]
    public async Task DeleteFile_WorkspaceRoot_Refused()
    {
        var r = await InvokeAsync("delete_file", "{\"path\":\".\"}");

        Assert.False(r.Success);
        Assert.Contains("Refusing to delete the workspace root itself", r.Output);
        Assert.True(Directory.Exists(_dir));
    }

    // ---------- list_directory ----------

    [Fact]
    public async Task ListDirectory_DirsFirstSorted_ExcludedSkipped()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "b_dir"));
        Directory.CreateDirectory(Path.Combine(_dir, "a_dir"));
        Directory.CreateDirectory(Path.Combine(_dir, "node_modules"));
        File.WriteAllText(Path.Combine(_dir, "node_modules", "hidden.js"), "x");
        File.WriteAllText(Path.Combine(_dir, "zeta.txt"), "12345");

        var r = await InvokeOkAsync("list_directory", "{}");

        var dirsIdx = r.Output.IndexOf("[dir] a_dir");
        var dirs2Idx = r.Output.IndexOf("[dir] b_dir");
        var fileIdx = r.Output.IndexOf("zeta.txt");
        Assert.True(dirsIdx >= 0 && dirs2Idx > dirsIdx && fileIdx > dirs2Idx,
            $"目录应先于文件且有序，实际输出：{r.Output}");
        Assert.Contains("(5 B)", r.Output);
        Assert.DoesNotContain("node_modules", r.Output);
        Assert.DoesNotContain("hidden.js", r.Output);
    }

    [Fact]
    public async Task ListDirectory_EmptyDirectory_ReportsEmpty()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "void"));

        var r = await InvokeOkAsync("list_directory", "{\"path\":\"void\"}");

        Assert.Equal("(empty)", r.Output.Trim());
    }

    // ---------- search_files ----------

    [Fact]
    public async Task SearchFiles_GlobFindsRecursiveMatches()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "src"));
        File.WriteAllText(Path.Combine(_dir, "src", "prog.cs"), "x");
        File.WriteAllText(Path.Combine(_dir, "readme.md"), "x");

        var r = await InvokeOkAsync("search_files", "{\"pattern\":\"*.cs\"}");

        Assert.Contains("src/prog.cs", r.Output);
        Assert.DoesNotContain("readme.md", r.Output);
    }

    [Fact]
    public async Task SearchFiles_HonorsExclusions_AndGitignore()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "node_modules"));
        File.WriteAllText(Path.Combine(_dir, "node_modules", "dep.cs"), "x");
        File.WriteAllText(Path.Combine(_dir, "skipme.cs"), "x");
        File.WriteAllText(Path.Combine(_dir, "keep.cs"), "x");
        File.WriteAllText(Path.Combine(_dir, ".gitignore"), "skipme.cs\n");
        // WorkspaceContext 在构造时读取 .gitignore（快照语义）：先落 gitignore 再建上下文。
        var ws = new WorkspaceContext(_dir);
        var box = new WorkspaceToolbox(ws, _shell);

        var r = await box.InvokeAsync("search_files", "{\"pattern\":\"*.cs\"}", CancellationToken.None);

        Assert.True(r.Success, r.Output);
        Assert.Contains("keep.cs", r.Output);
        Assert.DoesNotContain("node_modules", r.Output);
        Assert.DoesNotContain("skipme.cs", r.Output);
    }

    [Fact]
    public async Task SearchFiles_MaxResults_StopsAndReports()
    {
        for (var i = 1; i <= 5; i++)
        {
            File.WriteAllText(Path.Combine(_dir, $"f{i}.log"), "x");
        }

        var r = await InvokeOkAsync("search_files", "{\"pattern\":\"*.log\",\"max_results\":3}");

        Assert.Contains("stopped at 3 results", r.Output);
        Assert.DoesNotContain("f4.log", r.Output);
    }

    [Fact]
    public async Task SearchFiles_NoMatches_ReportsHonest()
    {
        var r = await InvokeOkAsync("search_files", "{\"pattern\":\"*.nope\"}");

        Assert.Contains("No files matching", r.Output);
    }

    // ---------- grep_search ----------

    [Fact]
    public async Task GrepSearch_Literal_ReturnsFileLineText()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "docs"));
        File.WriteAllLines(Path.Combine(_dir, "docs", "note.txt"), new[] { "intro", "hello world", "tail" });

        var r = await InvokeOkAsync("grep_search", "{\"pattern\":\"hello world\"}");

        Assert.Contains("docs/note.txt:2: hello world", r.Output);
    }

    [Fact]
    public async Task GrepSearch_RegexMode_UsesRegularExpression()
    {
        File.WriteAllLines(Path.Combine(_dir, "rx.txt"), new[] { "foo123", "bar", "foox" });

        var r = await InvokeOkAsync("grep_search", "{\"pattern\":\"^foo[0-9]+$\",\"regex\":true}");

        Assert.Contains("rx.txt:1: foo123", r.Output);
        Assert.DoesNotContain("foox", r.Output);
        Assert.DoesNotContain("bar", r.Output);
    }

    [Fact]
    public async Task GrepSearch_CaseSensitivity_DefaultInsensitive_ExplicitSensitive()
    {
        File.WriteAllText(Path.Combine(_dir, "case.txt"), "Hello World");

        var insensitive = await InvokeOkAsync("grep_search", "{\"pattern\":\"hello world\"}");
        Assert.Contains("case.txt:1: Hello World", insensitive.Output);

        var sensitive = await InvokeOkAsync(
            "grep_search", "{\"pattern\":\"hello world\",\"case_sensitive\":true}");
        Assert.Contains("No matches", sensitive.Output);
    }

    [Fact]
    public async Task GrepSearch_BinaryFileSkipped_NulSentinel()
    {
        // 含 NUL 字节的二进制文件必须整文件跳过（NUL 哨兵），即便文本形状上匹配。
        File.WriteAllBytes(Path.Combine(_dir, "blob.dat"), new byte[] { 0x61, 0x00, 0x62 });

        var r = await InvokeOkAsync("grep_search", "{\"pattern\":\"a\"}");

        Assert.Contains("No matches", r.Output);
        Assert.DoesNotContain("blob.dat", r.Output);
    }

    [Fact]
    public async Task GrepSearch_MaxResults_StopsEarly()
    {
        for (var i = 1; i <= 3; i++)
        {
            File.WriteAllText(Path.Combine(_dir, $"g{i}.txt"), "needle");
        }

        var r = await InvokeOkAsync("grep_search", "{\"pattern\":\"needle\",\"max_results\":2}");

        Assert.Contains("stopped at 2 results", r.Output);
        var hitLines = r.Output.Split('\n').Count(l => l.Contains("needle"));
        Assert.Equal(2, hitLines);
    }

    [Fact]
    public async Task GrepSearch_InvalidRegex_FailsHonest()
    {
        var r = await InvokeAsync("grep_search", "{\"pattern\":\"[unclosed\",\"regex\":true}");

        Assert.False(r.Success);
        Assert.Contains("Invalid regex", r.Output);
    }

    // ---------- run_shell ----------

    [Fact]
    public async Task RunShell_Echo_ReportsExitCodeAndStdout()
    {
        var r = await InvokeOkAsync("run_shell", "{\"command\":\"echo hello_ws\"}");

        Assert.Contains("exit=0", r.Output);
        Assert.Contains("hello_ws", r.Output);
        Assert.Contains("--- stdout ---", r.Output);
    }

    [Fact]
    public async Task RunShell_FailingCommand_ReportsRealExitCode()
    {
        var cmd = OperatingSystem.IsWindows() ? "cmd /c exit 3" : "exit 3";

        var r = await InvokeOkAsync("run_shell", $"{{\"command\":\"{cmd}\"}}");

        // 工具域如实回报非零退出码，不冒充成功。
        Assert.Contains("exit=3", r.Output);
    }

    [Fact]
    public async Task RunShell_Timeout_KillsTreeAndReports()
    {
        var cmd = OperatingSystem.IsWindows() ? "ping -n 5 127.0.0.1 > nul" : "sleep 5";

        var r = await InvokeOkAsync("run_shell", $"{{\"command\":\"{cmd}\",\"timeout_seconds\":1}}");

        Assert.Contains("exit=-1", r.Output);
        Assert.Contains("(timed out, killed)", r.Output);
        Assert.Contains("timed out after 1s", r.Output);
    }

    [Fact]
    public async Task RunShell_MissingCommand_Fails()
    {
        var r = await InvokeAsync("run_shell", "{}");

        Assert.False(r.Success);
        Assert.Contains("requires 'command'", r.Output);
    }

    // ---------- 检查点留痕钩子（ICheckpointTracker 集成） ----------

    [Fact]
    public async Task WriteEditDelete_WithCheckpointStore_LeavesTraces()
    {
        var cpRoot = Path.Combine(_dir, "checkpoints");
        var store = new CheckpointStore(cpRoot);
        var box = new WorkspaceToolbox(_ws, _shell, store);

        await box.InvokeAsync("write_file", J("cp.txt", ",\"content\":\"v1\""), CancellationToken.None);
        await box.InvokeAsync("edit_file", J("cp.txt", ",\"old_string\":\"v1\",\"new_string\":\"v2\""), CancellationToken.None);
        await box.InvokeAsync("delete_file", J("cp.txt"), CancellationToken.None);

        var infos = store.List();
        Assert.Equal(3, infos.Count);
        // List 新→旧：delete/edit/write
        Assert.Equal("delete_file", infos[0].ToolName);
        Assert.Equal("edit_file", infos[1].ToolName);
        Assert.Equal("write_file", infos[2].ToolName);
        Assert.All(infos, i => Assert.Contains("cp.txt", i.Paths[0]));
    }

    [Fact]
    public async Task UnknownWorkspaceTool_FailsHonest()
    {
        var r = await InvokeAsync("not_a_tool", "{}");

        Assert.False(r.Success);
        Assert.Contains("Unknown workspace tool", r.Output);
    }

    [Fact]
    public async Task MalformedArgumentsJson_FailsWithoutThrowing()
    {
        var r = await InvokeAsync("read_file", "{not json");

        Assert.False(r.Success);
        Assert.Contains("Invalid arguments JSON", r.Output);
    }
}
