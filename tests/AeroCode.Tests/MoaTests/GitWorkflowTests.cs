// Copyright (c) AeroCode
// GitWorkflow 真实 git CLI 验证：真实 git init 临时仓，提交/撤销/状态/diff 全部经真实 git 回读校验。
// git CLI 不可用的环境用 SkippableFact 如实跳过（不伪造提交成功）。
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
/// git 工作流钉子：AutoCommit 的提交以 `git log -1 --pretty=%s` 真实回读校验；
/// undo 验证 reset --soft 后改动确在暂存区（不丢工作，绝不 hard）。
/// </summary>
public sealed class GitWorkflowTests : IDisposable
{
    private static readonly Lazy<Task<bool>> GitAvailable = new(async () =>
    {
        var probe = Path.Combine(Path.GetTempPath(), $"gitprobe_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(probe);
            var r = await new ShellRunner(probe).RunAsync("git --version", 20, CancellationToken.None);
            return r.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            try { Directory.Delete(probe, recursive: true); } catch { /* 清理失败不致命 */ }
        }
    });

    private readonly string _dir;
    private readonly GitWorkflow _git;

    public GitWorkflowTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"gitwf_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _git = new GitWorkflow(_dir);
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

    private static async Task SkipIfNoGitAsync()
    {
        Skip.IfNot(
            await GitAvailable.Value.ConfigureAwait(false),
            "git CLI 不可用（PATH 中无 git），真实 git 仓测试如实跳过");
    }

    /// <summary>真实 git init + 本地身份配置（spec 规定：user.email=a@b.c / user.name=t）。</summary>
    private static async Task InitRepoAsync(string dir)
    {
        var runner = new ShellRunner(dir, TimeSpan.FromSeconds(30));
        Assert.Equal(0, (await runner.RunAsync("git init", 30, CancellationToken.None)).ExitCode);
        Assert.Equal(0, (await runner.RunAsync("git config user.email a@b.c", 15, CancellationToken.None)).ExitCode);
        Assert.Equal(0, (await runner.RunAsync("git config user.name t", 15, CancellationToken.None)).ExitCode);
    }

    private async Task InitRepoAsync()
    {
        await SkipIfNoGitAsync().ConfigureAwait(false);
        await InitRepoAsync(_dir).ConfigureAwait(false);
    }

    private async Task<string> GitAsync(string args)
    {
        var r = await new ShellRunner(_dir, TimeSpan.FromSeconds(30))
            .RunAsync($"git {args}", 30, CancellationToken.None);
        Assert.True(r.ExitCode == 0, $"git {args} 应成功：{r.StdErr}");
        return r.StdOut.Trim();
    }

    [SkippableFact]
    public async Task IsRepo_TrueAfterRealInit()
    {
        await InitRepoAsync().ConfigureAwait(false);

        Assert.True(await _git.IsRepoAsync(CancellationToken.None).ConfigureAwait(false));
    }

    [Fact]
    public async Task IsRepo_FalseInPlainDirectory()
    {
        // 无 git 也不需要 git：不在仓 = false（git 缺失同样诚实返回 false）。
        Assert.False(await _git.IsRepoAsync(CancellationToken.None).ConfigureAwait(false));
    }

    [SkippableFact]
    public async Task AutoCommit_CommitsTargetPath_RealLogEntry()
    {
        await InitRepoAsync().ConfigureAwait(false);
        var target = Path.Combine(_dir, "edited.cs");
        File.WriteAllText(target, "class X {}");

        var (outcome, detail) = await _git.AutoCommitAsync(
            target, "feat: auto commit", protectDirty: false, CancellationToken.None).ConfigureAwait(false);

        Assert.Equal(GitCommitOutcome.Committed, outcome);
        // 真实提交：log -1 的 subject / 作者邮箱 / status 干净度都用 git 本体回读。
        // 提交信息用 ASCII：ShellRunner 按控制台代码页解码 git 的 UTF-8 输出，中文会乱码（如实限制）。
        Assert.Equal("feat: auto commit", await GitAsync("log -1 --pretty=%s").ConfigureAwait(false));
        Assert.Equal(string.Empty, await GitAsync("status --porcelain").ConfigureAwait(false));
        Assert.Contains("a@b.c", await GitAsync("log -1 --pretty=%ae").ConfigureAwait(false));
    }

