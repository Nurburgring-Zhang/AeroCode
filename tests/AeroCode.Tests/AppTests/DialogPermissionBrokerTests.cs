// Copyright (c) AeroCode V3.0
// DialogPermissionBroker tests — real PermissionPolicy + real permissions.json on temp disk;
// the dialog presenter is scripted (test double for the Avalonia window itself).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AeroAgent.Moa.Tools;
using AeroCode.AI.Models;
using AeroCode.App.Services;
using AeroCode.Harness.EventBus;
using AeroCode.Harness.Permission;
using AeroCode.Tests.MoaTests;
using Xunit;

namespace AeroCode.Tests.AppTests;

/// <summary>可编程对话框呈现层：按脚本出队决定，记录收到的提示词与并发峰值。</summary>
internal sealed class ScriptedPresenter : IPermissionDialogPresenter
{
    private readonly Queue<PermissionDialogResult?> _results = new();
    private int _inFlight;

    public List<PermissionPrompt> Prompts { get; } = new();
    public int DelayMs { get; set; }
    public int MaxObservedConcurrency { get; private set; }

    /// <summary>设置后 ShowAsync 抛此异常（模拟呈现层崩溃）。</summary>
    public Exception? Throw { get; set; }

    public void Enqueue(PermissionDialogResult? result) => _results.Enqueue(result);

    public async Task<PermissionDialogResult?> ShowAsync(PermissionPrompt prompt, CancellationToken ct)
    {
        lock (Prompts)
        {
            Prompts.Add(prompt);
        }

        var current = Interlocked.Increment(ref _inFlight);
        lock (this)
        {
            MaxObservedConcurrency = Math.Max(MaxObservedConcurrency, current);
        }

        try
        {
            if (Throw is not null)
            {
                throw Throw;
            }

            if (DelayMs > 0)
            {
                await Task.Delay(DelayMs, ct);
            }

            lock (_results)
            {
                return _results.Count > 0 ? _results.Dequeue() : null;
            }
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }
}

/// <summary>
/// 授权代理行为纪律验证：拿不到决定一律 Deny；记住选择即时写策略 + 落盘；
/// 并发授权请求串行弹窗且门内复检避免重复打扰；取消按拒绝收尾。
/// </summary>
public sealed class DialogPermissionBrokerTests : IDisposable
{
    private readonly PermissionPolicy _policy;
    private readonly JsonPermissionStore _store;
    private readonly ScriptedPresenter _presenter;
    private readonly DialogPermissionBroker _broker;
    private readonly string _dir;
    private readonly string _permFile;

