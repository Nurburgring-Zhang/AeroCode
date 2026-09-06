// Copyright (c) AeroCode
// L1 JobDef.BudgetTokens 消费者钉子：无预算路径零变化（基线）、Continue=留痕继续（默认）、
// Stop=停用落盘且不再触发、预算经 AEROCODE_JOB_BUDGET_TOKENS 注入子进程环境、
// 非法档位 fail-closed 拒绝。全部真实子进程/真实 jobs.json，零 mock。
using System;
using System.IO;
using System.Linq;
using System.Globalization;
using AeroCode.Harness.Scheduler;
using Xunit;

namespace AeroCode.Tests.HarnessTests;

public sealed class SchedulerBudgetTests : IDisposable
{
    private readonly string _dir;
    private readonly string _jobsPath;

    public SchedulerBudgetTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "aerocode-scheduler-budget-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _jobsPath = Path.Combine(_dir, "jobs.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort 清理 */ }
    }

    private JobDef Job(string id, string? command = null, long? budgetTokens = null,
        JobBudgetExceededAction action = JobBudgetExceededAction.Continue)
        => new()
        {
            Id = id,
            Cron = "* * * * *", // 对任何注入时刻都到期
            Command = command ?? $"echo fired-{id}> \"{Marker(id)}\"",
            BudgetTokens = budgetTokens,
            BudgetExceededAction = action,
            TimeoutSec = 30,
        };

    private string Marker(string id) => Path.Combine(_dir, $"{id}.txt");

    private SchedulerService NewScheduler() => new(_jobsPath, log: _ => { });

    // ---------- 基线：无预算路径零变化 ----------

    [Fact]
    public void NoBudget_ReportTokenUsage_IsNoOp()
    {
        using var s = NewScheduler();
        s.AddOrUpdate(Job("plain"));
        JobDef? budgetEventJob = null;
        s.BudgetExceeded += (j, _) => budgetEventJob = j;

        s.ReportTokenUsage("plain", 1_000_000); // 无预算：只记账不设闸

        Assert.Null(budgetEventJob); // 不发跨限事件
        Assert.True(s.Jobs.Single(j => j.Id == "plain").Enabled);
        Assert.Equal(1, s.RunDueJobsOnce(DateTimeOffset.Now)); // 照常触发（基线路径）
    }

    [Fact]
    public void UnknownJobId_ReportTokenUsage_Ignored()
    {
        using var s = NewScheduler();
        s.ReportTokenUsage("ghost", 100); // 如实忽略，不伪造记账、不抛
        Assert.Empty(s.Jobs);
    }

    [Fact]
    public void ReportTokenUsage_InvalidArguments_Throw()
    {
        using var s = NewScheduler();
        Assert.Throws<ArgumentException>(() => s.ReportTokenUsage("", 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => s.ReportTokenUsage("x", -1));
    }

    // ---------- Continue（默认）：留痕继续 ----------

    [Fact]
    public void BudgetExceeded_ContinueAction_EventOnce_JobKeepsFiring()
    {
        using var s = NewScheduler();
        s.AddOrUpdate(Job("cont", budgetTokens: 100));
        var events = new System.Collections.Generic.List<(string Id, long Total)>();
        s.BudgetExceeded += (j, total) => events.Add((j.Id, total));

        s.ReportTokenUsage("cont", 60);
        Assert.Empty(events); // 未跨限：无事件

        s.ReportTokenUsage("cont", 40); // 累计 100 ≥ 上限 100 → 跨限
        var single = Assert.Single(events);
        Assert.Equal("cont", single.Id);
        Assert.Equal(100, single.Total);

        s.ReportTokenUsage("cont", 500); // 转换沿恰一次：不重复刷事件
        Assert.Single(events);

        Assert.True(s.Jobs.Single(j => j.Id == "cont").Enabled); // Continue 不停用
        Assert.Equal(1, s.RunDueJobsOnce(DateTimeOffset.Now));   // 照常触发
        Assert.True(File.Exists(Marker("cont")));
    }

    // ---------- Stop：停用落盘、不再触发 ----------

    [Fact]
    public void BudgetExceeded_StopAction_DisablesJob_PersistsAndStopsFiring()
    {
        using var s = NewScheduler();
        s.AddOrUpdate(Job("stop", budgetTokens: 50, action: JobBudgetExceededAction.Stop));
        var events = new System.Collections.Generic.List<(string Id, long Total)>();
        s.BudgetExceeded += (j, total) => events.Add((j.Id, total));

        s.ReportTokenUsage("stop", 60);

        var single = Assert.Single(events);
        Assert.Equal(60, single.Total);
        Assert.False(s.Jobs.Single(j => j.Id == "stop").Enabled); // 已停用

        // 真实落盘：新实例（模拟重启）读回仍停用。
        using var reloaded = NewScheduler();
        reloaded.Load();
        Assert.False(reloaded.Jobs.Single(j => j.Id == "stop").Enabled);

        Assert.Equal(0, s.RunDueJobsOnce(DateTimeOffset.Now)); // 后续轮次不再触发
        Assert.False(File.Exists(Marker("stop")));
    }

    // ---------- 预算投递到执行环境 ----------

    [Fact]
    public void Execute_InjectsBudgetTokensEnvVar()
    {
        using var s = NewScheduler();
        var outPath = Path.Combine(_dir, "env.txt");
        // 注意 cmd 陷阱：值尾部紧贴 > 会被当文件句柄（如 "777>"），故 "> " 留空格，断言用 Trim。
        s.AddOrUpdate(Job("env", command: $"echo %AEROCODE_JOB_BUDGET_TOKENS% > \"{outPath}\"", budgetTokens: 777));

        Assert.Equal(1, s.RunDueJobsOnce(DateTimeOffset.Now));

        Assert.True(File.Exists(outPath), "预算环境变量注入须以真实子进程验证");
        Assert.Equal("777", File.ReadAllText(outPath).Trim());
    }

    // ---------- fail-closed 校验 ----------

    [Fact]
    public void JobDef_InvalidBudgetAction_Throws()
    {
        using var s = NewScheduler();
        Assert.Throws<ArgumentException>(() => s.AddOrUpdate(new JobDef
        {
            Id = "bad",
            Cron = "* * * * *",
            Command = "echo hi",
            BudgetTokens = 10,
            BudgetExceededAction = (JobBudgetExceededAction)99, // 越界数值 fail-closed 拒绝
        }));
        Assert.Throws<ArgumentException>(() => s.AddOrUpdate(Job("neg", budgetTokens: -5)));
    }

    [Fact]
    public void JobDef_BudgetFields_RoundTripThroughJobsJson()
    {
        using (var s = NewScheduler())
        {
            s.AddOrUpdate(Job("rt", budgetTokens: 1234, action: JobBudgetExceededAction.Stop));
        }

        var raw = File.ReadAllText(_jobsPath);
        Assert.Contains("budgetTokens", raw, StringComparison.Ordinal);
        Assert.Contains("budgetExceededAction", raw, StringComparison.Ordinal);

        using var reloaded = NewScheduler();
        reloaded.Load();
        var job = reloaded.Jobs.Single(j => j.Id == "rt");
        Assert.Equal(1234, job.BudgetTokens);
        Assert.Equal(JobBudgetExceededAction.Stop, job.BudgetExceededAction);
    }
}
