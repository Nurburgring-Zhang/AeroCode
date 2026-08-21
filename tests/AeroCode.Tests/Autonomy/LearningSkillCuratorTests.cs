// SkillCurator tests: usage stats from the real SkillRegistry, degraded-flag persistence,
// and REAL file operations for archive / backup / rollback.
using AeroAgent.Autonomy.Learning;
using AeroCode.Skills.Loader;
using AeroCode.Skills.Registry;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AeroCode.Tests.Autonomy;

/// <summary>Test double: file-backed skill registered under a given id (hand-written ISkill).</summary>
internal sealed class FileSkillStub : ISkill
{
    public FileSkillStub(string id, string name = "Stub Skill")
    {
        Id = id;
        Name = name;
    }

    public string Id { get; }
    public string Name { get; }
    public string Description => "Hand-written test skill.";
    public string Category => "user";
    public string Author => "tester";
    public string Version => "1.0.0";
    public IReadOnlyList<string> Tags => Array.Empty<string>();
    public string GetSystemPrompt() => Name;
    public bool IsAvailable() => true;

    public Task<SkillResult> ExecuteAsync(SkillInput input, SkillContext ctx, CancellationToken ct = default)
        => Task.FromResult(new SkillResult { Text = "ok", Success = true });
}

public sealed class LearningSkillCuratorTests : IDisposable
{
    private readonly LearningEnv _env = new();
    private readonly string _skillsRoot;
    private readonly SkillRegistry _registry = new();
    private readonly SkillLoader _loader;
    private readonly SkillCurator _curator;
    private readonly LearningDbContext _curatorDb;

    public LearningSkillCuratorTests()
    {
        _skillsRoot = Path.Combine(_env.Root, "skills-root");
        _loader = new SkillLoader(_registry);
        _curatorDb = _env.NewLearningDb();
        _curator = new SkillCurator(_registry, _loader, _curatorDb, _env.LearningPaths);
    }

    public void Dispose()
    {
        _curator.Dispose();
        _curatorDb.Dispose();
        _env.Dispose();
    }

    /// <summary>Writes a real SKILL.md tree and loads it; returns the derived skill id.</summary>
    private string SeedSkillOnDisk(string group, string name, bool withReferenceFile = false)
    {
        var dir = Path.Combine(_skillsRoot, "skills", group, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "SKILL.md"),
            "---\n" +
            $"name: {name}\n" +
            "description: A real on-disk skill for curation tests.\n" +
            "version: 1.0.0\n" +
            "author: tester\n" +
            "license: MIT\n" +
            "---\n\n" +
            $"# {name}\n\nDo the thing.\n");
        if (withReferenceFile)
        {
            Directory.CreateDirectory(Path.Combine(dir, "references"));
            File.WriteAllText(Path.Combine(dir, "references", "guide.md"), "# guide\nstep 1\n");
        }

