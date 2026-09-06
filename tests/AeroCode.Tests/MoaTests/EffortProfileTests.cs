using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AeroAgent.Moa.Assignment;
using AeroCode.AI.Capabilities;
using Xunit;

namespace AeroCode.Tests.MoaTests;

/// <summary>IVendorCapabilityProbe 测试替身：脚本化三态返回并记录调用（不含任何真实探测/密钥）。</summary>
internal sealed class EffortProbeStub : IVendorCapabilityProbe
{
    private readonly Func<string, VendorCapability, VendorCapabilityState> _resolver;

    public EffortProbeStub(Func<string, VendorCapability, VendorCapabilityState> resolver) => _resolver = resolver;

    public List<(string ProviderId, VendorCapability Capability)> Calls { get; } = new();

    public Task<VendorCapabilityState> ProbeAsync(string providerId, VendorCapability capability, CancellationToken ct = default)
    {
        Calls.Add((providerId, capability));
        return Task.FromResult(_resolver(providerId, capability));
    }
}

/// <summary>
/// B4 effort 档位映射：探测未注入/Missing/未请求/无映射回落 Standard（fail-closed）；
/// Supported/Downgraded → Peak + 三厂商 token 映射；显式规则表优先于默认映射。
/// </summary>
public sealed class EffortProfileTests
{
    [Fact]
    public async Task DecideAsync_ProbeNotInjected_FallsBackToStandard()
    {
        var profile = new EffortProfile(); // null = 现行为

        var decision = await profile.DecideAsync("openai", peakRequested: true);

        Assert.Equal(EffortTier.Standard, decision.Tier);
        Assert.Null(decision.VendorToken);
        Assert.Contains("未注入", decision.Reason);
        Assert.False(string.IsNullOrWhiteSpace(decision.Reason));
    }

    [Fact]
    public async Task DecideAsync_ProbeMissing_FallsBackToStandard_FailClosed()
    {
        var probe = new EffortProbeStub((_, _) => VendorCapabilityState.Missing);
        var profile = new EffortProfile(probe);

        var decision = await profile.DecideAsync("openai", peakRequested: true);

        Assert.Equal(EffortTier.Standard, decision.Tier);
        Assert.Null(decision.VendorToken);
        Assert.Contains("Missing", decision.Reason);
        Assert.Single(probe.Calls); // 探测恰好一次
        Assert.Equal(("openai", VendorCapability.EffortTiers), probe.Calls[0]);
    }

    [Fact]
    public async Task DecideAsync_ProbeSupported_MapsVendorTokens()
    {
        var probe = new EffortProbeStub((_, _) => VendorCapabilityState.Supported);
        var profile = new EffortProfile(probe);

        var openai = await profile.DecideAsync("openai", peakRequested: true);
        Assert.Equal(EffortTier.Peak, openai.Tier);
        Assert.Equal("xhigh", openai.VendorToken);

        var anthropic = await profile.DecideAsync("anthropic", peakRequested: true);
        Assert.Equal(EffortTier.Peak, anthropic.Tier);
        Assert.Equal("extended-thinking", anthropic.VendorToken);

        var gemini = await profile.DecideAsync("gemini", peakRequested: true);
        Assert.Equal(EffortTier.Peak, gemini.Tier);
        Assert.Equal("deep-think", gemini.VendorToken);

        Assert.False(string.IsNullOrWhiteSpace(openai.Reason));
        Assert.Contains("Supported", openai.Reason);
    }

    [Fact]
    public async Task DecideAsync_ProbeDowngraded_DocumentedDegradedPath_StillPeak()
    {
        var probe = new EffortProbeStub((_, _) => VendorCapabilityState.Downgraded);
        var profile = new EffortProfile(probe);

        var decision = await profile.DecideAsync("claude", peakRequested: true);

        Assert.Equal(EffortTier.Peak, decision.Tier);
        Assert.Equal("extended-thinking", decision.VendorToken);
        Assert.Contains("Downgraded", decision.Reason);
    }

    [Fact]
    public async Task DecideAsync_PeakNotRequested_KeepsStandard_WithoutProbing()
    {
        var probe = new EffortProbeStub((_, _) => VendorCapabilityState.Supported);
        var profile = new EffortProfile(probe);

        var decision = await profile.DecideAsync("openai", peakRequested: false);

        Assert.Equal(EffortTier.Standard, decision.Tier);
        Assert.Null(decision.VendorToken);
        Assert.Empty(probe.Calls); // 未请求峰值就不探测
    }

    [Fact]
    public async Task DecideAsync_UnknownProviderFamily_FallsBackToStandard()
    {
        var probe = new EffortProbeStub((_, _) => VendorCapabilityState.Supported);
        var profile = new EffortProfile(probe);

        var decision = await profile.DecideAsync("deepseek", peakRequested: true);

        Assert.Equal(EffortTier.Standard, decision.Tier);
        Assert.Null(decision.VendorToken);
        Assert.Contains("映射", decision.Reason);
        Assert.Empty(probe.Calls); // 无映射不浪费探测
    }

    [Fact]
    public async Task DecideAsync_ExplicitRules_OverrideDefaultMapping()
    {
        var probe = new EffortProbeStub((_, _) => VendorCapabilityState.Supported);
        var profile = new EffortProfile(probe, new[]
        {
            new EffortTokenRule { Family = "acme", PeakToken = "ultra-think" },
        });

        var custom = await profile.DecideAsync("acme-1", peakRequested: true);
        Assert.Equal(EffortTier.Peak, custom.Tier);
        Assert.Equal("ultra-think", custom.VendorToken);

        // 显式规则表整体替换默认表：openai 不再有映射 → Standard
        var replaced = await profile.DecideAsync("openai", peakRequested: true);
        Assert.Equal(EffortTier.Standard, replaced.Tier);
    }

    [Fact]
    public async Task DecideAsync_EmptyProviderId_FallsBackToStandard()
    {
        var profile = new EffortProfile();

        var decision = await profile.DecideAsync(string.Empty, peakRequested: true);

        Assert.Equal(EffortTier.Standard, decision.Tier);
        Assert.False(string.IsNullOrWhiteSpace(decision.Reason));
    }

    [Fact]
    public void PeakTokenFor_DefaultTable_MatchesResearchMapping()
    {
        var profile = new EffortProfile();

        Assert.Equal("xhigh", profile.PeakTokenFor("openai"));
        Assert.Equal("xhigh", profile.PeakTokenFor("OpenAI-Compatible")); // 大小写不敏感
        Assert.Equal("extended-thinking", profile.PeakTokenFor("anthropic"));
        Assert.Equal("extended-thinking", profile.PeakTokenFor("claude"));
        Assert.Equal("deep-think", profile.PeakTokenFor("gemini"));
        Assert.Null(profile.PeakTokenFor("deepseek"));
    }
}
