// Copyright (c) AeroCode
// GitWorkflow — 编辑后 Git 工作流（对标 aider git 自动提交 / openhands git-control-bar / cline diff 审查）。
// 真实调用 git CLI（PATH 解析）；不在 git 仓时一切操作如实跳过（不伪造提交）。
// 安全边界：git_push 不在本批执行器范围（规则表保持休眠——远端副作用仅经既有审批面）；
// undo 用 reset --soft（改动回暂存区，不丢工作，绝不 reset --hard）。
using System.Text;
using System.Text.Json;
using AeroCode.AI.Models;

namespace AeroAgent.Moa.Tools.Workspace;

/// <summary>自动提交结果（诚实四态，不冒充）。</summary>
public enum GitCommitOutcome
{
    /// <summary>已真实提交。</summary>
    Committed,
    /// <summary>不在 git 仓（或 git 不可用），什么都没做。</summary>
    SkippedNotRepo,
    /// <summary>脏区保护触发：存在与本次编辑无关的未暂存改动，需用户决定。</summary>
    NeedsUserDecision,
    /// <summary>git 返回非零，携带 stderr。</summary>
    Failed,
}

/// <summary>
/// git CLI 工作流：<see cref="AutoCommitAsync"/>（编辑后自动提交，含脏区保护）、
/// <see cref="UndoLastCommitAsync"/>（reset --soft）、status/diff 读取。
/// </summary>
public sealed class GitWorkflow
{
    private readonly string _workingDirectory;

    public GitWorkflow(string workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            throw new ArgumentException("working directory must not be empty", nameof(workingDirectory));
        }

