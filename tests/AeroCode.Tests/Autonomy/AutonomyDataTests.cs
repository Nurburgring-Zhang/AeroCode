// MissionStore + ExperienceInjector + RetrospectiveEngine tests — real SQLite
// persistence in temp directories (EF Core Sqlite, the production stack).
using AeroAgent.Autonomy.Data;
using AeroAgent.Autonomy.Experience;
using AeroAgent.Autonomy.Mission;
using AeroAgent.Autonomy.Retrospective;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AeroCode.Tests.Autonomy;

public sealed class AutonomyPersistenceFixture : IDisposable
{
    public AutonomyDataPaths Paths { get; }
    public AutonomyDbContext Db { get; }
    public MissionStore Store { get; }

    public AutonomyPersistenceFixture()
    {
        Paths = new AutonomyDataPaths(
            Path.Combine(Path.GetTempPath(), "aerocode-autonomy-" + Guid.NewGuid().ToString("N")));
        Paths.EnsureDirectories();
        Db = new AutonomyDbContext(
            new DbContextOptionsBuilder<AutonomyDbContext>().UseSqlite($"Data Source={Paths.DatabaseFile}").Options);
        Store = new MissionStore(Db);
        Store.EnsureCreatedAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        Db.Dispose();
        Store.Dispose();
        try { if (Directory.Exists(Paths.RootDirectory)) Directory.Delete(Paths.RootDirectory, true); } catch { }
    }
}

public sealed class MissionStoreTests : IDisposable
{
    private readonly AutonomyPersistenceFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [Fact]
    public async Task UpsertAndGet_RoundTripsAllCoreFields()
    {
        var mission = new MissionRecord
        {
            TaskText = "真实任务文本",
            State = MissionState.Executing,
            Outcome = MissionOutcome.Pending,
            Strategy = "Router",
            StrategyRationale = "研究类任务",
            AnalysisJson = "{\"Type\":1}",
        };

        await _fx.Store.UpsertMissionAsync(mission);
        var loaded = await _fx.Store.GetMissionAsync(mission.Id);

        Assert.NotNull(loaded);
        Assert.Equal("真实任务文本", loaded!.TaskText);
        Assert.Equal(MissionState.Executing, loaded.State);
        Assert.Equal("Router", loaded.Strategy);
        Assert.Equal("{\"Type\":1}", loaded.AnalysisJson);
    }

    [Fact]
    public async Task Upsert_SameId_UpdatesInsteadOfDuplicating()
    {
        var mission = new MissionRecord { TaskText = "v1" };
        await _fx.Store.UpsertMissionAsync(mission);
        mission.TaskText = "v2";
        mission.State = MissionState.Retrospective;
        await _fx.Store.UpsertMissionAsync(mission);

        var all = await _fx.Store.ListMissionsAsync();
        Assert.Single(all);
        Assert.Equal("v2", all[0].TaskText);
        Assert.Equal(MissionState.Retrospective, all[0].State);
    }

    [Fact]
    public async Task Lessons_WriteAndReadBack_InRecencyOrder()
    {
        var older = new LessonRecord { MissionId = "m1", Phase = "Executing", Gap = "older gap", Suggestion = "s1" };
        var newer = new LessonRecord { MissionId = "m1", Phase = "Verifying", Gap = "newer gap", Suggestion = "s2" };
        older.CreatedAtUtc = DateTime.UtcNow.AddMinutes(-10);

        var written = await _fx.Store.AddLessonsAsync(new[] { older, newer });
        Assert.Equal(2, written);

        var recent = await _fx.Store.GetRecentLessonsAsync(10);
        Assert.Equal(2, recent.Count);
        Assert.Equal("newer gap", recent[0].Gap); // most recent first

        var byMission = await _fx.Store.GetLessonsByMissionAsync("m1");
        Assert.Equal(2, byMission.Count);
    }

    [Fact]
    public async Task GetMission_UnknownId_ReturnsNull()
    {
        Assert.Null(await _fx.Store.GetMissionAsync("no-such-id"));
    }

    [Fact]
    public async Task ListMissions_RespectsLimit_AndOrdersByCreatedDesc()
    {
        for (var i = 0; i < 5; i++)
        {
            await _fx.Store.UpsertMissionAsync(new MissionRecord
            {
                TaskText = $"task {i}",
                CreatedAtUtc = DateTime.UtcNow.AddSeconds(i),
            });
        }

        var top2 = await _fx.Store.ListMissionsAsync(limit: 2);
        Assert.Equal(2, top2.Count);
        Assert.Equal("task 4", top2[0].TaskText);
    }
}

public sealed class ExperienceInjectorTests : IDisposable
{
    private readonly AutonomyPersistenceFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [Fact]
    public async Task Lessons_AreReallyInjectedIntoSystemPrompt()
    {
        await _fx.Store.AddLessonsAsync(new[]
        {
            new LessonRecord { MissionId = "m", Phase = "Executing", Gap = "网络超时未重试导致失败", Suggestion = "增加重试", Severity = "warning" },
        });

        var injector = new ExperienceInjector(_fx.Store);
        var injection = await injector.BuildSystemPromptAsync(maxLessons: 5);

        Assert.Equal(1, injection.InjectedLessonCount);
        Assert.Contains("网络超时未重试导致失败", injection.SystemPrompt);
        Assert.Contains("增加重试", injection.SystemPrompt);
    }

