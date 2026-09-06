// Copyright (c) AeroCode
// advisor args 脱敏 + 自动采纳收紧门测试（批次 C 安全切片，builder-γ）：
// 词表单一事实源（Harness SensitiveTextScrubber）复用验证、prompt 构造前脱敏（占位符呈现）、
// 收紧门纯函数裁决（args 无修改 / 白名单 / 转人工）。
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using AeroAgent.Moa.Safety;
using Xunit;

namespace AeroCode.Tests.MoaTests;

public sealed class AdvisorArgsSanitizerTests
{
    // 命中 canonical 词表 sk- 形态（sk- + ≥12 位）的确定性样例（测试专用假凭据）。
    private const string FakeKey = "sk-ABCDEFGHIJKLMNOPQRSTUVWXYZ123456";

    [Fact]
    public void Sanitize_SecretInArgs_Redacted_PlaceholderPresent_ParamNameTracked()
    {
        var args = new Dictionary<string, object?> { ["api_key"] = FakeKey };

        var report = AdvisorArgsSanitizer.Sanitize(args);

        Assert.True(report.Modified);
        Assert.Equal(new[] { "api_key" }, report.ModifiedParameterNames);
        Assert.Contains(AdvisorArgsSanitizer.RedactionPlaceholder, report.SanitizedPreview, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeKey, report.SanitizedPreview, StringComparison.Ordinal);
    }

    [Fact]
    public void Sanitize_CleanArgs_NotModified_PreviewIntact()
    {
        var args = new Dictionary<string, object?> { ["command"] = "git status" };

        var report = AdvisorArgsSanitizer.Sanitize(args);

        Assert.False(report.Modified);
        Assert.Empty(report.ModifiedParameterNames);
        Assert.Contains("git status", report.SanitizedPreview, StringComparison.Ordinal);
    }

    [Fact]
    public void Sanitize_NullOrEmptyArgs_NoModification()
    {
        var nullReport = AdvisorArgsSanitizer.Sanitize(null);
        Assert.False(nullReport.Modified);
        Assert.Equal(string.Empty, nullReport.SanitizedPreview);

        var emptyReport = AdvisorArgsSanitizer.Sanitize(new Dictionary<string, object?>());
        Assert.False(emptyReport.Modified);
        Assert.Empty(emptyReport.ModifiedParameterNames);
    }

    [Fact]
    public void Sanitize_BearerFormInCommandValue_Tracked()
    {
        var args = new Dictionary<string, object?>
        {
            ["command"] = "curl -H \"Authorization: Bearer abcdef123456\" https://example.test",
        };

        var report = AdvisorArgsSanitizer.Sanitize(args);

        Assert.True(report.Modified);
        Assert.Contains("command", report.ModifiedParameterNames);
        Assert.DoesNotContain("abcdef123456", report.SanitizedPreview, StringComparison.Ordinal);
        Assert.Contains(AdvisorArgsSanitizer.RedactionPlaceholder, report.SanitizedPreview, StringComparison.Ordinal);
    }

    [Fact]
    public void Sanitize_MultipleSensitiveParams_AllTrackedInOrder()
    {
        var args = new Dictionary<string, object?>
        {
            ["token"] = FakeKey,
            ["fingerprint"] = "aabbccdd00112233aabbccdd00112233", // 32 位 hex 形态
            ["path"] = "src/a.cs",
        };

        var report = AdvisorArgsSanitizer.Sanitize(args);

        Assert.True(report.Modified);
        Assert.Equal(new[] { "token", "fingerprint" }, report.ModifiedParameterNames);
        Assert.DoesNotContain(FakeKey, report.SanitizedPreview, StringComparison.Ordinal);
        Assert.DoesNotContain("aabbccdd00112233aabbccdd00112233", report.SanitizedPreview, StringComparison.Ordinal);
    }