        _workingDirectory = workingDirectory;
    }

    /// <summary>当前目录是否在 git 工作树内（git 缺失也返回 false）。</summary>
    public async Task<bool> IsRepoAsync(CancellationToken ct)
    {
        var r = await RunAsync("rev-parse --is-inside-work-tree", ct).ConfigureAwait(false);
        return r.ExitCode == 0 && r.StdOut.Trim() == "true";
    }

    /// <summary>porcelain status 行（不在仓返回空）。</summary>
    public async Task<IReadOnlyList<string>> StatusAsync(CancellationToken ct)
    {
        if (!await IsRepoAsync(ct).ConfigureAwait(false))
        {
            return Array.Empty<string>();
        }

        var r = await RunAsync("status --porcelain", ct).ConfigureAwait(false);
        return r.ExitCode != 0
            ? Array.Empty<string>()
            : r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>相对 HEAD 的全部差异（含暂存与未暂存）。</summary>
    public async Task<string> DiffAsync(CancellationToken ct)
    {
        var r = await RunAsync("diff HEAD", ct).ConfigureAwait(false);
        return r.ExitCode != 0 ? string.Empty : r.StdOut;
    }

    /// <summary>
    /// 编辑后自动提交：add 指定路径 + commit。<paramref name="protectDirty"/>=true 且
    /// 工作树中存在除目标路径之外的未暂存改动时返回 <see cref="GitCommitOutcome.NeedsUserDecision"/>
    /// （避免把用户自己的半成品一起卷进提交）。
    /// </summary>
    public async Task<(GitCommitOutcome Outcome, string Detail)> AutoCommitAsync(
        string committedPath, string message, bool protectDirty, CancellationToken ct)
    {
        if (!await IsRepoAsync(ct).ConfigureAwait(false))
        {
            return (GitCommitOutcome.SkippedNotRepo, "not a git work tree");
        }

        if (protectDirty)
        {
            var status = await StatusAsync(ct).ConfigureAwait(false);
            var target = Normalize(committedPath);
            var others = status
                // 未跟踪文件（?? 前缀）不构成"被卷入"风险：AutoCommit 只 add 目标路径，
                // 它们不会进入本次提交——阻断它们是过严保护（真实工作区常有未跟踪文件）。
                .Where(line => !line.StartsWith("??", StringComparison.Ordinal))
                .Where(line => !line.EndsWith(target, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (others.Count > 0)
            {
                return (GitCommitOutcome.NeedsUserDecision,
                    $"{others.Count} unrelated change(s) pending, e.g. {others[0]}");
            }
        }

        var add = await RunAsync($"add -- \"{Quote(Normalize(committedPath))}\"", ct).ConfigureAwait(false);
        if (add.ExitCode != 0)
        {
            return (GitCommitOutcome.Failed, add.StdErr);
        }

        var commit = await RunAsync($"commit -m {Quote(message)}", ct).ConfigureAwait(false);
        return commit.ExitCode == 0
            ? (GitCommitOutcome.Committed, commit.StdOut.Trim())
            : (GitCommitOutcome.Failed, commit.StdErr);
    }

    /// <summary>撤销最近一次提交（reset --soft HEAD~1：改动回暂存区，绝不 hard）。</summary>
    public async Task<(GitCommitOutcome Outcome, string Detail)> UndoLastCommitAsync(CancellationToken ct)
    {
        if (!await IsRepoAsync(ct).ConfigureAwait(false))
        {
            return (GitCommitOutcome.SkippedNotRepo, "not a git work tree");
        }

        var r = await RunAsync("reset --soft HEAD~1", ct).ConfigureAwait(false);
        return r.ExitCode == 0
            ? (GitCommitOutcome.Committed, "last commit undone (changes kept staged)")
            : (GitCommitOutcome.Failed, r.StdErr);
    }

    private async Task<ShellResult> RunAsync(string args, CancellationToken ct)
    {
        var runner = new ShellRunner(_workingDirectory);
        return await runner.RunAsync($"git {args}", timeoutSeconds: 30, ct).ConfigureAwait(false);
    }

    private static string Normalize(string path) =>
        Path.GetFullPath(path).Replace('\\', '/');

    private static string Quote(string s) => "\"" + s.Replace("\"", "\\\"") + "\"";
}

/// <summary>
/// git 只读/提交工具域：git_status / git_diff / git_commit（message 必填）/ git_undo。
/// git_push 不在本域（规则表休眠）——远端副作用走既有审批面，如实注明。
/// </summary>
public sealed class GitToolbox : IWorkerToolbox
{
    private readonly GitWorkflow _git;

    public GitToolbox(GitWorkflow git)
    {
        _git = git ?? throw new ArgumentNullException(nameof(git));
    }

    public string Domain => "git";

    public IReadOnlyList<ToolDefinition> Definitions { get; } = new[]
    {
        new ToolDefinition
        {
            Name = "git_status",
            Description = "Git porcelain status (empty when not a repo).",
            ParametersJsonSchema = """{"type":"object","properties":{}}""",
        },
        new ToolDefinition
        {
            Name = "git_diff",
            Description = "Git diff of the whole work tree vs HEAD.",
            ParametersJsonSchema = """{"type":"object","properties":{}}""",
        },
        new ToolDefinition
        {
            Name = "git_commit",
            Description = "Stage one committed path and commit with a Conventional-Commits style message. Requires user approval by default.",
            ParametersJsonSchema = """{"type":"object","properties":{"path":{"type":"string"},"message":{"type":"string"},"protect_dirty":{"type":"boolean"}},"required":["path","message"]}""",
        },
        new ToolDefinition
        {
            Name = "git_undo",
            Description = "Undo the last commit keeping changes staged (reset --soft). Requires user approval by default.",
            ParametersJsonSchema = """{"type":"object","properties":{}}""",
        },
    };

    public async Task<ToolInvokeResult> InvokeAsync(string toolName, string argumentsJson, CancellationToken ct)
    {
        string? path;
        string? message;
        var protect = true;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            var root = doc.RootElement;
            path = root.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
            message = root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
            if (root.TryGetProperty("protect_dirty", out var pd) && pd.ValueKind == JsonValueKind.False)
            {
                protect = false;
            }
        }
        catch (JsonException ex)
        {
            return ToolInvokeResult.Fail($"Invalid arguments JSON: {ex.Message}");
        }

        switch (toolName)
        {
            case "git_status":
            {
                var lines = await _git.StatusAsync(ct).ConfigureAwait(false);
                return ToolInvokeResult.Ok(lines.Count == 0 ? "(clean or not a repo)" : string.Join('\n', lines));
            }
            case "git_diff":
            {
                var diff = await _git.DiffAsync(ct).ConfigureAwait(false);
                return ToolInvokeResult.Ok(diff.Length == 0 ? "(no diff vs HEAD)" : diff);
            }
            case "git_commit":
            {
                if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(message))
                {
                    return ToolInvokeResult.Fail("git_commit requires 'path' and 'message'");
                }

                var (outcome, detail) = await _git.AutoCommitAsync(path, message, protect, ct).ConfigureAwait(false);
                return outcome switch
                {
                    GitCommitOutcome.Committed => ToolInvokeResult.Ok($"Committed. {detail}"),
                    GitCommitOutcome.SkippedNotRepo => ToolInvokeResult.Fail($"Not committed: {detail}"),
                    GitCommitOutcome.NeedsUserDecision => ToolInvokeResult.Fail(
                        $"Dirty-worktree protection: {detail}. Commit only your own change or ask the user."),
                    _ => ToolInvokeResult.Fail($"git commit failed: {detail}"),
                };
            }
            case "git_undo":
            {
                var (outcome, detail) = await _git.UndoLastCommitAsync(ct).ConfigureAwait(false);
                return outcome == GitCommitOutcome.Committed
                    ? ToolInvokeResult.Ok(detail)
                    : ToolInvokeResult.Fail($"git undo failed: {detail}");
            }
            default:
                return ToolInvokeResult.Fail($"Unknown git tool '{toolName}'");
        }
    }
}
