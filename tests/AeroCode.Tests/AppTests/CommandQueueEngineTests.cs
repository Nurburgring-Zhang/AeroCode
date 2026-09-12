// Copyright (c) AeroCode
// CommandQueueEngine 回归测试（独立评审 H1/H2 修复钉死）。
// H1：StopQueue 后旧循环未退出前 IsRunning 恒为 true（单飞），第二个循环被守卫挡住。
// H2：执行期间被插队/上移挪走的条目，执行完后按引用移除，绝不被二次执行。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AeroCode.App.ViewModels;
using Xunit;

namespace AeroCode.Tests.AppTests;

public sealed class CommandQueueEngineTests
{
    private static async Task WaitAsync(Func<bool> cond, int timeoutMs = 4000)
    {
        var sw = Stopwatch.StartNew();
        while (!cond() && sw.ElapsedMilliseconds < timeoutMs)
        {
            await Task.Delay(10);
        }

        Assert.True(cond(), $"condition not met within {timeoutMs}ms");
    }

    [Fact]
    public async Task Loop_Executes_Each_Item_Exactly_Once()
    {
        var executed = new List<string>();
        var engine = new CommandQueueEngine((text, _) =>
        {
            lock (executed) executed.Add(text);
            return Task.FromResult(true);
        });

        engine.Enqueue("A"); // 空闲 → 自动开始
        await WaitAsync(() => !engine.IsRunning && engine.Queue.Count == 0);

        lock (executed) Assert.Equal(new[] { "A" }, executed.ToArray());
        Assert.Contains("执行完毕", engine.StatusText);
    }

    [Fact]
    public async Task JumpToFront_During_Execution_No_Duplicate_Run()
    {
        // H2 回归：A 执行中把 C 插队到队首；修复前 A 会因"不再是 Queue[0]"而残留并被二次执行。
        var executed = new List<string>();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new CommandQueueEngine(async (text, _) =>
        {
            lock (executed) executed.Add(text);
            if (text == "A") await gate.Task; // A 阻塞，留给测试操纵队列
            return true;
        });

        engine.Enqueue("A"); // 自动开始，A 阻塞在 gate
        await WaitAsync(() => { lock (executed) return executed.Contains("A"); });

        engine.Enqueue("B"); // IsRunning=true → 仅累积，不自动开始
        engine.Enqueue("C");
        var c = engine.Queue.First(q => q.Text == "C");
        engine.JumpQueuedToFrontCommand(c); // Queue: [A,B,C] → [C,A,B]

        gate.SetResult(); // 放行 A
        await WaitAsync(() => !engine.IsRunning && engine.Queue.Count == 0);

        // 每条恰好一次；顺序 = A（已在跑）→ C（插队到首）→ B
        lock (executed) Assert.Equal(new[] { "A", "C", "B" }, executed.ToArray());
    }

    [Fact]
    public async Task Stop_Keeps_Single_Flight_Until_Wound_Down()
    {
        // H1 回归：StopQueue 只置停止标志，不清 IsRunning——收敛中第二个循环被挡住。
        var started = 0;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new CommandQueueEngine(async (_, _) =>
        {
            Interlocked.Increment(ref started);
            await gate.Task; // 不观察 ct：停止后仍阻塞，直到放行
            return true;
        });

        engine.Enqueue("A"); // 自动开始，阻塞在 gate
        await WaitAsync(() => Volatile.Read(ref started) == 1);

        engine.Enqueue("B"); // 累积
        engine.StopQueue(); // _stopRequested=true；IsRunning 仍应为 true（旧循环未退出）
        Assert.True(engine.IsRunning);

        // 试图再起一个循环：被单飞守卫立即挡回（不新增执行）。
        await engine.RunQueueAsync();
        Assert.True(engine.IsRunning);
        Assert.Equal(1, Volatile.Read(ref started));

        gate.SetResult(); // 放行 A → 旧循环收敛退出
        await WaitAsync(() => !engine.IsRunning);

        Assert.Equal(1, Volatile.Read(ref started)); // 只有 A 执行过
        Assert.Single(engine.Queue); // B 因停止而保留
        Assert.Equal("B", engine.Queue[0].Text);
    }

    [Fact]
    public async Task Refused_Item_Is_Kept_And_Queue_Stops()
    {
        // M1 回归：宿主拒绝执行（返回 false）→ 该条保留在队首、队列停止、诚实上报，绝不静默吞掉。
        var engine = new CommandQueueEngine((_, _) => Task.FromResult(false)); // 恒拒绝

        engine.Enqueue("A"); // 空闲 → 自动开始；执行 A 被拒
        await WaitAsync(() => engine.StatusText.Contains("无法执行") || engine.StatusText.Contains("执行完毕"));

        Assert.Contains("无法执行", engine.StatusText); // 诚实的暂停提示
        Assert.False(engine.IsRunning);
        Assert.Single(engine.Queue); // A 被保留，未被吞掉
        Assert.Equal("A", engine.Queue[0].Text);
    }

    [Fact]
    public async Task Failed_Items_Are_Counted_In_Final_Status()
    {
        // M4 回归：失败的条不被"✓ 队列执行完毕"掩盖，收尾如实上报失败数。
        var engine = new CommandQueueEngine((text, _) =>
        {
            if (text == "bad") throw new InvalidOperationException("boom");
            return Task.FromResult(true);
        });
        engine.Queue.Add(new QueuedCommandViewModel { Text = "bad" });
        engine.Queue.Add(new QueuedCommandViewModel { Text = "good" });

        await engine.RunQueueAsync();

        Assert.Contains("1 条失败", engine.StatusText);
        Assert.Empty(engine.Queue);
    }

    [Fact]
    public async Task NotifyHostIdle_Resumes_Parked_Queue()
    {
        // M2 回归：宿主正忙时入队不自动开始（条目积压）；宿主转空闲调 NotifyHostIdle 后接续执行。
        var executed = new List<string>();
        var hostIdle = false;
        var engine = new CommandQueueEngine((text, _) =>
        {
            lock (executed) executed.Add(text);
            return Task.FromResult(true);
        }, () => hostIdle);

        engine.Enqueue("A"); // hostIdle=false → 不自动开始，A 积压
        Assert.False(engine.IsRunning);
        Assert.Single(engine.Queue);

        hostIdle = true;
        engine.NotifyHostIdle(); // 转空闲 → 接续执行积压队列
        await WaitAsync(() => !engine.IsRunning && engine.Queue.Count == 0);

        lock (executed) Assert.Equal(new[] { "A" }, executed.ToArray());
    }
}
