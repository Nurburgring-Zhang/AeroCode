// Copyright (c) AeroCode
// WorkspaceToolbox — 工作区编码工具族的真实执行器（对标 opencode src/tool/{read,edit,shell}.ts）。
// PermissionPolicy.CreateDefault 已为该工具族预置规则（read allow / write ask / shell
// 危险模式升级）；本类把休眠规则接线为真实执行。全部路径经 WorkspaceContext 边界校验，
// 写类操作在落盘前经 ICheckpointTracker 留检查点（可回滚），零 mock。
using System.Globalization;
using System.Text;
using System.Text.Json;
using AeroCode.AI.Models;

namespace AeroAgent.Moa.Tools.Workspace;

/// <summary>
/// 写类操作落盘前的检查点留痕钩子（<see cref="CheckpointStore"/> 实现）。
/// 独立成接口使工具箱不依赖检查点实现；为 null 时不留痕（诚实降级需在装配处显式说明）。
/// </summary>
public interface ICheckpointTracker
{
    /// <summary>为目标路径集记录当前内容快照（不存在=删除语义），返回检查点序号。</summary>
    long Track(string toolName, IReadOnlyList<string> absolutePaths);
}

/// <summary>
/// 工作区工具域：read/write/edit/delete/list/search/grep/shell 八个真实工具。
/// 所有参数在域内自行解析与校验；域内失败以 <see cref="ToolInvokeResult.Fail"/> 如实返回，
/// 永不抛业务异常（ToolboxRegistry 契约）。
/// </summary>
public sealed class WorkspaceToolbox : IWorkerToolbox
{
    /// <summary>单次 read 的文件大小上限（>8MB 建议用 grep_search 定点读取）。</summary>
    public const long MaxReadBytes = 8 * 1024 * 1024;

    /// <summary>search/grep 默认与硬上限结果数。</summary>
    public const int DefaultMaxResults = 100;
    public const int HardMaxResults = 500;

    private readonly WorkspaceContext _workspace;
    private readonly ShellRunner _shell;
    private readonly ICheckpointTracker? _checkpoints;
    private readonly IReadOnlyList<ToolDefinition> _definitions;

