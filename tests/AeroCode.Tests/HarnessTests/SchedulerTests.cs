// Copyright (c) AeroCode
// SchedulerService 真实行为测试（批次 B G4，builder-γ）：零 mock——
// 触发=真实子进程写真实文件；持久化=真实 jobs.json 落盘重读；ESTOP=真实哨兵文件；时区=注入固定 nowUtc。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AeroCode.Harness.EventBus;
using AeroCode.Harness.Scheduler;
using Xunit;

namespace AeroCode.Tests.HarnessTests;

public sealed class SchedulerTests : IDisposable
{
    private readonly string _dir;
    private readonly string _jobsPath;

    public SchedulerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "aerocode-scheduler-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _jobsPath = Path.Combine(_dir, "jobs.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort 清理 */ }
    }

    private JobDef Job(string id, string? cron = null, DateTime? atUtc = null, string? command = null, string? missionPrompt = null, bool enabled = true)
        => new()
        {
            Id = id,
            Cron = cron,
            AtUtc = atUtc,
            Command = command ?? $"echo fired-{id}> \"{Path.Combine(_dir, $"{id}.txt")}\"",
            MissionPrompt = missionPrompt,
            Enabled = enabled,
            TimeoutSec = 30,
        };

    private string Marker(string id) => Path.Combine(_dir, $"{id}.txt");

    private SchedulerService NewScheduler(EventBus? bus = null)
        => new(_jobsPath, Path.Combine(_dir, "estop.sentinel"), bus, log: _ => { });

    // ---- 注册与持久化 ----

    [Fact]
    public void AddOrUpdate_PersistsToJobsJson_AndSurvivesReload()
    {
        using (var s = NewScheduler())
        {
            s.AddOrUpdate(Job("nightly", cron: "30 9 * * *", missionPrompt: "每晚整理笔记"));
            s.AddOrUpdate(Job("once", atUtc: DateTime.UtcNow.AddHours(1)));
        }

        // 真实落盘：文件存在且新实例（模拟重启）能读回
        Assert.True(File.Exists(_jobsPath));
        using var reloaded = NewScheduler();
        reloaded.Load();
        var jobs = reloaded.Jobs;
        Assert.Equal(2, jobs.Count);
        Assert.Equal("nightly", jobs[0].Id);
        Assert.Equal("30 9 * * *", jobs[0].Cron);
        Assert.Equal("每晚整理笔记", jobs[0].MissionPrompt);
        Assert.NotNull(jobs[1].AtUtc);
        Assert.Equal(DateTimeKind.Utc, jobs[1].AtUtc!.Value.Kind); // JobDef 显式存 UTC
    }

    [Fact]
    public void AddOrUpdate_InvalidJobs_ThrowArgumentException()
    {
        using var s = NewScheduler();
        // cron 与 atUtc 都缺 → 拒绝
        Assert.Throws<ArgumentException>(() => s.AddOrUpdate(new JobDef { Id = "x", Command = "echo hi" }));
        // 同时都给 → 拒绝
        Assert.Throws<ArgumentException>(() => s.AddOrUpdate(new JobDef { Id = "x", Command = "echo hi", Cron = "* * * * *", AtUtc = DateTime.UtcNow }));
        // 缺 command → 拒绝
        Assert.Throws<ArgumentException>(() => s.AddOrUpdate(new JobDef { Id = "x", Cron = "* * * * *" }));
        // 非法 cron → 拒绝
        Assert.Throws<ArgumentException>(() => s.AddOrUpdate(new JobDef { Id = "x", Command = "echo hi", Cron = "99 * * * *" }));
        Assert.Empty(s.Jobs); // 坏任务不进表
    }

    // ---- cron 触发（本地时间求值，注入固定时刻） ----

    [Fact]
    public void RunDueJobsOnce_CronDue_RunsRealCommand_AndFiresEvent()
    {
        using var s = NewScheduler();
        s.AddOrUpdate(Job("everymin", cron: "* * * * *")); // 每分钟：对任何注入时刻都到期，时区无关
        JobDef? firedJob = null;
        JobRunResult? result = null;
        s.JobFired += (j, r) => { firedJob = j; result = r; };

        var now = DateTimeOffset.Now;
        var fired = s.RunDueJobsOnce(now);

        Assert.Equal(1, fired);
        Assert.Equal("everymin", firedJob!.Id);
        Assert.True(result!.Success, $"stderr: {result.StdErr}");
        Assert.True(File.Exists(Marker("everymin")), "cron job must run a real process writing a real file");
        Assert.Contains("fired-everymin", File.ReadAllText(Marker("everymin")));
    }

    [Fact]
    public void RunDueJobsOnce_CronSameMinute_FiresOnlyOnce()
    {
        using var s = NewScheduler();
        s.AddOrUpdate(Job("m", cron: "* * * * *"));
        // 固定在本分钟第 5 秒，保证 +30s 仍处同一分钟（防分钟翻转抖动）
        var minuteStart = new DateTime(DateTime.Now.Year, DateTime.Now.Month, DateTime.Now.Day, DateTime.Now.Hour, DateTime.Now.Minute, 0, DateTimeKind.Local);
        var now = new DateTimeOffset(minuteStart.AddSeconds(5));

        Assert.Equal(1, s.RunDueJobsOnce(now));
        Assert.Equal(0, s.RunDueJobsOnce(now)); // 同一分钟去重
        Assert.Equal(0, s.RunDueJobsOnce(now.AddSeconds(30)));
        Assert.Equal(1, s.RunDueJobsOnce(now.AddMinutes(1))); // 下一分钟重新触发
    }

    [Fact]
    public void RunDueJobsOnce_CronNotDueYet_DoesNotFire()
    {
        using var s = NewScheduler();
        // 1月1日 00:00：对年中任何时刻都不到期（本地时区无关的确定性构造）
        s.AddOrUpdate(new JobDef { Id = "newyear", Cron = "0 0 1 1 *", Command = $"echo ny> \"{Marker("newyear")}\"", TimeoutSec = 30 });

        var midYear = new DateTimeOffset(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(0, s.RunDueJobsOnce(midYear));
        Assert.False(File.Exists(Marker("newyear")));
    }

    // ---- 一次性任务（AtUtc 显式 UTC） ----

    [Fact]
    public void RunDueJobsOnce_OneShotPastDue_Fires_ThenAutoDisables_AndStaysDisabledAcrossReload()
    {
        using (var s = NewScheduler())
        {
            s.AddOrUpdate(Job("oneshot", atUtc: DateTime.UtcNow.AddMinutes(-1)));
            Assert.Equal(1, s.RunDueJobsOnce(DateTimeOffset.UtcNow));
            Assert.True(File.Exists(Marker("oneshot")));
            Assert.False(s.Jobs.Single(j => j.Id == "oneshot").Enabled, "one-shot must auto-disable after firing");
        }

        // 落盘验证：重启（新实例）后不复燃
        using var reloaded = NewScheduler();
        reloaded.Load();
        Assert.False(reloaded.Jobs.Single(j => j.Id == "oneshot").Enabled);
        Assert.Equal(0, reloaded.RunDueJobsOnce(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void RunDueJobsOnce_OneShotInFuture_DoesNotFire()
    {
        using var s = NewScheduler();
        s.AddOrUpdate(Job("future", atUtc: DateTime.UtcNow.AddMinutes(5)));

        Assert.Equal(0, s.RunDueJobsOnce(DateTimeOffset.UtcNow));
        Assert.False(File.Exists(Marker("future")));
        Assert.True(s.Jobs.Single(j => j.Id == "future").Enabled, "future job must stay enabled");
    }

    // ---- ESTOP 哨兵联动 ----

    [Fact]
    public void EstopSentinel_BlocksDueJobs_PublishesEtopTrippedEvent_ThenRecoversAfterRemoval()
    {
        var bus = new EventBus();
        var etopEvents = new List<EtopTrippedEvent>();
        bus.Subscribe<EtopTrippedEvent>(e => etopEvents.Add(e));

        var sentinel = Path.Combine(_dir, "estop.sentinel");
        using var s = NewScheduler(bus);
        s.AddOrUpdate(Job("blocked", cron: "* * * * *"));
        s.AddOrUpdate(Job("blocked-once", atUtc: DateTime.UtcNow.AddMinutes(-1)));

        // 哨兵存在：到期任务被拦截——不执行、不消耗；转换轮发布 EtopTrippedEvent（一次）
        File.WriteAllText(sentinel, "EMERGENCY STOP");
        Assert.Equal(0, s.RunDueJobsOnce(DateTimeOffset.Now));
        Assert.Equal(0, s.RunDueJobsOnce(DateTimeOffset.Now.AddSeconds(40))); // 第二轮仍拦（急停期间不重复发事件）
        Assert.False(File.Exists(Marker("blocked")));
        Assert.False(File.Exists(Marker("blocked-once")));
        Assert.True(s.Jobs.Single(j => j.Id == "blocked-once").Enabled, "ESTOP must not consume one-shot jobs");
        Assert.Single(etopEvents); // 转换语义：连续拦截只发一次，不刷屏
        Assert.Contains("sentinel", etopEvents[0].Reason);

        // 哨兵移除：立即恢复（一次性任务未被消耗仍可触发），并复位转换标志
        File.Delete(sentinel);
        Assert.Equal(2, s.RunDueJobsOnce(DateTimeOffset.Now)); // cron + 未被消耗的一次性
        Assert.True(File.Exists(Marker("blocked")));
        Assert.True(File.Exists(Marker("blocked-once")));

        // 再次急停：新一次转换再次发布，且同样不消耗任务
        File.WriteAllText(sentinel, "EMERGENCY STOP 2");
        s.AddOrUpdate(Job("blocked-again", atUtc: DateTime.UtcNow.AddMinutes(-1)));
        Assert.Equal(0, s.RunDueJobsOnce(DateTimeOffset.Now));
        Assert.Equal(2, etopEvents.Count);
        Assert.True(s.Jobs.Single(j => j.Id == "blocked-again").Enabled, "second ESTOP must not consume either");
    }

    // ---- 禁用任务 ----

    [Fact]
    public void DisabledJob_NeverFires()
    {
        using var s = NewScheduler();
        s.AddOrUpdate(Job("off", cron: "* * * * *", enabled: false));
        s.AddOrUpdate(Job("off-once", atUtc: DateTime.UtcNow.AddMinutes(-1), enabled: false));

        Assert.Equal(0, s.RunDueJobsOnce(DateTimeOffset.Now));
        Assert.False(File.Exists(Marker("off")));
        Assert.False(File.Exists(Marker("off-once")));
    }

    // ---- Remove ----

    [Fact]
    public void Remove_DeletesJobAndPersists()
    {
        using var s = NewScheduler();
        s.AddOrUpdate(Job("todelete", cron: "* * * * *"));
        Assert.True(s.Remove("todelete"));
        Assert.False(s.Remove("todelete")); // 再删如实返回 false

        using var reloaded = NewScheduler();
        reloaded.Load();
        Assert.Empty(reloaded.Jobs);
    }

    // ---- 损坏 jobs.json：fail-safe 空载 ----

    [Fact]
    public void Load_CorruptJobsJson_FailSafeEmpty_WithLastLoadError()
    {
        File.WriteAllText(_jobsPath, "{ jobs: [ broken ]]]");
        using var s = NewScheduler();
        s.Load(); // 不抛：fail-safe 空载 + 诚实记录错误
        Assert.Empty(s.Jobs);
        Assert.NotNull(s.LastLoadError);
    }

    [Fact]
    public void Load_NoFile_StartsEmpty_WithoutError()
    {
        using var s = NewScheduler();
        s.Load();
        Assert.Empty(s.Jobs);
        Assert.Null(s.LastLoadError);
    }

    // ---- CronSchedule 直接单测（本地时间语义） ----

    [Fact]
    public void CronSchedule_ParsesAndMatches()
    {
        Assert.True(CronSchedule.Parse("* * * * *").Matches(new DateTime(2026, 3, 15, 8, 47, 13)));
        Assert.True(CronSchedule.Parse("*/5 * * * *").Matches(new DateTime(2026, 3, 15, 8, 45, 0)));
        Assert.False(CronSchedule.Parse("*/5 * * * *").Matches(new DateTime(2026, 3, 15, 8, 47, 0)));
        Assert.True(CronSchedule.Parse("30 9 * * *").Matches(new DateTime(2026, 3, 15, 9, 30, 0)));
        Assert.False(CronSchedule.Parse("30 9 * * *").Matches(new DateTime(2026, 3, 15, 9, 31, 0)));
        // 周一（DayOfWeek.Monday = 1）
        Assert.True(CronSchedule.Parse("0 8 * * 1").Matches(new DateTime(2026, 3, 16, 8, 0, 0)));
        Assert.False(CronSchedule.Parse("0 8 * * 1").Matches(new DateTime(2026, 3, 17, 8, 0, 0)));
        // 列表与区间
        Assert.True(CronSchedule.Parse("0,15,30,45 * * * *").Matches(new DateTime(2026, 3, 15, 10, 45, 0)));
        Assert.True(CronSchedule.Parse("0 9-17 * * *").Matches(new DateTime(2026, 3, 15, 14, 0, 0)));
        Assert.False(CronSchedule.Parse("0 9-17 * * *").Matches(new DateTime(2026, 3, 15, 18, 0, 0)));
        // 7 也是周日
        Assert.True(CronSchedule.Parse("0 0 * * 7").Matches(new DateTime(2026, 3, 15, 0, 0, 0))); // 2026-03-15 是周日
    }

    [Fact]
    public void CronSchedule_InvalidExpressions_ThrowFormatException()
    {
        Assert.Throws<FormatException>(() => CronSchedule.Parse("* * * *"));        // 4 字段
        Assert.Throws<FormatException>(() => CronSchedule.Parse("* * * * * *"));    // 6 字段
        Assert.Throws<FormatException>(() => CronSchedule.Parse("99 * * * *"));     // 越界
        Assert.Throws<FormatException>(() => CronSchedule.Parse("* 25 * * *"));     // 越界
        Assert.Throws<FormatException>(() => CronSchedule.Parse("* * 0 * *"));      // 日从 1 起
        Assert.Throws<FormatException>(() => CronSchedule.Parse("*/0 * * * *"));    // 步进非正
        Assert.Throws<FormatException>(() => CronSchedule.Parse(""));               // 空
    }

    // ---- Start/Stop/Dispose：Timer 生命周期 ----

    [Fact]
    public async Task Start_TicksAndFiresDueJobs_RealTimer()
    {
        using var s = NewScheduler();
        s.PollSeconds = 1; // 最小轮询：1s 内应触发 * * * * *
        s.AddOrUpdate(Job("timer", cron: "* * * * *"));
        s.Start();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!File.Exists(Marker("timer")) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        s.Stop();
        Assert.True(File.Exists(Marker("timer")), "started scheduler must fire due job via real timer");
    }
}