    [Fact]
    public void AdvisorPrompt_SanitizeSwitchOn_ArgsSanitizedBeforePromptConstruction_PlaceholderNotRawSecret()
    {
        // 开关开启：脱敏在 prompt 构造前——provider 收到的 user 消息只含占位符，绝无敏感原文。
        var provider = new AdvisorScriptedProvider
        {
            DefaultContent = "{\"recommend\":\"allow\",\"risk\":\"low\",\"reason\":\"ok\"}",
        };
        var advisor = new PermissionAdvisor(provider, "cheap-model", sanitizeArgs: true);
        var args = new Dictionary<string, object?> { ["api_key"] = FakeKey };

        var advice = advisor.AdviseAsync("store_secret", args, CancellationToken.None).GetAwaiter().GetResult();

        Assert.Equal(("allow", "low", "ok"), (advice.Recommend, advice.Risk, advice.Reason));
        var userMessage = provider.LastRequest!.Messages[1].Content;
        Assert.Contains(AdvisorArgsSanitizer.RedactionPlaceholder, userMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeKey, userMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void AdvisorPrompt_SanitizeSwitchOff_Default_RawArgsAsBeforeR3()
    {
        // R3 修复（F-MED-2）：开关默认关闭 = R3 前行为逐字节一致——参数原样进 prompt。
        var provider = new AdvisorScriptedProvider
        {
            DefaultContent = "{\"recommend\":\"allow\",\"risk\":\"low\",\"reason\":\"ok\"}",
        };
        var advisor = new PermissionAdvisor(provider, "cheap-model"); // 默认 sanitizeArgs=false
        var args = new Dictionary<string, object?> { ["api_key"] = FakeKey };

        _ = advisor.AdviseAsync("store_secret", args, CancellationToken.None).GetAwaiter().GetResult();

        var userMessage = provider.LastRequest!.Messages[1].Content;
        Assert.Contains(FakeKey, userMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(AdvisorArgsSanitizer.RedactionPlaceholder, userMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void AdvisorPrompt_SanitizeSwitchOn_NestedSecret_Redacted()
    {
        // 开关开启 + 嵌套敏感值：S-MED-1 修复后的检测口径同样作用于 advisor prompt。
        var provider = new AdvisorScriptedProvider
        {
            DefaultContent = "{\"recommend\":\"ask\",\"risk\":\"medium\",\"reason\":\"nested\"}",
        };
        var advisor = new PermissionAdvisor(provider, "cheap-model", sanitizeArgs: true);
        var args = new Dictionary<string, object?> { ["config"] = "{\"password\":\"hunter22\"}" };

        _ = advisor.AdviseAsync("configure", args, CancellationToken.None).GetAwaiter().GetResult();

        var userMessage = provider.LastRequest!.Messages[1].Content;
        Assert.DoesNotContain("hunter22", userMessage, StringComparison.Ordinal);
        Assert.Contains(AdvisorArgsSanitizer.RedactionPlaceholder, userMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void AdvisorPrompt_CleanArgs_PreviewUnchanged()
    {
        var provider = new AdvisorScriptedProvider
        {
            DefaultContent = "{\"recommend\":\"allow\",\"risk\":\"low\",\"reason\":\"ok\"}",
        };
        var advisor = new PermissionAdvisor(provider, "cheap-model");

        _ = advisor.AdviseAsync(
            "run_shell", new Dictionary<string, object?> { ["command"] = "git status" }, CancellationToken.None)
            .GetAwaiter().GetResult();

        Assert.Contains("git status", provider.LastRequest!.Messages[1].Content, StringComparison.Ordinal);
    }
}

public sealed class AdvisorAutoAdoptGateTests
{
    private const string FakeKey = "sk-ABCDEFGHIJKLMNOPQRSTUVWXYZ123456";

    private static AdvisorArgsSanitizationReport Report(
        bool modified, params string[] modifiedParams) =>
        new(modified ? "sanitized" : "raw", modified, modifiedParams);

    [Fact]
    public void UnmodifiedArgs_Allow()
    {
        var result = AdvisorAutoAdoptGate.Evaluate(Report(modified: false), modifiedArgsWhitelist: null);
        Assert.True(result.Allow);
        Assert.Equal(AutoAdoptDecision.Allow, result.Decision);
    }

    [Fact]
    public void NullReport_Allow()
    {
        Assert.True(AdvisorAutoAdoptGate.Evaluate(null, null).Allow);
    }

    [Fact]
    public void ModifiedArgs_EmptyWhitelist_FallThroughToApproval()
    {
        // 默认空集白名单：被脱敏即转人工（收紧语义；仅显式启用收紧开关时被消费）。
        var result = AdvisorAutoAdoptGate.Evaluate(Report(modified: true, "api_key"), modifiedArgsWhitelist: null);

        Assert.False(result.Allow);
        Assert.Equal(AutoAdoptDecision.FallThroughToApproval, result.Decision);
        Assert.Contains("api_key", result.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeKey, result.Reason, StringComparison.Ordinal); // 理由不含敏感原文
    }

    [Fact]
    public void ModifiedArgs_AllParamsWhitelisted_Allow()
    {
        var whitelist = new HashSet<string>(StringComparer.Ordinal) { "api_key" };

        var result = AdvisorAutoAdoptGate.Evaluate(Report(modified: true, "api_key"), whitelist);

        Assert.True(result.Allow);
    }

    [Fact]
    public void ModifiedArgs_PartialWhitelist_FallThrough()
    {
        var whitelist = new HashSet<string>(StringComparer.Ordinal) { "path" };

        var result = AdvisorAutoAdoptGate.Evaluate(Report(modified: true, "api_key", "token"), whitelist);

        Assert.False(result.Allow);
    }

    [Fact]
    public void ModifiedArgs_WhitelistCaseSensitiveByDefault()
    {
        // 白名单按 Ordinal 精确匹配（最小化语义：不悄悄放宽）。
        var whitelist = new HashSet<string>(StringComparer.Ordinal) { "API_KEY" };

        var result = AdvisorAutoAdoptGate.Evaluate(Report(modified: true, "api_key"), whitelist);

        Assert.False(result.Allow);
    }

    [Fact]
    public void RealSanitizeReport_FlowsThroughGate()
    {
        var report = AdvisorArgsSanitizer.Sanitize(
            new Dictionary<string, object?> { ["api_key"] = FakeKey });

        Assert.False(AdvisorAutoAdoptGate.Evaluate(report, null).Allow);

        Assert.True(AdvisorAutoAdoptGate.Evaluate(
            report, new HashSet<string>(StringComparer.Ordinal) { "api_key" }).Allow);
    }

    [Fact]
    public void UnmodifiedReport_ParamNamesNeverContainSecret()
    {
        // 任何 GateResult.Reason / Report.SanitizedPreview 都不得夹带原文（占位符替代）。
        var report = AdvisorArgsSanitizer.Sanitize(
            new Dictionary<string, object?> { ["command"] = $"deploy --token {FakeKey}" });

        Assert.True(report.Modified);
        Assert.All(
            new[] { report.SanitizedPreview },
            text => Assert.DoesNotContain(FakeKey, text, StringComparison.Ordinal));
        Assert.Single(report.ModifiedParameterNames);
        Assert.Equal("command", report.ModifiedParameterNames.Single());
    }
}

/// <summary>
/// R3 修复（S-MED-1）：嵌套结构 + JSON 转义绕过回归。旧实现对整段序列化文本过词表，
/// 嵌套值的 JSON 转义（\"、\uXXXX）使 key=value 与字符类模式失配 → 漏检 → Modified=false
/// → 收紧门误判"未修改"。修复后检测覆盖未转义原始值（递归字符串叶子 + GetString 还原）。
/// 嵌套值形态与生产一致：ToolRouter.MaterializeArgs 对嵌套对象/数组保留 GetRawText 原始文本。
/// </summary>
public sealed class AdvisorArgsSanitizerNestedEscapeTests
{
    private const string FakeKey = "sk-ABCDEFGHIJKLMNOPQRSTUVWXYZ123456";

    [Fact]
    public void NestedJsonObject_SensitiveKeyValue_Detected_Redacted_ModifiedTrue()
    {
        // 嵌套 {"password":"hunter22"}：序列化转义曾使 password 与 : 隔 \ 失配（旧实现漏检）。
        var args = new Dictionary<string, object?>
        {
            ["config"] = "{\"password\":\"hunter22\"}", // MaterializeArgs GetRawText 形态
        };

        var report = AdvisorArgsSanitizer.Sanitize(args);

        Assert.True(report.Modified);
        Assert.Equal(new[] { "config" }, report.ModifiedParameterNames);
        Assert.DoesNotContain("hunter22", report.SanitizedPreview, StringComparison.Ordinal);
        Assert.Contains(AdvisorArgsSanitizer.RedactionPlaceholder, report.SanitizedPreview, StringComparison.Ordinal);
    }

    [Fact]
    public void NestedJson_UnicodeEscapedSecret_OnlyCatchableAfterUnescape_Detected()
    {
        // 嵌套值内 \u0073（='s'）转义：原始文本形态下 sk-/Bearer 模式均失配，
        // 只有解析后 GetString 还原未转义原值（"Bearer sk-…"）才命中——检测必须覆盖该层。
        var args = new Dictionary<string, object?>
        {
            ["auth"] = "{\"header\":\"Bearer \\u0073k-ABCDEFGHIJKLMNOP1234\"}",
        };

        var report = AdvisorArgsSanitizer.Sanitize(args);

        Assert.True(report.Modified);
        Assert.Equal(new[] { "auth" }, report.ModifiedParameterNames);
        Assert.DoesNotContain("ABCDEFGHIJKLMNOP1234", report.SanitizedPreview, StringComparison.Ordinal);
    }

    [Fact]
    public void NestedJson_KeywordValueWithUnicodeEscape_Detected()
    {
        // \u002B 转义落在 key=value 值内：原始文本叶子过词表仍命中（keyword 邻接未被转义破坏）。
        var args = new Dictionary<string, object?>
        {
            ["env"] = "{\"password\":\"hunter\\u002B22secret\"}",
        };

        var report = AdvisorArgsSanitizer.Sanitize(args);

        Assert.True(report.Modified);
        Assert.Equal(new[] { "env" }, report.ModifiedParameterNames);
        Assert.DoesNotContain("22secret", report.SanitizedPreview, StringComparison.Ordinal);
    }

    [Fact]
    public void NestedArray_BearerToken_Detected()
    {
        var args = new Dictionary<string, object?>
        {
            ["headers"] = "[\"Authorization: Bearer abcdef123456\"]",
        };

        var report = AdvisorArgsSanitizer.Sanitize(args);

        Assert.True(report.Modified);
        Assert.Equal(new[] { "headers" }, report.ModifiedParameterNames);
        Assert.DoesNotContain("abcdef123456", report.SanitizedPreview, StringComparison.Ordinal);
    }

    [Fact]
    public void ParameterNameItself_SensitiveForm_CountedAsModified()
    {
        // 参数名命中词表（值干净）同样计入 Modified 与 ModifiedParameterNames。
        var args = new Dictionary<string, object?>
        {
            ["cfg-sk-ABCDEFGHIJKLMNOP1234"] = "plain value",
        };

        var report = AdvisorArgsSanitizer.Sanitize(args);

        Assert.True(report.Modified);
        Assert.Equal(new[] { "cfg-sk-ABCDEFGHIJKLMNOP1234" }, report.ModifiedParameterNames);
        Assert.DoesNotContain("plain value", report.SanitizedPreview, StringComparison.Ordinal);
    }

    [Fact]
    public void CleanNestedJson_NotModified_PreviewIntact()
    {
        var args = new Dictionary<string, object?>
        {
            ["config"] = "{\"retries\":3,\"mode\":\"fast\"}",
            ["tags"] = "[\"a\",\"b\"]",
        };

        var report = AdvisorArgsSanitizer.Sanitize(args);

        Assert.False(report.Modified);
        Assert.Empty(report.ModifiedParameterNames);
        Assert.Contains("retries", report.SanitizedPreview, StringComparison.Ordinal);
    }

    [Fact]
    public void NestedHit_TighteningGate_FallsThroughToApproval()
    {
        // 收紧门消费真实 Sanitize 报告：嵌套命中 → Modified=true → 默认空集白名单转人工。
        var report = AdvisorArgsSanitizer.Sanitize(
            new Dictionary<string, object?> { ["config"] = "{\"password\":\"hunter22\"}" });

        Assert.True(report.Modified);
        var gate = AdvisorAutoAdoptGate.Evaluate(report, modifiedArgsWhitelist: null);

        Assert.False(gate.Allow);
        Assert.Equal(AutoAdoptDecision.FallThroughToApproval, gate.Decision);
        Assert.Contains("config", gate.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter22", gate.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void NestedSecret_DirectValueLeak_GoneFromPreview()
    {
        // 顶层直接值形态的嵌套 JSON：敏感原文同样不得出现在预览任何位置。
        var args = new Dictionary<string, object?>
        {
            ["payload"] = $"{{\"nested\":{{\"api_key\":\"{FakeKey}\"}}}}",
        };

        var report = AdvisorArgsSanitizer.Sanitize(args);

        Assert.True(report.Modified);
        Assert.Equal(new[] { "payload" }, report.ModifiedParameterNames);
        Assert.DoesNotContain(FakeKey, report.SanitizedPreview, StringComparison.Ordinal);
    }
}
