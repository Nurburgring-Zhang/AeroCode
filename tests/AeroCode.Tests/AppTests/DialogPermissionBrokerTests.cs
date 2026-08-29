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
/// TCS 控制的对话框呈现层：ShowAsync 开窗后等待外部完成——刻意不观察 ct，
/// 模拟"宿主不响应令牌、决定晚于取消到达"的场景，让测试能精确控制
/// "对话轮取消"与"用户点击决定"的先后顺序，验证
/// DialogPermissionBroker.cs:104-108 的中途取消契约（决定作废按 Deny 收尾）。
/// </summary>
internal sealed class TcsControlledPresenter : IPermissionDialogPresenter
{
    private readonly TaskCompletionSource<PermissionDialogResult?> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<PermissionPrompt> _shown =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>对话框已打开（收到提示词）时完成。</summary>
    public Task<PermissionPrompt> Shown => _shown.Task;

    /// <summary>模拟用户在对话框上点击（允许/拒绝/记住）或关闭（null）。</summary>
    public void Complete(PermissionDialogResult? result) => _completion.TrySetResult(result);

    public async Task<PermissionDialogResult?> ShowAsync(PermissionPrompt prompt, CancellationToken ct)
    {
        _shown.TrySetResult(prompt);
        return await _completion.Task; // 不观察 ct：由测试显式决定完成时机
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

    // ============ 授权对话框中途取消/宿主关闭行为契约 ============
    // 对应实现：DialogPermissionBroker.cs:104-108（取消后到达的决定作废按 Deny）、
    // 110-115（宿主关闭 = null → Deny）、91-94/133-137（令牌取消 → Deny）。

    /// <summary>
    /// 中途取消（决定晚到路径）：对话轮先被取消，用户随后才点"允许并记住"——
    /// 该决定必须作废、按 Deny 收尾（DialogPermissionBroker.cs:104-108），
    /// 不得把 Allow 粘滞写入策略或落盘；ResolveAsync 的 Task 正常完成，无悬挂。
    /// </summary>
    [Fact]
    public async Task MidDialogCancel_UserApprovesAfterCancellation_DecisionVoidedAsDeny()
    {
        var tcsPresenter = new TcsControlledPresenter();
        var broker = new DialogPermissionBroker(_policy, _store, tcsPresenter);
        using var cts = new CancellationTokenSource();

        var resolveTask = broker.ResolveAsync("write_file", null, cts.Token).AsTask();

        // 对话框确已打开（收到提示词）后才取消——复现"中途取消"
        var prompt = await tcsPresenter.Shown.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("write_file", prompt.ToolName);

        cts.Cancel(); // 对话轮先取消
        tcsPresenter.Complete(new PermissionDialogResult(Approved: true, Remember: true)); // 决定晚到

        var decision = await resolveTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(PermissionDecision.Deny, decision); // 决定作废 → 诚实拒绝，绝不放行
        Assert.True(resolveTask.IsCompleted);            // 无悬挂 Task
        Assert.Equal(PermissionDecision.Ask, _policy.Check("write_file").Decision); // 策略无粘滞 Allow
        Assert.False(File.Exists(_permFile));            // "记住"也未落盘
    }

    /// <summary>
    /// 中途取消（令牌被观察路径）：呈现层随 ct 等待，取消使 ShowAsync 抛
    /// OperationCanceledException → broker 统一按 Deny 收尾
    /// （DialogPermissionBroker.cs:91-94、133-137）；Task 完成不悬挂、无残留状态。
    /// </summary>
    [Fact]
    public async Task CancelWhileDialogOpen_TokenObservedPath_DeniesWithoutDanglingTask()
    {
        _presenter.DelayMs = 10_000; // 对话框"一直等用户决定"
        using var cts = new CancellationTokenSource();

        var resolveTask = _broker.ResolveAsync("write_file", null, cts.Token).AsTask();

        // 等到对话框真的打开再取消
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (_presenter.Prompts.Count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
        Assert.Single(_presenter.Prompts);

        cts.Cancel();

        var decision = await resolveTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(PermissionDecision.Deny, decision);
        Assert.True(resolveTask.IsCompleted);
        Assert.Equal(PermissionDecision.Ask, _policy.Check("write_file").Decision);
        Assert.False(File.Exists(_permFile));
    }

    /// <summary>
    /// 取消后状态无粘滞：中途取消过一次之后，同一工具再次请求权限必须照常弹窗、
    /// 新用户决定照常生效——取消路径正确释放信号量（不死锁）、不污染策略。
    /// </summary>
    [Fact]
    public async Task AfterMidDialogCancel_NextRequestStillPrompts_NoStickyState()
    {
        // 第一次：弹窗中途取消 → Deny
        _presenter.DelayMs = 10_000;
        using var cts = new CancellationTokenSource();
        var first = _broker.ResolveAsync("write_file", null, cts.Token).AsTask();

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (_presenter.Prompts.Count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
        Assert.Single(_presenter.Prompts);
        cts.Cancel();
        Assert.Equal(PermissionDecision.Deny, await first.WaitAsync(TimeSpan.FromSeconds(10)));

        // 第二次：全新对话轮（未取消令牌）→ 照常弹窗，用户新决定生效
        _presenter.DelayMs = 0;
        _presenter.Enqueue(new PermissionDialogResult(Approved: true, Remember: false));

        var second = await _broker.ResolveAsync("write_file", null, CancellationToken.None);

        Assert.Equal(PermissionDecision.Allow, second); // 取消不产生粘滞拒绝
        Assert.Equal(2, _presenter.Prompts.Count);      // 第二次弹窗确实发生（门未被取消卡死）
        Assert.Equal(PermissionDecision.Ask, _policy.Check("write_file").Decision); // 未记住 → 策略如旧
    }

    /// <summary>
    /// 对话框宿主中途关闭：presenter 按契约返回 null（关闭/无决定）→
    /// broker 按 Deny 收尾（DialogPermissionBroker.cs:110-115）；
    /// 宿主关闭同样不产生粘滞状态，后续请求照常弹窗。
    /// 说明：实现中没有独立的"超时"分支——超时最终表现为令牌取消（OCE → Deny）
    /// 或宿主返回 null（→ Deny），殊途同归。
    /// </summary>
    [Fact]
    public async Task HostClosesDialog_MidFlow_Denies_AndNextRequestStillPrompts()
    {
        _presenter.Enqueue(null); // 用户直接关掉对话框窗口 → 未产生决定

        var decision = await _broker.ResolveAsync("write_file", null, CancellationToken.None);

        Assert.Equal(PermissionDecision.Deny, decision);
        Assert.Equal(PermissionDecision.Ask, _policy.Check("write_file").Decision);
        Assert.False(File.Exists(_permFile));

        // 宿主关闭不是终态：下一次授权请求照常弹窗且决定生效
        _presenter.Enqueue(new PermissionDialogResult(Approved: true, Remember: false));
        var second = await _broker.ResolveAsync("write_file", null, CancellationToken.None);

        Assert.Equal(PermissionDecision.Allow, second);
        Assert.Equal(2, _presenter.Prompts.Count);
    }

    /// <summary>
    /// 中途取消端到端（ToolRouter 链路）：授权对话框弹出期间取消对话轮 →
    /// 该次工具调用如实 Deny，且工具本体绝不执行（"后续工具调用未被放行"）。
    /// </summary>
    [Fact]
    public async Task MidDialogCancel_EndToEnd_ToolCallDeniedAndNeverExecuted()
    {
        var box = new ScriptedToolbox("demo",
            new ToolDefinition { Name = "danger_tool", Description = "高风险操作" });
        box.SetResult("danger_tool", ToolInvokeResult.Ok("EXECUTED"));
        var registry = new ToolboxRegistry();
        registry.Register(box);
        var router = new ToolRouter(registry, _policy, _broker);

        _presenter.DelayMs = 10_000;
        using var cts = new CancellationTokenSource();

        var invokeTask = router.InvokeAsync("danger_tool", "{}", cts.Token);

        // 等对话框弹出后再取消：复现"授权进行到一半，用户取消了整轮对话"
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (_presenter.Prompts.Count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
        Assert.Single(_presenter.Prompts);
        cts.Cancel();

        var result = await invokeTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(result.Denied); // 拿不到有效决定 → 诚实拒绝
        Assert.Contains("Permission denied", result.Output);
        Assert.Empty(box.Invocations); // 工具从未被执行
        Assert.Equal(PermissionDecision.Ask, _policy.Check("danger_tool").Decision); // 未记住任何决定
    }
}
