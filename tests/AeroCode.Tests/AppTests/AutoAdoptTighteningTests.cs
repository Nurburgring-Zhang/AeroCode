// Copyright (c) AeroCode
// 自动采纳收紧端到端测试（批次 C 安全切片，builder-γ）：DialogPermissionBroker 新增收紧开关。
// 开关默认 false = 现行为逐字节一致（敏感参数也照旧自动采纳——现状即无条件自动采纳）；
// 开关显式启用后：args 无修改 → 采纳；被脱敏且全参数在白名单 → 采纳；否则转既有审批弹窗
// （不静默丢弃：advisorNote 附加收紧原因，用户可放行/拒绝）。
// 不真启 LLM：advisor 用脚本化 provider（复用 MoaTests.AdvisorScriptedProvider）。
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AeroAgent.Moa.Safety;
using AeroCode.AI.Models;
using AeroCode.App.Services;
using AeroCode.Harness.EventBus;
using AeroCode.Harness.Permission;
using AeroCode.Tests.MoaTests;
using Xunit;

namespace AeroCode.Tests.AppTests;

public sealed class AutoAdoptTighteningTests : IDisposable
{
    // 命中 canonical 词表 sk- 形态的确定性假凭据（测试专用）。
    private const string FakeKey = "sk-ABCDEFGHIJKLMNOPQRSTUVWXYZ123456";

    private readonly PermissionPolicy _policy;
    private readonly JsonPermissionStore _store;
    private readonly ScriptedPresenter _presenter;
    private readonly AdvisorScriptedProvider _provider;
    private readonly string _dir;