        var loaded = _loader.LoadFromDirectory(_skillsRoot, "user");
        Assert.True(loaded >= 1, "SKILL.md must be loadable");
        return $"{group}/{name}";
    }

    [Fact]
    public void CollectUsageReport_ReflectsRealInvocationStats()
    {
        _registry.Register(new FileSkillStub("demo/alpha"));
        _registry.Register(new FileSkillStub("demo/beta"));
        _registry.RecordInvocation("demo/alpha", success: true);
        _registry.RecordInvocation("demo/alpha", success: false);
        _registry.RecordInvocation("demo/beta", success: true);

        var report = _curator.CollectUsageReport();

        var alpha = Assert.Single(report, u => u.SkillId == "demo/alpha");
        var beta = Assert.Single(report, u => u.SkillId == "demo/beta");
        Assert.Equal(2, alpha.Invocations);
        Assert.Equal(0.5, alpha.SuccessRate, 5);
        Assert.Equal(1, beta.Invocations);
        Assert.Equal(1.0, beta.SuccessRate, 5);
    }

    [Fact]
    public async Task MarkDegraded_FlagsLowSuccessRate_PersistsAndSkipsHealthy()
    {
        _registry.Register(new FileSkillStub("demo/flaky"));
        _registry.Register(new FileSkillStub("demo/healthy"));
        _registry.Register(new FileSkillStub("demo/rare"));
        for (var i = 0; i < 3; i++) _registry.RecordInvocation("demo/flaky", success: false);
        _registry.RecordInvocation("demo/flaky", success: true); // 1/4 = 25%
        for (var i = 0; i < 3; i++) _registry.RecordInvocation("demo/healthy", success: true);
        _registry.RecordInvocation("demo/rare", success: false); // only 1 call → below min invocations

        var flagged = await _curator.MarkDegradedAsync(minInvocations: 3, maxSuccessRate: 0.5);

        Assert.Equal(new[] { "demo/flaky" }, flagged);
        Assert.True(await _curator.IsDegradedAsync("demo/flaky"));
        Assert.False(await _curator.IsDegradedAsync("demo/healthy"));
        Assert.False(await _curator.IsDegradedAsync("demo/rare"));

        var listed = await _curator.ListDegradedAsync();
        var entry = Assert.Single(listed);
        Assert.Equal("demo/flaky", entry.SkillId);
        Assert.Contains("25%", entry.Reason);

        // 持久化真实性：全新上下文读到同一标记。
        using var fresh = _env.NewLearningDb();
        Assert.True(await fresh.SkillFlags.AnyAsync(f => f.SkillId == "demo/flaky" && f.Flag == SkillCurator.DegradedFlag));
    }

    [Fact]
    public async Task ClearDegraded_RemovesTheFlag()
    {
        _registry.Register(new FileSkillStub("demo/flaky2"));
        for (var i = 0; i < 3; i++) _registry.RecordInvocation("demo/flaky2", success: false);
        await _curator.MarkDegradedAsync();

        Assert.True(await _curator.ClearDegradedAsync("demo/flaky2"));
        Assert.False(await _curator.IsDegradedAsync("demo/flaky2"));
        Assert.False(await _curator.ClearDegradedAsync("demo/flaky2")); // second clear: honest false
    }

    [Fact]
    public void Archive_MovesSkillMd_CreatesBackup_Unregisters_RealFileOps()
    {
        var id = SeedSkillOnDisk("demo", "archiver", withReferenceFile: true);
        _registry.Register(new FileSkillStub(id));
        var originalSkillFile = Path.Combine(_skillsRoot, "skills", "demo", "archiver", "SKILL.md");
        var originalRefFile = Path.Combine(_skillsRoot, "skills", "demo", "archiver", "references", "guide.md");
        Assert.True(File.Exists(originalSkillFile));

        var result = _curator.ArchiveSkill(id);

        Assert.True(result.Success, result.Error);
        Assert.False(File.Exists(originalSkillFile));                    // SKILL.md 真实移走
        Assert.NotNull(result.ArchivedSkillFile);
        Assert.True(File.Exists(result.ArchivedSkillFile));              // archive 目录真实存在
        Assert.Contains(_env.LearningPaths.SkillArchiveDirectory, result.ArchivedSkillFile!);
        Assert.True(File.Exists(Path.Combine(result.BackupDirectory!, "SKILL.md")));          // 备份含 SKILL.md
        Assert.True(File.Exists(Path.Combine(result.BackupDirectory!, "references", "guide.md"))); // 备份含整目录
        Assert.True(File.Exists(Path.Combine(result.BackupDirectory!, "backup-meta.json")));
        Assert.True(result.Unregistered);
        Assert.Null(_registry.Get(id));
        Assert.True(File.Exists(originalRefFile)); // 非 SKILL.md 文件保持原位（备份已覆盖）
    }

    [Fact]
    public void Archive_UnknownOrMissingSkill_ReturnsHonestFailure()
    {
        var result = _curator.ArchiveSkill("demo/ghost");
        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Contains("demo/ghost", result.Error);
    }

    [Fact]
    public void Rollback_RestoresFilesFromBackup_RealFileOps()
    {
        var id = SeedSkillOnDisk("demo", "rollback-me", withReferenceFile: true);
        _registry.Register(new FileSkillStub(id));
        var originalSkillFile = Path.Combine(_skillsRoot, "skills", "demo", "rollback-me", "SKILL.md");
        var originalRefFile = Path.Combine(_skillsRoot, "skills", "demo", "rollback-me", "references", "guide.md");
        var originalContent = File.ReadAllText(originalSkillFile);
        var archive = _curator.ArchiveSkill(id);
        Assert.True(archive.Success, archive.Error);
        Assert.False(File.Exists(originalSkillFile));

        var rollback = _curator.RollbackSkill(id);

        Assert.True(rollback.Success, rollback.Error);
        Assert.True(rollback.RestoredFileCount >= 2); // SKILL.md + references/guide.md
        Assert.True(File.Exists(originalSkillFile));
        Assert.Equal(originalContent, File.ReadAllText(originalSkillFile));
        Assert.True(File.Exists(originalRefFile));
    }

    [Fact]
    public void Rollback_WithoutBackup_ReturnsHonestFailure()
    {
        var result = _curator.RollbackSkill("demo/never-archived");
        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Contains("备份", result.Error);
    }

    [Fact]
    public void Archive_Twice_KeepsBothBackups_AndDistinctArchiveFiles()
    {
        var id = SeedSkillOnDisk("demo", "twice");
        _registry.Register(new FileSkillStub(id));

        var first = _curator.ArchiveSkill(id);
        Assert.True(first.Success, first.Error);

        // Re-seed the file (simulates rollback + further usage) and archive again.
        var dir = Path.Combine(_skillsRoot, "skills", "demo", "twice");
        File.WriteAllText(Path.Combine(dir, "SKILL.md"),
            "---\nname: twice\ndescription: A real on-disk skill for curation tests.\nversion: 1.1.0\nauthor: tester\nlicense: MIT\n---\n\nbody v2\n");
        var second = _curator.ArchiveSkill(id);

        Assert.True(second.Success, second.Error);
        Assert.NotEqual(first.BackupDirectory, second.BackupDirectory);
        Assert.NotEqual(first.ArchivedSkillFile, second.ArchivedSkillFile);

        // Rollback restores from the LATEST backup (v2 content).
        var rollback = _curator.RollbackSkill(id);
        Assert.True(rollback.Success, rollback.Error);
        Assert.Contains("body v2", File.ReadAllText(Path.Combine(dir, "SKILL.md")));
    }
}