    public WorkspaceToolbox(
        WorkspaceContext workspace,
        ShellRunner shell,
        ICheckpointTracker? checkpoints = null)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        _checkpoints = checkpoints;
        _definitions = BuildDefinitions();
    }

    public string Domain => "workspace";

    public IReadOnlyList<ToolDefinition> Definitions => _definitions;

    /// <inheritdoc/>
    public async Task<ToolInvokeResult> InvokeAsync(string toolName, string argumentsJson, CancellationToken ct)
    {
        try
        {
            using var doc = string.IsNullOrWhiteSpace(argumentsJson)
                ? JsonDocument.Parse("{}")
                : JsonDocument.Parse(argumentsJson);
            var args = doc.RootElement.ValueKind == JsonValueKind.Object
                ? doc.RootElement
                : throw new ArgumentException("arguments must be a JSON object");

            return toolName switch
            {
                "read_file" => ReadFile(args),
                "write_file" => WriteFile(args),
                "edit_file" => EditFile(args),
                "delete_file" => DeleteFile(args),
                "list_directory" => ListDirectory(args),
                "search_files" => SearchFiles(args),
                "grep_search" => GrepSearch(args),
                "run_shell" => await RunShellAsync(args, ct).ConfigureAwait(false),
                _ => ToolInvokeResult.Fail($"Unknown workspace tool '{toolName}'"),
            };
        }
        catch (JsonException ex)
        {
            return ToolInvokeResult.Fail($"Invalid arguments JSON: {ex.Message}");
        }
        catch (ArgumentException ex)
        {
            return ToolInvokeResult.Fail(ex.Message);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (IOException ex)
        {
            return ToolInvokeResult.Fail($"IO error: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            return ToolInvokeResult.Fail($"Access denied: {ex.Message}");
        }
    }

    // ---------- read_file ----------

    private ToolInvokeResult ReadFile(JsonElement args)
    {
        var abs = RequirePath(args);
        if (abs is null)
        {
            return FailOutsideWorkspace();
        }

        var info = new FileInfo(abs);
        if (!info.Exists)
        {
            return ToolInvokeResult.Fail($"File not found: {_workspace.Display(abs)}");
        }

        if (info.Length > MaxReadBytes)
        {
            return ToolInvokeResult.Fail(
                $"File too large to read in full ({info.Length:N0} bytes > {MaxReadBytes:N0}). " +
                "Use grep_search to locate content, then read_file with offset/limit.");
        }

        var lines = File.ReadAllLines(abs);
        var offset = GetInt(args, "offset", 1);
        var limit = GetInt(args, "limit", 2000);
        if (offset < 1)
        {
            return ToolInvokeResult.Fail("offset is 1-based and must be >= 1");
        }

        if (limit is < 1 or > 5000)
        {
            return ToolInvokeResult.Fail("limit must be within 1..5000");
        }

        var sb = new StringBuilder();
        var last = Math.Min(lines.Length, (offset - 1) + limit);
        for (var i = offset - 1; i < last; i++)
        {
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append(": ").AppendLine(lines[i]);
        }

        if (last < lines.Length)
        {
            sb.Append($"[aerocode] showing lines {offset}-{last} of {lines.Length}; " +
                      "pass offset/limit for more").AppendLine();
        }

        return ToolInvokeResult.Ok(sb.ToString());
    }

    // ---------- write_file ----------

    private ToolInvokeResult WriteFile(JsonElement args)
    {
        var abs = RequirePath(args);
        if (abs is null)
        {
            return FailOutsideWorkspace();
        }

        var content = GetString(args, "content");
        if (content is null)
        {
            return ToolInvokeResult.Fail("write_file requires 'content' (string; empty string allowed)");
        }

        if (_workspace.ShouldExclude(_workspace.Display(abs)) && !IsExplicitlyRequested(args))
        {
            return ToolInvokeResult.Fail(
                $"Refusing to write '{_workspace.Display(abs)}': path matches build/dependency/sensitive exclusions. " +
                "Pass \"force\": true if this is genuinely intended.");
        }

        TrackCheckpoint("write_file", abs);
        var dir = Path.GetDirectoryName(abs);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(abs, content);
        return ToolInvokeResult.Ok(
            $"Wrote {content.Length:N0} chars to {_workspace.Display(abs)}");
    }

    // ---------- edit_file ----------

    private ToolInvokeResult EditFile(JsonElement args)
    {
        var abs = RequirePath(args);
        if (abs is null)
        {
            return FailOutsideWorkspace();
        }

        var oldText = GetString(args, "old_string");
        var newText = GetString(args, "new_string");
        if (oldText is null || newText is null)
        {
            return ToolInvokeResult.Fail("edit_file requires 'old_string' and 'new_string'");
        }

        if (!File.Exists(abs))
        {
            return ToolInvokeResult.Fail($"File not found: {_workspace.Display(abs)}");
        }

        var original = File.ReadAllText(abs);
        var occurrences = CountOccurrences(original, oldText);
        var replaceAll = args.TryGetProperty("replace_all", out var ra) && ra.ValueKind == JsonValueKind.True;

        if (occurrences == 0)
        {
            return ToolInvokeResult.Fail(
                $"old_string not found in {_workspace.Display(abs)} — copy the exact text including whitespace");
        }

        if (occurrences > 1 && !replaceAll)
        {
            return ToolInvokeResult.Fail(
                $"old_string occurs {occurrences} times in {_workspace.Display(abs)}; " +
                "provide more surrounding context or pass replace_all: true");
        }

        TrackCheckpoint("edit_file", abs);
        var updated = replaceAll ? original.Replace(oldText, newText) : ReplaceFirst(original, oldText, newText);
        File.WriteAllText(abs, updated);
        return ToolInvokeResult.Ok(
            $"Edited {_workspace.Display(abs)} ({(replaceAll ? occurrences : 1)} replacement(s), " +
            $"{original.Length:N0} -> {updated.Length:N0} chars)");
    }

    // ---------- delete_file ----------

    private ToolInvokeResult DeleteFile(JsonElement args)
    {
        var abs = RequirePath(args);
        if (abs is null)
        {
            return FailOutsideWorkspace();
        }

        if (string.Equals(abs, _workspace.Root, StringComparison.OrdinalIgnoreCase))
        {
            return ToolInvokeResult.Fail("Refusing to delete the workspace root itself");
        }

        var recursive = args.TryGetProperty("recursive", out var rec) && rec.ValueKind == JsonValueKind.True;
        if (File.Exists(abs))
        {
            TrackCheckpoint("delete_file", abs);
            File.Delete(abs);
            return ToolInvokeResult.Ok($"Deleted file {_workspace.Display(abs)}");
        }

        if (Directory.Exists(abs))
        {
            if (!recursive)
            {
                return ToolInvokeResult.Fail(
                    $"{_workspace.Display(abs)} is a directory; pass recursive: true to delete it with all contents");
            }

            TrackCheckpoint("delete_file", abs);
            Directory.Delete(abs, recursive: true);
            return ToolInvokeResult.Ok($"Deleted directory {_workspace.Display(abs)} (recursive)");
        }

        return ToolInvokeResult.Fail($"Path not found: {_workspace.Display(abs)}");
    }

    // ---------- list_directory ----------

    private ToolInvokeResult ListDirectory(JsonElement args)
    {
        var rel = GetString(args, "path") ?? ".";
        var abs = _workspace.Resolve(rel);
        if (abs is null)
        {
            return FailOutsideWorkspace();
        }

        if (!Directory.Exists(abs))
        {
            return ToolInvokeResult.Fail($"Directory not found: {_workspace.Display(abs)}");
        }

        var sb = new StringBuilder();
        foreach (var dir in Directory.EnumerateDirectories(abs).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            if (_workspace.ShouldExclude(_workspace.Display(dir)))
            {
                continue;
            }

            sb.Append("[dir] ").AppendLine(_workspace.Display(dir));
        }

        foreach (var file in Directory.EnumerateFiles(abs).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            if (_workspace.ShouldExclude(_workspace.Display(file)))
            {
                continue;
            }

            var info = new FileInfo(file);
            sb.Append("      ").Append(_workspace.Display(file))
              .AppendFormat(CultureInfo.InvariantCulture, "  ({0:N0} B)", info.Length)
              .AppendLine();
        }

        return ToolInvokeResult.Ok(sb.Length == 0 ? "(empty)" : sb.ToString());
    }

    // ---------- search_files ----------

    private ToolInvokeResult SearchFiles(JsonElement args)
    {
        var pattern = GetString(args, "pattern");
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return ToolInvokeResult.Fail("search_files requires 'pattern' (file name glob, e.g. *.cs)");
        }

        var max = ClampMax(args);
        var rel = GetString(args, "path") ?? ".";
        var abs = _workspace.Resolve(rel);
        if (abs is null)
        {
            return FailOutsideWorkspace();
        }

        if (!Directory.Exists(abs))
        {
            return ToolInvokeResult.Fail($"Directory not found: {_workspace.Display(abs)}");
        }

        var matches = new List<string>();
        foreach (var file in Directory.EnumerateFiles(
                     abs, "*", new EnumerationOptions
                     {
                         RecurseSubdirectories = true,
                         IgnoreInaccessible = true,
                         MatchCasing = MatchCasing.CaseInsensitive,
                     }))
        {
            var display = _workspace.Display(file);
            if (_workspace.ShouldExclude(display))
            {
                continue;
            }

            if (!GlobToRegex(pattern).IsMatch(Path.GetFileName(file)))
            {
                continue;
            }

            matches.Add(display);
            if (matches.Count >= max)
            {
                break;
            }
        }

        return ToolInvokeResult.Ok(
            matches.Count == 0
                ? $"No files matching '{pattern}' (exclusions applied)"
                : string.Join('\n', matches) + (matches.Count >= max ? $"\n[aerocode] stopped at {max} results" : string.Empty));
    }

    // ---------- grep_search ----------

    private ToolInvokeResult GrepSearch(JsonElement args)
    {
        var pattern = GetString(args, "pattern");
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return ToolInvokeResult.Fail("grep_search requires 'pattern'");
        }

        var useRegex = args.TryGetProperty("regex", out var re) && re.ValueKind == JsonValueKind.True;
        var caseSensitive = args.TryGetProperty("case_sensitive", out var cs) && cs.ValueKind == JsonValueKind.True;
        var max = ClampMax(args);
        var rel = GetString(args, "path") ?? ".";
        var abs = _workspace.Resolve(rel);
        if (abs is null)
        {
            return FailOutsideWorkspace();
        }

        System.Text.RegularExpressions.Regex rx;
        try
        {
            rx = new System.Text.RegularExpressions.Regex(
                useRegex ? pattern : System.Text.RegularExpressions.Regex.Escape(pattern),
                caseSensitive
                    ? System.Text.RegularExpressions.RegexOptions.Compiled
                    : System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase,
                TimeSpan.FromSeconds(2));
        }
        catch (ArgumentException ex)
        {
            return ToolInvokeResult.Fail($"Invalid regex: {ex.Message}");
        }

        var sb = new StringBuilder();
        var hits = 0;
        var binarySuspect = new byte[512];
        foreach (var file in Directory.EnumerateFiles(
                     abs, "*", new EnumerationOptions
                     {
                         RecurseSubdirectories = true,
                         IgnoreInaccessible = true,
                     }))
        {
            var display = _workspace.Display(file);
            if (_workspace.ShouldExclude(display))
            {
                continue;
            }

            FileInfo info;
            try
            {
                info = new FileInfo(file);
            }
            catch (IOException)
            {
                continue;
            }

            if (info.Length > MaxReadBytes)
            {
                continue;
            }

            string[] lines;
            try
            {
                using var stream = info.OpenRead();
                var read = stream.Read(binarySuspect, 0, binarySuspect.Length);
                if (binarySuspect.AsSpan(0, read).IndexOf((byte)0) >= 0)
                {
                    continue; // 二进制文件跳过（NUL 哨兵）。
                }

                lines = File.ReadAllLines(file);
            }
            catch (IOException)
            {
                continue;
            }

            for (var i = 0; i < lines.Length; i++)
            {
                if (!rx.IsMatch(lines[i]))
                {
                    continue;
                }

                sb.Append(display).Append(':').Append(i + 1).Append(": ").AppendLine(lines[i].TrimEnd());
                hits++;
                if (hits >= max)
                {
                    sb.Append("[aerocode] stopped at ").Append(max).Append(" results").AppendLine();
                    return ToolInvokeResult.Ok(sb.ToString());
                }
            }
        }

        return ToolInvokeResult.Ok(hits == 0 ? "No matches (exclusions applied)" : sb.ToString());
    }

    // ---------- run_shell ----------

    private async Task<ToolInvokeResult> RunShellAsync(JsonElement args, CancellationToken ct)
    {
        var command = GetString(args, "command");
        if (string.IsNullOrWhiteSpace(command))
        {
            return ToolInvokeResult.Fail("run_shell requires 'command'");
        }

        var timeout = GetInt(args, "timeout_seconds", 0);
        var result = await _shell.RunAsync(command, timeout, ct).ConfigureAwait(false);
        var sb = new StringBuilder();
        sb.Append("exit=").Append(result.ExitCode);
        if (result.TimedOut)
        {
            sb.Append(" (timed out, killed)");
        }

        sb.AppendLine();
        if (result.StdOut.Length > 0)
        {
            sb.AppendLine("--- stdout ---").AppendLine(result.StdOut.TrimEnd());
        }

        if (result.StdErr.Length > 0)
        {
            sb.AppendLine("--- stderr ---").AppendLine(result.StdErr.TrimEnd());
        }

        return ToolInvokeResult.Ok(sb.ToString());
    }

    // ---------- helpers ----------

    private string? RequirePath(JsonElement args)
    {
        var path = GetString(args, "path");
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("requires 'path'");
        }

        return _workspace.Resolve(path);
    }

    private static ToolInvokeResult FailOutsideWorkspace() => ToolInvokeResult.Fail(
        "Path resolves outside the workspace root — the call was not executed. " +
        "Use a path inside the workspace; if truly needed, the user must grant access explicitly.");

    private static bool IsExplicitlyRequested(JsonElement args) =>
        args.TryGetProperty("force", out var f) && f.ValueKind == JsonValueKind.True;

    private void TrackCheckpoint(string toolName, string absPath)
    {
        _checkpoints?.Track(toolName, new[] { absPath });
    }

    private static string? GetString(JsonElement args, string name) =>
        args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int GetInt(JsonElement args, string name, int fallback) =>
        args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)
            ? i
            : fallback;

    private static int ClampMax(JsonElement args)
    {
        var max = GetInt(args, "max_results", DefaultMaxResults);
        return max is < 1 or > HardMaxResults ? DefaultMaxResults : max;
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += needle.Length;
        }

        return count;
    }

    private static string ReplaceFirst(string source, string needle, string replacement)
    {
        var idx = source.IndexOf(needle, StringComparison.Ordinal);
        return idx < 0 ? source : source[..idx] + replacement + source[(idx + needle.Length)..];
    }

    private static System.Text.RegularExpressions.Regex GlobToRegex(string glob) =>
        new(
            "^" + System.Text.RegularExpressions.Regex.Escape(glob).Replace(@"\*", "[^/\\\\]*").Replace(@"\?", ".") + "$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    private IReadOnlyList<ToolDefinition> BuildDefinitions() => new List<ToolDefinition>
    {
        new()
        {
            Name = "read_file",
            Description = "Read a text file inside the workspace. Returns numbered lines. Large files: use offset/limit or grep_search first.",
            ParametersJsonSchema = """{"type":"object","properties":{"path":{"type":"string","description":"workspace-relative or absolute path"},"offset":{"type":"integer","description":"1-based start line"},"limit":{"type":"integer","description":"max lines (1..5000)"}},"required":["path"]}""",
        },
        new()
        {
            Name = "write_file",
            Description = "Create or overwrite a file inside the workspace. A checkpoint is captured before writing.",
            ParametersJsonSchema = """{"type":"object","properties":{"path":{"type":"string"},"content":{"type":"string"},"force":{"type":"boolean","description":"allow writing into excluded dirs (.env etc)"}},"required":["path","content"]}""",
        },
        new()
        {
            Name = "edit_file",
            Description = "Exact string replacement in an existing file. old_string must occur exactly once unless replace_all is true. A checkpoint is captured first.",
            ParametersJsonSchema = """{"type":"object","properties":{"path":{"type":"string"},"old_string":{"type":"string"},"new_string":{"type":"string"},"replace_all":{"type":"boolean"}},"required":["path","old_string","new_string"]}""",
        },
        new()
        {
            Name = "delete_file",
            Description = "Delete a file, or a directory with recursive: true. A checkpoint is captured first.",
            ParametersJsonSchema = """{"type":"object","properties":{"path":{"type":"string"},"recursive":{"type":"boolean"}},"required":["path"]}""",
        },
        new()
        {
            Name = "list_directory",
            Description = "List one directory level (dirs first, then files with sizes). Excluded dirs (.git, node_modules, bin, obj...) are skipped.",
            ParametersJsonSchema = """{"type":"object","properties":{"path":{"type":"string","description":"defaults to workspace root"}}}""",
        },
        new()
        {
            Name = "search_files",
            Description = "Find files by name glob (e.g. *.cs) recursively under path, honoring exclusion rules.",
            ParametersJsonSchema = """{"type":"object","properties":{"pattern":{"type":"string"},"path":{"type":"string"},"max_results":{"type":"integer"}},"required":["pattern"]}""",
        },
        new()
        {
            Name = "grep_search",
            Description = "Search file contents. Literal by default; set regex: true for regular expressions. Returns file:line:text.",
            ParametersJsonSchema = """{"type":"object","properties":{"pattern":{"type":"string"},"path":{"type":"string"},"regex":{"type":"boolean"},"case_sensitive":{"type":"boolean"},"max_results":{"type":"integer"}},"required":["pattern"]}""",
        },
        new()
        {
            Name = "run_shell",
            Description = "Run a shell command with the workspace root as cwd. Timeout defaults to 60s; the process tree is killed on timeout. Dangerous commands are intercepted by the permission policy.",
            ParametersJsonSchema = """{"type":"object","properties":{"command":{"type":"string"},"timeout_seconds":{"type":"integer"}},"required":["command"]}""",
        },
    };
}
