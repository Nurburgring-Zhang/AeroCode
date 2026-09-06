// Copyright (c) AeroCode
// R1 缝合窗口（#13）设置层测试：budget / loopGuard / curation 三节点与 subagent.parallelEnabled
// 的默认值必须等于基线行为（能力全关 / 并行保持 true），翻转只经显式配置发生。
using System.Text.Json;
using AeroCode.App.Configuration;
using Xunit;

namespace AeroCode.Tests.AppTests;

public sealed class StitchSettingsDefaultsTests
{
    [Fact]
    public void Defaults_MatchBaseline_AllStitchCapabilitiesDisabled()
    {
        var s = new AppSettings();
        Assert.False(s.Budget.Enabled);
        Assert.False(s.LoopGuard.Enabled);
        Assert.False(s.Curation.Enabled);
        Assert.True(s.Subagent.ParallelEnabled); // 并行开关默认 true = 现行为

        // R2 缝合节点默认值必须等于基线行为（LOW-D 钉住，防默认漂移）。
        Assert.False(s.CostTiers.Enabled);          // B1 四层成本排序默认关：选模走既有 Assign 路径
        Assert.False(s.CostTiers.PeakTierEnabled);  // ④峰值档输入默认关
        Assert.False(s.CostTiers.CacheBreakpointsEnabled); // 缓存断点默认关：请求体逐字节基线
        Assert.False(s.Effort.Enabled);             // effort 峰值档默认关：不注入 EffortProfile
        Assert.Equal("MarkOnly", s.Guardrail.Mode); // guardrail 默认只标记不拦截
        Assert.False(s.Critique.Enabled);           // C2 critique 默认关：不注入验证器
        Assert.False(s.Deprecation.Enabled);        // B6 弃用监控默认关：绝不外呼
    }

    [Fact]
    public void LegacySettingsJson_WithoutNewNodes_KeepsBaselineDefaults()
    {
        // 旧 settings.json（无 budget/loopGuard/curation 节）反序列化后仍为默认 = 基线行为。
        const string legacy = "{\"ai\":{\"defaultProviderId\":\"deepseek\"},\"ui\":{\"theme\":\"Dark\"}}";
        var s = JsonSerializer.Deserialize<AppSettings>(
            legacy, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(s);
        Assert.False(s!.Budget.Enabled);
        Assert.False(s.LoopGuard.Enabled);
        Assert.False(s.Curation.Enabled);
        Assert.True(s.Subagent.ParallelEnabled);

        // R2 缝合节点缺席 = 默认基线（LOW-D）。
        Assert.False(s.CostTiers.Enabled);
        Assert.False(s.CostTiers.PeakTierEnabled);
        Assert.False(s.CostTiers.CacheBreakpointsEnabled);
        Assert.False(s.Effort.Enabled);
        Assert.Equal("MarkOnly", s.Guardrail.Mode);
        Assert.False(s.Critique.Enabled);
        Assert.False(s.Deprecation.Enabled);
    }

    [Fact]
    public void ExplicitFlip_RoundTripsThroughJson()
    {
        var s = new AppSettings();
        s.Subagent.ParallelEnabled = false;
        s.Budget.Enabled = true;
        s.Budget.LimitTokens = 123_456;
        s.LoopGuard.Enabled = true;
        s.LoopGuard.OffGoalKeywords.Add("自定义偏离词");
        s.Curation.Enabled = true;
        s.Curation.WatermarkThresholdTokens = 4_000;

        var json = JsonSerializer.Serialize(s);
        var back = JsonSerializer.Deserialize<AppSettings>(
            json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(back);
        Assert.False(back!.Subagent.ParallelEnabled);
        Assert.True(back.Budget.Enabled);
        Assert.Equal(123_456, back.Budget.LimitTokens);
        Assert.True(back.LoopGuard.Enabled);
        Assert.Contains("自定义偏离词", back.LoopGuard.OffGoalKeywords);
        Assert.True(back.Curation.Enabled);
        Assert.Equal(4_000, back.Curation.WatermarkThresholdTokens);
    }
}
