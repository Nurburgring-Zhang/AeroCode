// ExperienceBridge + SystemPromptBuilder tests, including ACCEPTANCE E2E ①:
// mission complete → lessons synced → ExperienceStore persisted (DB + md readable)
// → visible in the NEXT session's system prompt (pending → effective semantics).
using AeroAgent.Autonomy.Analysis;
using AeroAgent.Autonomy.Clarification;
using AeroAgent.Autonomy.Data;
using AeroAgent.Autonomy.Experience;
using AeroAgent.Autonomy.Learning;
using AeroAgent.Autonomy.Llm;
using AeroAgent.Autonomy.Mission;
using AeroAgent.Autonomy.Retrospective;
using AeroAgent.Autonomy.Steelman;
using AeroAgent.Conversation.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AeroCode.Tests.Autonomy;

public sealed class LearningBridgeAndPromptTests : IDisposable
{
    private readonly LearningEnv _env = new();

    public void Dispose() => _env.Dispose();

    private async Task SeedLessonsAsync(int count)
    {
        var lessons = new List<LessonRecord>();
        for (var i = 0; i < count; i++)
        {
            lessons.Add(new LessonRecord
            {
                MissionId = $"m-{i}",
                Phase = "Executing",
                Gap = $"缺口 {i}：外部调用未设置超时导致挂起",
                Suggestion = $"建议 {i}：所有网络调用加超时",
                Severity = "warning",
            });
        }

        await _env.Missions.AddLessonsAsync(lessons);
    }

    [Fact]
    public async Task SyncLessons_ImportsFromRealMissionStore_Classified()
    {
        await _env.Missions.AddLessonsAsync(new[]
        {
            new LessonRecord { MissionId = "m-1", Phase = "Executing", Gap = "provider 连接超时导致执行失败", Suggestion = "检查 provider 配置与网络", Severity = "critical" },
            new LessonRecord { MissionId = "m-1", Phase = "Verifying", Gap = "校验未通过", Suggestion = "对照失败检查项补做", Severity = "warning" },
            new LessonRecord { MissionId = "m-2", Phase = "Executing", Gap = "任务中途取消，无产出", Suggestion = string.Empty, Severity = "info" },
        });

        var sync = await _env.Bridge.SyncLessonsAsync();

        Assert.Equal(3, sync.LessonsScanned);
        Assert.Equal(3, sync.NewlySynced);
        Assert.Equal(1, sync.FactCount);      // "配置" 命中 → 事实
        Assert.Equal(1, sync.MethodCount);    // 有建议无配置词 → 方法
        Assert.Equal(1, sync.TrajectoryCount); // 无建议 → 轨迹
        Assert.Equal(3, await _env.Experiences.CountAsync());

        var all = await _env.Experiences.GetByStatusAsync(ExperienceStatus.Pending, 10);
        Assert.All(all, e => Assert.StartsWith("lesson:", e.SourceKey));
    }

    [Fact]
    public async Task SyncLessons_SecondCall_IsIdempotent()
    {
        await SeedLessonsAsync(2);

        var first = await _env.Bridge.SyncLessonsAsync();
        var second = await _env.Bridge.SyncLessonsAsync();

        Assert.Equal(2, first.NewlySynced);
        Assert.Equal(0, second.NewlySynced);
        Assert.Equal(2, second.AlreadySynced);
        Assert.Equal(2, await _env.Experiences.CountAsync());
    }

    [Fact]
    public async Task SystemPromptBuilder_IncludesLessons_ViaRealInjector()
    {
        await SeedLessonsAsync(1);

        var composition = await _env.PromptBuilder.BuildAsync(maxLessons: 5, maxExperiences: 8);

        Assert.Equal(1, composition.InjectedLessonCount);
        Assert.Contains("外部调用未设置超时导致挂起", composition.SystemPrompt);
        Assert.Contains("所有网络调用加超时", composition.SystemPrompt);
    }

    [Fact]
    public async Task SystemPromptBuilder_WithoutActivation_ExcludesPendingExperiences()
    {
        await _env.Experiences.AddAsync(ExperienceKind.Method, "会话内刚写入的经验", "不应出现在同会话 prompt", sourceKey: "k:same-session");

        var composition = await _env.PromptBuilder.BuildAsync(activatePending: false);

        Assert.DoesNotContain("会话内刚写入的经验", composition.SystemPrompt);
        Assert.Equal(0, composition.InjectedExperienceCount);
        Assert.Equal(0, composition.ActivatedPendingCount);
    }