    public AutoAdoptTighteningTests()
    {
        _policy = PermissionPolicy.CreateDefault(new EventBus()); // write_file = Ask
        _dir = Path.Combine(Path.GetTempPath(), $"adopt_tighten_{Guid.NewGuid():N}");
        _store = new JsonPermissionStore(Path.Combine(_dir, "permissions.json"));
        _presenter = new ScriptedPresenter();
        _provider = new AdvisorScriptedProvider
        {
            DefaultContent = "{\"recommend\":\"allow\",\"risk\":\"low\",\"reason\":\"looks harmless\"}",
        };
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort 清理 */ }
    }

    private DialogPermissionBroker CreateBroker(bool tightenAutoAdopt = false, IEnumerable<string>? whitelist = null) =>
        new(
            _policy,
            _store,
            _presenter,
            advisor: new PermissionAdvisor(_provider, "cheap-model"),
            autoApproveLowRisk: true,
            tightenAutoAdopt: tightenAutoAdopt,
            autoAdoptModifiedArgsWhitelist: whitelist);

    private static Dictionary<string, object?> SensitiveArgs() => new()
    {
        ["content"] = $"deploy with {FakeKey}",
    };

    private static Dictionary<string, object?> CleanArgs() => new()
    {
        ["content"] = "plain release notes",
    };

    [Fact]
    public async Task TighteningOff_SensitiveArgs_AutoAdoptsAsBefore_ByteCompatible()
    {
        // 默认（开关关闭）：现状逐字节一致——risk=low 即自动采纳，即便参数含敏感形态也照旧
        // （现状无白名单概念，收紧逻辑不得在默认路径生效）。
        var broker = CreateBroker();

        var decision = await broker.ResolveAsync("write_file", SensitiveArgs(), CancellationToken.None);

        Assert.Equal(PermissionDecision.Allow, decision);
        Assert.Empty(_presenter.Prompts); // 未弹窗：自动采纳与既有行为一致
        Assert.Equal(1, _provider.CallCount);
    }

    [Fact]
    public async Task TighteningOn_CleanArgs_AutoAdopts()
    {
        var broker = CreateBroker(tightenAutoAdopt: true);

        var decision = await broker.ResolveAsync("write_file", CleanArgs(), CancellationToken.None);

        Assert.Equal(PermissionDecision.Allow, decision);
        Assert.Empty(_presenter.Prompts); // args 无修改 → 收紧门放行
    }

    [Fact]
    public async Task TighteningOn_SensitiveArgs_FallsThroughToDialog_NotSilentlyDropped()
    {
        // 收紧启用 + 被脱敏 + 默认空集白名单 → 转既有审批路径（弹窗），advisorNote 附加收紧原因。
        _presenter.Enqueue(new PermissionDialogResult(Approved: true, Remember: false));
        var broker = CreateBroker(tightenAutoAdopt: true);

        var decision = await broker.ResolveAsync("write_file", SensitiveArgs(), CancellationToken.None);

        // 不静默丢弃：人工审批路径可用且放行生效
        Assert.Equal(PermissionDecision.Allow, decision);
        var prompt = Assert.Single(_presenter.Prompts);
        Assert.NotNull(prompt.AdvisorNote);
        Assert.Contains("[自动采纳收紧]", prompt.AdvisorNote, StringComparison.Ordinal);
        Assert.Contains("content", prompt.AdvisorNote, StringComparison.Ordinal);      // 被脱敏参数名可见
        Assert.DoesNotContain(FakeKey, prompt.AdvisorNote, StringComparison.Ordinal);  // 敏感原文不入弹窗
        Assert.Equal(PermissionDecision.Ask, _policy.Check("write_file").Decision);    // 无粘滞放行
    }

    [Fact]
    public async Task TighteningOn_SensitiveArgs_WhitelistedParam_AutoAdopts()
    {
        // 白名单显式含被脱敏参数名 → 修改通过脱敏 + 白名单校验 → 自动采纳。
        var broker = CreateBroker(tightenAutoAdopt: true, whitelist: new[] { "content" });

        var decision = await broker.ResolveAsync("write_file", SensitiveArgs(), CancellationToken.None);

        Assert.Equal(PermissionDecision.Allow, decision);
        Assert.Empty(_presenter.Prompts);
    }

    [Fact]
    public async Task TighteningOn_SensitiveArgs_WhitelistMisses_FallsThrough()
    {
        // 白名单不含被脱敏参数 → 转人工。
        _presenter.Enqueue(new PermissionDialogResult(Approved: false, Remember: false));
        var broker = CreateBroker(tightenAutoAdopt: true, whitelist: new[] { "path" });

        var decision = await broker.ResolveAsync("write_file", SensitiveArgs(), CancellationToken.None);

        Assert.Equal(PermissionDecision.Deny, decision);
        Assert.Single(_presenter.Prompts); // 走了弹窗而非自动采纳
    }

    [Fact]
    public async Task RecommendAsk_LowRisk_NotAutoAdopted_FallsThroughToDialog()
    {
        // R4 δ-2：advisor 说 ask（语义=应由人裁决）时，即便 risk=low 且 AutoApproveLowRisk 开启，
        // 也绝不自动采纳——转弹窗人工裁决（收紧开关关闭路径同样生效）。
        _provider.DefaultContent = "{\"recommend\":\"ask\",\"risk\":\"low\",\"reason\":\"double-check this\"}";
        _presenter.Enqueue(new PermissionDialogResult(Approved: true, Remember: false));
        var broker = CreateBroker(); // tightenAutoAdopt 默认 false

        var decision = await broker.ResolveAsync("write_file", CleanArgs(), CancellationToken.None);

        Assert.Equal(PermissionDecision.Allow, decision); // 人工放行生效（非自动采纳）
        var prompt = Assert.Single(_presenter.Prompts);   // 确实弹了窗
        Assert.NotNull(prompt.AdvisorNote);
        Assert.Contains("ask", prompt.AdvisorNote, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecommendAsk_TighteningOn_AlsoFallsThroughToDialog()
    {
        // 收紧开启路径：ask 在外层条件即被排除，弹窗展示 advisor 建议。
        _provider.DefaultContent = "{\"recommend\":\"ask\",\"risk\":\"low\",\"reason\":\"double-check this\"}";
        _presenter.Enqueue(new PermissionDialogResult(Approved: false, Remember: false));
        var broker = CreateBroker(tightenAutoAdopt: true);

        var decision = await broker.ResolveAsync("write_file", CleanArgs(), CancellationToken.None);

        Assert.Equal(PermissionDecision.Deny, decision); // 人工拒绝生效
        Assert.Single(_presenter.Prompts);
    }

    [Fact]
    public async Task RecommendAllow_LowRisk_StillAutoAdopts()
    {
        // δ-2 不误伤允许路径：recommend=allow + risk=low 照旧自动采纳、不弹窗。
        _provider.DefaultContent = "{\"recommend\":\"allow\",\"risk\":\"low\",\"reason\":\"harmless\"}";
        var broker = CreateBroker();

        var decision = await broker.ResolveAsync("write_file", CleanArgs(), CancellationToken.None);

        Assert.Equal(PermissionDecision.Allow, decision);
        Assert.Empty(_presenter.Prompts);
    }

    [Fact]
    public async Task TighteningOn_NonLowRiskAdvice_ExistingPathUnaffected()
    {
        // 收紧门只管"低风险自动采纳"分支：risk=medium 照旧弹窗（与既有行为一致）。
        _provider.DefaultContent = "{\"recommend\":\"ask\",\"risk\":\"medium\",\"reason\":\"writes files\"}";
        _presenter.Enqueue(new PermissionDialogResult(Approved: true, Remember: false));
        var broker = CreateBroker(tightenAutoAdopt: true);

        var decision = await broker.ResolveAsync("write_file", CleanArgs(), CancellationToken.None);

        Assert.Equal(PermissionDecision.Allow, decision);
        Assert.Single(_presenter.Prompts);
    }

    [Fact]
    public async Task TighteningOn_AdvisorUnavailable_ExistingPathUnaffected()
    {
        // 无 advisor（未配置判定模型）：收紧门不参与，既有 Ask 弹窗路径照旧。
        _presenter.Enqueue(new PermissionDialogResult(Approved: true, Remember: false));
        var broker = new DialogPermissionBroker(_policy, _store, _presenter, autoApproveLowRisk: true);

        var decision = await broker.ResolveAsync("write_file", SensitiveArgs(), CancellationToken.None);

        Assert.Equal(PermissionDecision.Allow, decision);
        Assert.Single(_presenter.Prompts);
        Assert.Equal(0, _provider.CallCount);
    }
}
