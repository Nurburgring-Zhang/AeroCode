// ExperienceStore (three-way storage + pending/effective semantics) + ExperienceClassifier tests.
// Real SQLite + real file IO; fresh contexts prove persistence beyond a single instance.
using AeroAgent.Autonomy.Data;
using AeroAgent.Autonomy.Learning;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AeroCode.Tests.Autonomy;

public sealed class LearningExperienceStoreTests : IDisposable
{
    private readonly LearningEnv _env = new();

    public void Dispose() => _env.Dispose();

    [Fact]
    public async Task AddAsync_PersistsAsPending_ToRealSqlite_ReadableByFreshContext()
    {
        var result = await _env.Experiences.AddAsync(
            ExperienceKind.Fact, "SQLite 库文件路径约定", "自主内核库落在应用数据根，学习库独立成文件。",
            sourceKey: "manual:fact-1", tags: new[] { "storage" });

        Assert.True(result.CreatedNew);
        Assert.Equal(ExperienceStatus.Pending, result.Entry.Status);

        // A brand-new context over the same db file must see the row (real persistence).
        using var fresh = _env.NewLearningDb();
        var row = await fresh.Experiences.AsNoTracking().SingleAsync(x => x.SourceKey == "manual:fact-1");
        Assert.Equal("SQLite 库文件路径约定", row.Title);
        Assert.Equal(ExperienceKind.Fact, row.Kind);
        Assert.Equal(ExperienceStatus.Pending, row.Status);
    }

    [Fact]
    public async Task AddAsync_AppendsHumanReadableEntry_ToExperienceLogMd()
    {
        await _env.Experiences.AddAsync(
            ExperienceKind.Method, "网络调用必须带超时", "所有外部 HTTP 调用设置 30s 超时并重试一次。",
            sourceKey: "manual:method-1", sourceMissionId: "m-1", sourcePhase: "Executing",
            tags: new[] { "network", "timeout" });

        Assert.True(File.Exists(_env.LearningPaths.ExperienceLogFile));
        var md = await File.ReadAllTextAsync(_env.LearningPaths.ExperienceLogFile);
        Assert.Contains("方法：网络调用必须带超时", md);
        Assert.Contains("来源任务: m-1 / 阶段 Executing", md);
        Assert.Contains("标签: network, timeout", md);
        Assert.Contains("所有外部 HTTP 调用设置 30s 超时并重试一次。", md);
        Assert.Contains("写入与生效分离", md);
    }

    [Fact]
    public async Task AddAsync_SameSourceKey_IsIdempotent_NoDuplicateRowOrMdEntry()
    {
        var first = await _env.Experiences.AddAsync(
            ExperienceKind.Fact, "标题A", "内容A", sourceKey: "lesson:dup-1");
        var second = await _env.Experiences.AddAsync(
            ExperienceKind.Fact, "标题B", "内容B", sourceKey: "lesson:dup-1");

        Assert.True(first.CreatedNew);
        Assert.False(second.CreatedNew);
        Assert.Equal(first.Entry.Id, second.Entry.Id);
        Assert.Equal(1, await _env.Experiences.CountAsync());

        var md = await File.ReadAllTextAsync(_env.LearningPaths.ExperienceLogFile);
        Assert.Contains("标题A", md);
        Assert.DoesNotContain("标题B", md); // 幂等命中不重复写 md。
    }

    [Fact]
    public async Task GetEffectiveExperiences_ExcludesPending_Strictly()
    {
        await _env.Experiences.AddAsync(ExperienceKind.Method, "新写入的经验", "内容", sourceKey: "k:1");

        var effective = await _env.Experiences.GetEffectiveExperiencesAsync();

        Assert.Empty(effective); // Pending 绝不混入生效集合。
    }

    [Fact]
    public async Task ActivatePending_PromotesToEffective_WithTimestamp()
    {
        var added = await _env.Experiences.AddAsync(ExperienceKind.Fact, "事实X", "内容X", sourceKey: "k:2");

        var activated = await _env.Experiences.ActivatePendingAsync();

        Assert.Equal(1, activated);
        var entry = await _env.Experiences.GetByIdAsync(added.Entry.Id);
        Assert.NotNull(entry);
        Assert.Equal(ExperienceStatus.Effective, entry!.Status);
        Assert.NotNull(entry.ActivatedAtUtc);
        Assert.Null(entry.AppliedAtUtc);
    }