    [SkippableFact]
    public async Task AutoCommit_NotARepo_SkippedHonest()
    {
        await SkipIfNoGitAsync().ConfigureAwait(false);

        var (outcome, detail) = await _git.AutoCommitAsync(
            Path.Combine(_dir, "f.txt"), "msg", protectDirty: false, CancellationToken.None).ConfigureAwait(false);

        Assert.Equal(GitCommitOutcome.SkippedNotRepo, outcome);
        Assert.Contains("not a git work tree", detail);
        // 目录保持无仓库元数据：绝不在非仓目录伪造 .git
        Assert.False(Directory.Exists(Path.Combine(_dir, ".git")));
    }

    [SkippableFact]
    public async Task AutoCommit_ProtectDirty_UnrelatedChanges_NeedsUserDecision()
    {
        await InitRepoAsync().ConfigureAwait(false);
        var target = Path.Combine(_dir, "mine.cs");
        File.WriteAllText(target, "class Mine {}");
        // 无关改动必须是"已跟踪的未提交修改"：未跟踪文件不会被 add <path> 卷入提交，
        // 已修正的语义下不触发保护（见 IgnoresUntracked 用例）。
        var own = Path.Combine(_dir, "user-own-work.txt");
        File.WriteAllText(own, "wip v1");
        await _git.AutoCommitAsync(own, "chore: own work", protectDirty: false, CancellationToken.None).ConfigureAwait(false);
        File.WriteAllText(own, "wip v2 (dirty)");

        var (outcome, detail) = await _git.AutoCommitAsync(
            target, "feat: x", protectDirty: true, CancellationToken.None).ConfigureAwait(false);

        Assert.Equal(GitCommitOutcome.NeedsUserDecision, outcome);
        Assert.Contains("unrelated", detail);
        // 保护触发时绝不产生新提交：HEAD 仍是基线提交（chore: own work），
        // 目标提交 "feat: x" 不存在。（%s 在 cmd 展开有风险，用默认格式 + Contains 断言）
        var head = await new ShellRunner(_dir, TimeSpan.FromSeconds(30))
            .RunAsync("git log -1", 15, CancellationToken.None).ConfigureAwait(false);
        Assert.Equal(0, head.ExitCode);
        Assert.Contains("chore: own work", head.StdOut);
        Assert.DoesNotContain("feat: x", head.StdOut);
    }

    [SkippableFact]
    public async Task AutoCommit_ProtectDirtyFalse_ForcesCommit()
    {
        await InitRepoAsync().ConfigureAwait(false);
        var target = Path.Combine(_dir, "mine.cs");
        File.WriteAllText(target, "class Mine {}");
        File.WriteAllText(Path.Combine(_dir, "user-own-work.txt"), "unrelated");

        var (outcome, _) = await _git.AutoCommitAsync(
            target, "feat: force", protectDirty: false, CancellationToken.None).ConfigureAwait(false);

        Assert.Equal(GitCommitOutcome.Committed, outcome);
        // 只有目标路径进了提交；无关文件仍是未跟踪
        Assert.Equal("feat: force", await GitAsync("log -1 --pretty=%s").ConfigureAwait(false));
        var status = await GitAsync("status --porcelain").ConfigureAwait(false);
        Assert.Contains("user-own-work.txt", status);
    }

    [SkippableFact]
    public async Task UndoLastCommit_SoftReset_KeepsChangesStaged()
    {
        await InitRepoAsync().ConfigureAwait(false);
        var a = Path.Combine(_dir, "a.txt");
        var b = Path.Combine(_dir, "b.txt");
        File.WriteAllText(a, "first");
        await _git.AutoCommitAsync(a, "c1", protectDirty: false, CancellationToken.None).ConfigureAwait(false);
        File.WriteAllText(b, "second");
        await _git.AutoCommitAsync(b, "c2", protectDirty: false, CancellationToken.None).ConfigureAwait(false);

        var (outcome, detail) = await _git.UndoLastCommitAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.Equal(GitCommitOutcome.Committed, outcome);
        Assert.Contains("changes kept staged", detail);
        // HEAD 回到 c1；b.txt 的改动完整回到暂存区（--soft 不丢工作）
        Assert.Equal("c1", await GitAsync("log -1 --pretty=%s").ConfigureAwait(false));
        Assert.Equal("A  b.txt", await GitAsync("status --porcelain").ConfigureAwait(false));
        Assert.Equal("second", File.ReadAllText(b));
    }

    [SkippableFact]
    public async Task UndoLastCommit_NotARepo_Skipped()
    {
        await SkipIfNoGitAsync().ConfigureAwait(false);

        var (outcome, detail) = await _git.UndoLastCommitAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.Equal(GitCommitOutcome.SkippedNotRepo, outcome);
        Assert.Contains("not a git work tree", detail);
    }