    public DialogPermissionBrokerTests()
    {
        _policy = PermissionPolicy.CreateDefault(new EventBus());
        _dir = Path.Combine(Path.GetTempPath(), $"perm_broker_{Guid.NewGuid():N}");
        _permFile = Path.Combine(_dir, "permissions.json");
        _store = new JsonPermissionStore(_permFile);
        _presenter = new ScriptedPresenter();
        _broker = new DialogPermissionBroker(_policy, _store, _presenter);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public async Task AllowOnce_NoRemember_PolicyAndDiskUntouched()
    {
        // write_file 在 CreateDefault 中是 Ask
        _presenter.Enqueue(new PermissionDialogResult(Approved: true, Remember: false));

        var decision = await _broker.ResolveAsync("write_file", null, CancellationToken.None);

        Assert.Equal(PermissionDecision.Allow, decision);
        Assert.Equal(PermissionDecision.Ask, _policy.Check("write_file").Decision); // 未记住
        Assert.False(File.Exists(_permFile));                                        // 未落盘
        var prompt = Assert.Single(_presenter.Prompts);
        Assert.Equal("write_file", prompt.ToolName);
        Assert.Equal("(无参数)", prompt.ArgumentsPreview);
    }

    [Fact]
    public async Task AllowWithRemember_PolicyBecomesAllow_AndPersistedToFile()
    {
        _presenter.Enqueue(new PermissionDialogResult(Approved: true, Remember: true));

        var decision = await _broker.ResolveAsync("write_file", null, CancellationToken.None);

        Assert.Equal(PermissionDecision.Allow, decision);
        Assert.Equal(PermissionDecision.Allow, _policy.Check("write_file").Decision);
        var loaded = await _store.LoadAsync();
        Assert.Equal(PermissionDecision.Allow, loaded.ToolDecisions["write_file"]);

        // 记住之后再次征求：门内复检直接放行，不再弹窗
        var second = await _broker.ResolveAsync("write_file", null, CancellationToken.None);
        Assert.Equal(PermissionDecision.Allow, second);
        Assert.Single(_presenter.Prompts);
    }

    [Fact]
    public async Task DenyWithRemember_PolicyBecomesDeny_AndPersisted()
    {
        _presenter.Enqueue(new PermissionDialogResult(Approved: false, Remember: true));

        var decision = await _broker.ResolveAsync("write_file", null, CancellationToken.None);

        Assert.Equal(PermissionDecision.Deny, decision);
        var check = _policy.Check("write_file");
        Assert.Equal(PermissionDecision.Deny, check.Decision);
        Assert.Equal("Explicitly denied", check.Reason);
        var loaded = await _store.LoadAsync();
        Assert.Equal(PermissionDecision.Deny, loaded.ToolDecisions["write_file"]);
    }

    [Fact]
    public async Task NullDialogResult_Denies_NoPersistence()
    {
        // 对话框被关闭/无界面 → presenter 返回 null
        _presenter.Enqueue(null);

        var decision = await _broker.ResolveAsync("write_file", null, CancellationToken.None);

        Assert.Equal(PermissionDecision.Deny, decision);
        Assert.Equal(PermissionDecision.Ask, _policy.Check("write_file").Decision);
        Assert.False(File.Exists(_permFile));
    }

    [Fact]
    public async Task CancelledToken_Denies_NoDialogShown()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var decision = await _broker.ResolveAsync("write_file", null, cts.Token);

        Assert.Equal(PermissionDecision.Deny, decision); // 拿不到用户决定 → 诚实拒绝
        Assert.Empty(_presenter.Prompts);
    }

    [Fact]
    public async Task ConcurrentAsks_SameTool_SerializedGate_SecondReusesRememberedDecision()
    {
        _presenter.DelayMs = 150;
        _presenter.Enqueue(new PermissionDialogResult(Approved: true, Remember: true));

        // 两个并行 worker 同时请求同一工具授权
        var taskA = _broker.ResolveAsync("write_file", null, CancellationToken.None).AsTask();
        var taskB = _broker.ResolveAsync("write_file", null, CancellationToken.None).AsTask();
        var results = await Task.WhenAll(taskA, taskB);

        Assert.All(results, d => Assert.Equal(PermissionDecision.Allow, d));
        // 只弹了一次窗：第二个请求在门内复检到已记住的 Allow
        Assert.Single(_presenter.Prompts);
        Assert.Equal(1, _presenter.MaxObservedConcurrency);
    }

    [Fact]
    public async Task ToolRouterEndToEnd_DenyThenAllowThenAutoAllow()
    {
        // 未注册规则的工具 → Check = Ask → 交给 broker
        var box = new ScriptedToolbox("demo",
            new ToolDefinition { Name = "danger_tool", Description = "高风险操作" });
        box.SetResult("danger_tool", ToolInvokeResult.Ok("EXECUTED"));
        var registry = new ToolboxRegistry();
        registry.Register(box);
        var router = new ToolRouter(registry, _policy, _broker);

        // 第一次：用户拒绝 → 诚实 Deny，工具未执行
        _presenter.Enqueue(new PermissionDialogResult(Approved: false, Remember: false));
        var denied = await router.InvokeAsync("danger_tool", "{}", CancellationToken.None);
        Assert.True(denied.Denied);
        Assert.Contains("Permission denied", denied.Output);
        Assert.Empty(box.Invocations);

        // 第二次：用户允许并记住 → 真实执行
        _presenter.Enqueue(new PermissionDialogResult(Approved: true, Remember: true));
        var allowed = await router.InvokeAsync("danger_tool", "{}", CancellationToken.None);
        Assert.True(allowed.Success);
        Assert.Equal("EXECUTED", allowed.Output);
        Assert.Single(box.Invocations);

        // 第三次：已记住 → 无弹窗直达执行
        var auto = await router.InvokeAsync("danger_tool", "{}", CancellationToken.None);
        Assert.True(auto.Success);
        Assert.Equal(2, box.Invocations.Count);
        Assert.Equal(2, _presenter.Prompts.Count); // 没有第三次弹窗

        var loaded = await _store.LoadAsync();
        Assert.Equal(PermissionDecision.Allow, loaded.ToolDecisions["danger_tool"]);
    }