    [Fact]
    public async Task SystemPromptBuilder_ActivatesInjectsAndMarksApplied()
    {
        var added = await _env.Experiences.AddAsync(
            ExperienceKind.Method, "先跑构建再跑测试", "提交前必须本地构建通过，测试全绿。", sourceKey: "k:next-session");

        var composition = await _env.PromptBuilder.BuildAsync();

        Assert.Equal(1, composition.ActivatedPendingCount);
        Assert.Equal(1, composition.InjectedExperienceCount);
        Assert.Equal(1, composition.MarkedAppliedCount);
        Assert.Contains("先跑构建再跑测试", composition.SystemPrompt);
        Assert.Contains("已生效的长期经验", composition.SystemPrompt);

        var entry = await _env.Experiences.GetByIdAsync(added.Entry.Id);
        Assert.Equal(ExperienceStatus.Applied, entry!.Status);
        Assert.NotNull(entry.ActivatedAtUtc);
        Assert.NotNull(entry.AppliedAtUtc);
    }

    [Fact]
    public async Task CombinedPrompt_ContainsBothChannels_LessonsAndExperiences()
    {
        await SeedLessonsAsync(1);
        await _env.Experiences.AddAsync(ExperienceKind.Fact, "构建机无外网直连", "需走代理才能访问外部 API。", sourceKey: "k:fact");

        var composition = await _env.PromptBuilder.BuildAsync();

        Assert.Equal(1, composition.InjectedLessonCount);
        Assert.Equal(1, composition.InjectedExperienceCount);
        Assert.Contains("外部调用未设置超时导致挂起", composition.SystemPrompt); // lessons 通道
        Assert.Contains("构建机无外网直连", composition.SystemPrompt);           // 经验通道
        Assert.Contains("事实（环境/配置类稳定知识）", composition.SystemPrompt);
    }

    // ============ 验收 E2E ① ============

    private MissionController BuildController(IMissionExecutor executor)
    {
        var llm = new AutonomyLlmClient(registry: null); // deterministic paths, honest [DEGRADED]
        return new MissionController(
            analyzer: new TaskAnalyzer(llm),
            strategySelector: new StrategySelector(),
            clarificationGate: new ClarificationGate(llm),
            steelman: new SteelmanProtocol(llm),
            store: _env.Missions,
            executor: executor,
            retrospective: new RetrospectiveEngine(),
            experience: _env.Injector,
            llm: llm,
            paths: _env.AutonomyPaths);
    }

    [Fact]
    public async Task AcceptanceE2E_MissionComplete_LessonsPersisted_VisibleInNextSessionPrompt()
    {
        // ---- 会话 1：任务执行（失败路径同样必达复盘+lessons）----
        var executor = new FakeMissionExecutor(_ =>
            new MissionExecutionOutcome(false, false, string.Empty, "provider 连接超时", null, 0, 0));
        var controller = BuildController(executor);
        var record = await controller.RunAsync("部署服务到测试环境并验证接口联调");
        Assert.Equal(MissionOutcome.Failed, record.Outcome);

        var lessons = await _env.Missions.GetRecentLessonsAsync(20);
        Assert.NotEmpty(lessons); // PHASE 5 既有链路真实产出 lessons

        // ---- 会话 1 结束：lessons 同步进三分经验存储 ----
        var sync = await _env.Bridge.SyncLessonsAsync();
        Assert.True(sync.NewlySynced >= 2, $"期望至少 2 条新经验，实际 {sync.NewlySynced}");

        // 落盘真实性 1：独立 SQLite 库文件存在且可被全新上下文读取。
        Assert.True(File.Exists(_env.LearningPaths.DatabaseFile));
        using (var fresh = _env.NewLearningDb())
        {
            var rows = await fresh.Experiences.AsNoTracking().ToListAsync();
            Assert.Equal(sync.NewlySynced, rows.Count);
            Assert.Contains(rows, r => r.Kind == ExperienceKind.Fact); // "provider 配置" 命中事实类
        }

        // 落盘真实性 2：人类可读 md 真实可读且含经验内容。
        Assert.True(File.Exists(_env.LearningPaths.ExperienceLogFile));
        var md = await File.ReadAllTextAsync(_env.LearningPaths.ExperienceLogFile);
        Assert.Contains("provider 连接超时", md);

        // pending 语义：写入后尚未激活，生效集合为空（同会话不可见）。
        Assert.Empty(await _env.Experiences.GetEffectiveExperiencesAsync());

        // ---- 会话 2：构建 prompt 时激活并注入（下次会话可见）----
        var composition = await _env.PromptBuilder.BuildAsync(maxLessons: 5, maxExperiences: 8);

        Assert.True(composition.ActivatedPendingCount >= 2);
        Assert.True(composition.InjectedExperienceCount >= 2);
        Assert.Contains("provider 连接超时", composition.SystemPrompt);   // lessons 通道 + 事实经验
        Assert.Contains("已生效的长期经验", composition.SystemPrompt);

        // 状态机闭环：全部被注入的经验已标记 Applied（真实消费留痕）。
        var effective = await _env.Experiences.GetEffectiveExperiencesAsync(20);
        Assert.All(effective, e => Assert.Equal(ExperienceStatus.Applied, e.Status));
    }
}