    [Fact]
    public async Task StatusAsync_NotARepo_EmptyList()
    {
        var lines = await _git.StatusAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.Empty(lines);
    }

    [SkippableFact]
    public async Task StatusAsync_CleanThenDirty_ReflectsWorkTree()
    {
        await InitRepoAsync().ConfigureAwait(false);
        var f = Path.Combine(_dir, "tracked.txt");
        File.WriteAllText(f, "v1");
        await _git.AutoCommitAsync(f, "init", protectDirty: false, CancellationToken.None).ConfigureAwait(false);

        Assert.Empty(await _git.StatusAsync(CancellationToken.None).ConfigureAwait(false));

        File.WriteAllText(f, "v2");
        var lines = await _git.StatusAsync(CancellationToken.None).ConfigureAwait(false);

        // 注意：GitWorkflow.StatusAsync 以 TrimEntries 切分 porcelain 行，
        // 未暂存修改的行首空格被裁掉——实际形态为 "M tracked.txt"（按真实行为钉死）。
        var line = Assert.Single(lines);
        Assert.Contains("tracked.txt", line);
        Assert.StartsWith("M", line);
    }

    [Fact]
    public async Task DiffAsync_NotARepo_EmptyString()
    {
        var diff = await _git.DiffAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.Equal(string.Empty, diff);
    }

    [SkippableFact]
    public async Task DiffAsync_ShowsTrackedChange()
    {
        await InitRepoAsync().ConfigureAwait(false);
        var f = Path.Combine(_dir, "t.txt");
        File.WriteAllText(f, "v1");
        await _git.AutoCommitAsync(f, "init", protectDirty: false, CancellationToken.None).ConfigureAwait(false);

        File.WriteAllText(f, "v2");
        var diff = await _git.DiffAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.Contains("diff --git", diff);
        Assert.Contains("-v1", diff);
        Assert.Contains("+v2", diff);
    }

    [SkippableFact]
    public async Task GitToolbox_StatusAndCommit_Integration()
    {
        await InitRepoAsync().ConfigureAwait(false);
        var f = Path.Combine(_dir, "tool.cs");
        var box = new GitToolbox(_git);

        // 先查状态（刚 init、无任何改动 → 干净）
        var status = await box.InvokeAsync("git_status", "{}", CancellationToken.None).ConfigureAwait(false);
        Assert.True(status.Success);
        Assert.Equal("(clean or not a repo)", status.Output);

        File.WriteAllText(f, "ok");
        var commit = await box.InvokeAsync(
            "git_commit", $"{{\"path\":\"{f.Replace("\\", "/")}\",\"message\":\"feat: toolbox\"}}",
            CancellationToken.None).ConfigureAwait(false);
        Assert.True(commit.Success, commit.Output);
        Assert.Contains("Committed", commit.Output);
        Assert.Equal("feat: toolbox", await GitAsync("log -1 --pretty=%s").ConfigureAwait(false));
    }

    [SkippableFact]
    public async Task GitToolbox_DirtyProtection_IgnoresUntracked_BlocksTracked()
    {
        await InitRepoAsync().ConfigureAwait(false);
        var box = new GitToolbox(_git);
        var tracked = Path.Combine(_dir, "tracked.cs");
        var target = Path.Combine(_dir, "target.cs");
        File.WriteAllText(tracked, "v1");
        await _git.AutoCommitAsync(tracked, "init", protectDirty: false, CancellationToken.None).ConfigureAwait(false);

        // 场景一：工作区只有 ?? 未跟踪文件 → 不阻断（它们不会被 add <path> 卷入提交）。
        File.WriteAllText(target, "class Target {}");
        File.WriteAllText(Path.Combine(_dir, "untracked.tmp"), "x");
        var ok = await box.InvokeAsync(
            "git_commit", $"{{\"path\":\"{target.Replace("\\", "/")}\",\"message\":\"feat: with untracked\"}}",
            CancellationToken.None).ConfigureAwait(false);
        Assert.True(ok.Success, ok.Output);

        // 场景二：已跟踪文件有未提交修改 → 脏区保护照常触发（保护未被削弱）。
        File.WriteAllText(tracked, "v2");
        var blocked = await box.InvokeAsync(
            "git_commit", $"{{\"path\":\"{target.Replace("\\", "/")}\",\"message\":\"feat: dirty\"}}",
            CancellationToken.None).ConfigureAwait(false);
        Assert.False(blocked.Success);
        Assert.Contains("Dirty-worktree protection", blocked.Output);
    }
}