    [Fact]
    public async Task RememberedDeny_SurvivesOverrideAttempt()
    {
        // delete_note 式场景：默认放行 + Override 把 hard=true 升级为询问。
        // 用户记住拒绝后，显式 Deny 必须短路一切后续裁决——硬删除也不得绕过。
        _policy.SetRule(new ToolPermissionRule
        {
            ToolName = "delete_note_sim",
            DefaultDecision = PermissionDecision.Allow,
            Override = args => args is not null
                && args.TryGetValue("hard", out var hard)
                && hard is true
                ? PermissionDecision.Ask
                : PermissionDecision.Allow,
        });
        _presenter.Enqueue(new PermissionDialogResult(Approved: false, Remember: true));

        var hardArgs = new Dictionary<string, object?> { ["hard"] = true };
        var decision = await _broker.ResolveAsync("delete_note_sim", hardArgs, CancellationToken.None);
        Assert.Equal(PermissionDecision.Deny, decision);

        // 记住后：hard=true 也直接 Deny（显式拒绝先于 Override）
        Assert.Equal(PermissionDecision.Deny, _policy.Check("delete_note_sim", hardArgs).Decision);
        Assert.Equal(PermissionDecision.Deny, _policy.Check("delete_note_sim").Decision);

        // 后续征求不再弹窗
        var second = await _broker.ResolveAsync("delete_note_sim", hardArgs, CancellationToken.None);
        Assert.Equal(PermissionDecision.Deny, second);
        Assert.Single(_presenter.Prompts);
    }

    [Fact]
    public void FormatArguments_NullOrEmpty_ShowsHonestMarker()
    {
        Assert.Equal("(无参数)", DialogPermissionBroker.FormatArguments(null));
        Assert.Equal("(无参数)", DialogPermissionBroker.FormatArguments(
            new Dictionary<string, object?>()));
    }

    [Fact]
    public void FormatArguments_ChineseValues_RenderReadably_NoUnicodeEscapes()
    {
        // 授权预览是知情同意材料：中文必须原样可读（P1-2 回归）
        var args = new Dictionary<string, object?>
        {
            ["title"] = "会议纪要：量子斑马",
            ["content"] = "请记录这条笔记",
        };
        var preview = DialogPermissionBroker.FormatArguments(args);
        Assert.Contains("会议纪要：量子斑马", preview);
        Assert.Contains("请记录这条笔记", preview);
        Assert.DoesNotContain("\\u", preview, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PresenterThrows_Denies_PolicyAndDiskUntouched()
    {
        // 呈现层崩溃 = 拿不到用户决定 → 诚实拒绝（P2-2 回归），绝不放行
        _presenter.Throw = new InvalidOperationException("对话框渲染失败");

        var decision = await _broker.ResolveAsync("write_file", null, CancellationToken.None);

        Assert.Equal(PermissionDecision.Deny, decision);
        Assert.Equal(PermissionDecision.Ask, _policy.Check("write_file").Decision);
        Assert.False(File.Exists(_permFile));
    }

    [Fact]
    public void FormatArguments_ContainsKeysAndValues_TruncatesLongContent()
    {
        var args = new Dictionary<string, object?> { ["path"] = "D:\\notes\\a.md", ["hard"] = true };
        var preview = DialogPermissionBroker.FormatArguments(args);
        Assert.Contains("path", preview);
        Assert.Contains("a.md", preview);
        Assert.Contains("hard", preview);

        var huge = new Dictionary<string, object?> { ["blob"] = new string('x', 10_000) };
        var truncated = DialogPermissionBroker.FormatArguments(huge);
        Assert.EndsWith("…(参数过长已截断，完整内容仍会传给工具)", truncated);
        Assert.True(truncated.Length < 10_000);
    }

    [Fact]
    public void Constructor_NullDeps_Throw()
    {
        Assert.Throws<ArgumentNullException>(
            () => new DialogPermissionBroker(null!, _store, _presenter));
        Assert.Throws<ArgumentNullException>(
            () => new DialogPermissionBroker(_policy, null!, _presenter));
        Assert.Throws<ArgumentNullException>(
            () => new DialogPermissionBroker(_policy, _store, null!));
    }
}