    [Fact]
    public async Task NoLessons_NoFabricatedExperienceSection()
    {
        var injector = new ExperienceInjector(_fx.Store);
        var injection = await injector.BuildSystemPromptAsync(maxLessons: 5);

        Assert.Equal(0, injection.InjectedLessonCount);
        Assert.DoesNotContain("经验教训", injection.SystemPrompt);
    }

    [Fact]
    public async Task MaxLessonsZero_InjectsNothing()
    {
        await _fx.Store.AddLessonsAsync(new[]
        {
            new LessonRecord { MissionId = "m", Phase = "Executing", Gap = "gap", Suggestion = "s" },
        });
        var injector = new ExperienceInjector(_fx.Store);
        var injection = await injector.BuildSystemPromptAsync(maxLessons: 0);
        Assert.Equal(0, injection.InjectedLessonCount);
    }

    [Fact]
    public async Task MaxLessons_LimitsInjectedCount()
    {
        await _fx.Store.AddLessonsAsync(Enumerable.Range(0, 5).Select(i =>
            new LessonRecord { MissionId = "m", Phase = "Executing", Gap = $"gap {i}", Suggestion = "s" }));
        var injector = new ExperienceInjector(_fx.Store);
        var injection = await injector.BuildSystemPromptAsync(maxLessons: 2);
        Assert.Equal(2, injection.InjectedLessonCount);
    }
}

public sealed class RetrospectiveEngineTests : IDisposable
{
    private readonly AutonomyPersistenceFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private static MissionRecord FullRecord(bool executionOk, bool verificationPassed) => new()
    {
        TaskText = "完整链路任务",
        AnalysisJson = "{\"Type\":0}",
        ClarificationJson = "{\"AmbiguityScore\":0.1,\"UnansweredCount\":0}",
        SteelmanJson = "{\"DegradedToAutoApprove\": false}",
        PlanJson = "{\"Steps\":[{\"Title\":\"step\"}]}",
        ExecutionJson = executionOk ? "{\"Succeeded\":true}" : "{\"Succeeded\":false}",
        VerificationJson = verificationPassed ? "{\"Passed\": true}" : "{\"Passed\": false}",
        SessionId = "sess-1",
        Outcome = executionOk && verificationPassed ? MissionOutcome.Succeeded : MissionOutcome.Failed,
    };

    [Fact]
    public void AllPhasesAchieved_NoGaps()
    {
        var engine = new RetrospectiveEngine();
        var retro = engine.Evaluate(FullRecord(true, true),
            new MissionExecutionOutcome(true, false, "content", null, "sess-1", 2, 0.01));

        Assert.Empty(retro.Gaps);
        Assert.All(retro.PhaseReviews, r => Assert.True(r.Achieved));
        Assert.Contains("全部", retro.Summary);
    }

    [Fact]
    public void ExecutionFailure_ProducesCriticalGap()
    {
        var engine = new RetrospectiveEngine();
        var outcome = new MissionExecutionOutcome(false, false, "", "provider 401", "sess-1", 0, 0);
        var retro = engine.Evaluate(FullRecord(false, false), outcome);

        Assert.Contains(retro.Gaps, g => g.Severity == "critical" && g.Description.Contains("provider 401"));
    }

    [Fact]
    public void MissingPhases_AreFlaggedAsGaps()
    {
        var engine = new RetrospectiveEngine();
        var bare = new MissionRecord { TaskText = "裸记录" };
        var retro = engine.Evaluate(bare, null);

        // Analysis/Clarification/Steelman/Planning/Executing/Verifying all missing → gaps.
        Assert.True(retro.Gaps.Count >= 5);
        Assert.Contains(retro.PhaseReviews, r => r is { Phase: "Analyzed", Achieved: false });
    }

    [Fact]
    public void UnansweredClarifications_ProduceWarningGap()
    {
        var engine = new RetrospectiveEngine();
        var record = FullRecord(true, true);
        record.ClarificationJson = "{\"AmbiguityScore\":0.6,\"UnansweredCount\":2}";
        var retro = engine.Evaluate(record, new MissionExecutionOutcome(true, false, "ok", null, "s", 1, 0));

        Assert.Contains(retro.Gaps, g => g.Severity == "warning" && g.Description.Contains("2"));
    }

    [Fact]
    public void BuildLessons_OnePerGap_TraceableToMission()
    {
        var engine = new RetrospectiveEngine();
        var retro = engine.Evaluate(new MissionRecord { TaskText = "x", Id = "mid-1" }, null);
        var lessons = engine.BuildLessons(retro);

        Assert.Equal(retro.Gaps.Count, lessons.Count);
        Assert.All(lessons, l => Assert.Equal("mid-1", l.MissionId));
    }

    [Fact]
    public void WriteMarkdown_CreatesRealFile_WithPhaseReviews()
    {
        var engine = new RetrospectiveEngine();
        var record = FullRecord(true, true);
        var retro = engine.Evaluate(record, new MissionExecutionOutcome(true, false, "ok", null, "s", 1, 0));

        var path = engine.WriteMarkdown(_fx.Paths, retro, record);

        Assert.True(File.Exists(path));
        var md = File.ReadAllText(path);
        Assert.Contains("逐阶段评审", md);
        Assert.Contains(record.Id, md);
    }
}