    [Fact]
    public async Task MarkApplied_OnlyAffectsEffective_SetsTimestamp_AndStaysReadable()
    {
        var effectiveEntry = (await _env.Experiences.AddAsync(ExperienceKind.Method, "已激活", "c1", sourceKey: "k:3")).Entry;
        var pendingEntry = (await _env.Experiences.AddAsync(ExperienceKind.Method, "仍待激活", "c2", sourceKey: "k:4")).Entry;
        await _env.Experiences.ActivatePendingAsync();
        // 再把一条重新留作 Pending 之外：这里 k:4 也被激活了；补写一条新的保持 Pending。
        var freshPending = (await _env.Experiences.AddAsync(ExperienceKind.Method, "会话内新经验", "c3", sourceKey: "k:5")).Entry;

        var marked = await _env.Experiences.MarkAppliedAsync(new[] { effectiveEntry.Id, pendingEntry.Id, freshPending.Id });

        Assert.Equal(2, marked); // 只有 Effective 的两条可被标记；Pending 语义不被绕过。
        var applied = await _env.Experiences.GetByIdAsync(effectiveEntry.Id);
        Assert.Equal(ExperienceStatus.Applied, applied!.Status);
        Assert.NotNull(applied.AppliedAtUtc);
        var stillPending = await _env.Experiences.GetByIdAsync(freshPending.Id);
        Assert.Equal(ExperienceStatus.Pending, stillPending!.Status);

        // Applied 仍然属于"生效"集合（持续有效，可继续注入）。
        var effective = await _env.Experiences.GetEffectiveExperiencesAsync();
        Assert.Contains(effective, e => e.Id == effectiveEntry.Id);
    }

    [Fact]
    public async Task GetByKind_FiltersCorrectly()
    {
        await _env.Experiences.AddAsync(ExperienceKind.Fact, "f1", "cf", sourceKey: "kf:1");
        await _env.Experiences.AddAsync(ExperienceKind.Trajectory, "t1", "ct", sourceKey: "kt:1");
        await _env.Experiences.AddAsync(ExperienceKind.Method, "m1", "cm", sourceKey: "km:1");
        await _env.Experiences.AddAsync(ExperienceKind.Method, "m2", "cm2", sourceKey: "km:2");

        Assert.Single(await _env.Experiences.GetByKindAsync(ExperienceKind.Fact));
        Assert.Single(await _env.Experiences.GetByKindAsync(ExperienceKind.Trajectory));
        Assert.Equal(2, (await _env.Experiences.GetByKindAsync(ExperienceKind.Method)).Count);
    }

    [Fact]
    public async Task Count_TotalAndByKind_AreHonest()
    {
        await _env.Experiences.AddAsync(ExperienceKind.Fact, "f", "cf", sourceKey: "c:1");
        await _env.Experiences.AddAsync(ExperienceKind.Method, "m", "cm", sourceKey: "c:2");

        Assert.Equal(2, await _env.Experiences.CountAsync());
        Assert.Equal(1, await _env.Experiences.CountAsync(ExperienceKind.Fact));
        Assert.Equal(0, await _env.Experiences.CountAsync(ExperienceKind.Trajectory));
    }

    [Fact]
    public async Task EmptyStore_ActivateZero_EffectiveEmpty()
    {
        Assert.Equal(0, await _env.Experiences.ActivatePendingAsync());
        Assert.Empty(await _env.Experiences.GetEffectiveExperiencesAsync());
        Assert.Equal(0, await _env.Experiences.CountAsync());
    }

    [Fact]
    public async Task AddAsync_EmptyTitleOrContent_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _env.Experiences.AddAsync(ExperienceKind.Fact, "  ", "content"));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _env.Experiences.AddAsync(ExperienceKind.Fact, "title", ""));
    }

    // ============ 三分分类器（确定性规则） ============

    private static LessonRecord Lesson(string gap, string suggestion, string phase = "Executing", string severity = "warning") => new()
    {
        MissionId = "m-x",
        Phase = phase,
        Gap = gap,
        Suggestion = suggestion,
        Severity = severity,
    };

    [Fact]
    public void Classifier_EnvironmentOrConfigKeywords_ClassifiedAsFact()
    {
        Assert.Equal(ExperienceKind.Fact, ExperienceClassifier.Classify(
            Lesson("构建机 IP 被反爬拦截", "配置 BING 密钥后恢复")));
        Assert.Equal(ExperienceKind.Fact, ExperienceClassifier.Classify(
            Lesson("dependency missing at runtime", "install the package")));
    }

    [Fact]
    public void Classifier_ActionableSuggestion_ClassifiedAsMethod()
    {
        Assert.Equal(ExperienceKind.Method, ExperienceClassifier.Classify(
            Lesson("校验未通过：产物未满足验收标准", "对照失败检查项补做后重跑")));
    }

    [Fact]
    public void Classifier_NoSuggestion_ClassifiedAsTrajectory()
    {
        Assert.Equal(ExperienceKind.Trajectory, ExperienceClassifier.Classify(
            Lesson("执行中途被取消，产出为空", suggestion: string.Empty)));
    }

    [Fact]
    public void Classifier_BuildTitleAndContent_UseLessonOriginalText()
    {
        var lesson = Lesson("执行未成功：provider 连接超时", "检查编排策略适配", phase: "Executing", severity: "critical");
        var title = ExperienceClassifier.BuildTitle(lesson);
        var content = ExperienceClassifier.BuildContent(lesson);

        Assert.Equal("[Executing] 执行未成功：provider 连接超时", title);
        Assert.Contains("缺口（critical）: 执行未成功：provider 连接超时", content);
        Assert.Contains("做法: 检查编排策略适配", content);
    }
}
