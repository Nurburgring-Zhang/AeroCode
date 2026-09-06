// Copyright (c) AeroCode
// R3-δ 契约 D-MATRIX 代管字段测试：android.foregroundService / sandbox.enforce / deprecation.monitor
// 三个设置字段的节名、字段名与默认值契约钉死（默认 false = 现行为），翻转只经显式配置发生。
// 模式仿 StitchSettingsDefaultsTests（默认值 + legacy JSON 缺席 + 显式翻转 roundtrip）。
using System.Text.Json;
using AeroCode.App.Configuration;
using Xunit;

namespace AeroCode.Tests.AppTests;

public sealed class R3SettingsFieldsTests
{
    [Fact]
    public void Defaults_AllThreeContractFields_False()
    {
        var s = new AppSettings();
        Assert.False(s.Android.ForegroundService); // android.foregroundService 默认 false（α 消费）
        Assert.False(s.Sandbox.Enforce);           // sandbox.enforce 默认 false（γ 消费）
        Assert.False(s.Deprecation.Monitor);       // deprecation.monitor 默认 false（网关执行路径门控）
    }

    [Fact]
    public void LegacySettingsJson_WithoutNewFields_KeepsBaselineDefaults()
    {
        // 旧 settings.json（无 android/sandbox 节、deprecation 无 monitor 字段）→ 反序列化后仍为默认。
        const string legacy = "{\"ai\":{\"defaultProviderId\":\"deepseek\"},\"deprecation\":{\"enabled\":true}}";
        var s = JsonSerializer.Deserialize<AppSettings>(
            legacy, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(s);
        Assert.False(s!.Android.ForegroundService);
        Assert.False(s.Sandbox.Enforce);
        Assert.False(s.Deprecation.Monitor);
        Assert.True(s.Deprecation.Enabled); // 旧字段语义不受影响
    }

    [Fact]
    public void ContractNames_PinnedInSerializedJson_CamelCasePaths()
    {
        // 契约钉死：JSON 路径逐字节为 android.foregroundService / sandbox.enforce / deprecation.monitor。
        var s = new AppSettings
        {
            Android = new AndroidSettings { ForegroundService = true },
            Sandbox = new SandboxSettings { Enforce = true },
            Deprecation = new DeprecationSettings { Monitor = true },
        };

        var json = JsonSerializer.Serialize(s, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        Assert.Contains("\"android\":{\"foregroundService\":true}", json);
        Assert.Contains("\"sandbox\":{\"enforce\":true}", json);
        Assert.Contains("\"deprecation\":{\"enabled\":false,\"urlAllowlist\":[],\"monitor\":true}", json);
    }

    [Fact]
    public void ExplicitFlip_RoundTripsThroughJson()
    {
        var s = new AppSettings();
        s.Android.ForegroundService = true;
        s.Sandbox.Enforce = true;
        s.Deprecation.Monitor = true;

        var json = JsonSerializer.Serialize(s);
        var back = JsonSerializer.Deserialize<AppSettings>(
            json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(back);
        Assert.True(back!.Android.ForegroundService);
        Assert.True(back.Sandbox.Enforce);
        Assert.True(back.Deprecation.Monitor);
    }

    [Fact]
    public void CopyContract_Note_ProviderConfigCopyUnaffected()
    {
        // Copy() 契约说明（R2 教训的边界）：SettingsService.Copy 只深拷贝 ProviderConfig；
        // 三个代管字段位于 AppSettings 顶层节，序列化整体携带、无手工拷贝路径可漏。
        // 本测试钉住：它们不出现在 ProviderConfig，也不经 ToAiOptions 快照（避免误挂到 provider 上）。
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"r3settings_{Guid.NewGuid():N}");
        try
        {
            var svc = new SettingsService(new AeroCode.App.Services.AppDataPaths(root));
            svc.Current.Android.ForegroundService = true;
            svc.Current.Sandbox.Enforce = true;
            svc.Current.Deprecation.Monitor = true;

            var aiOptions = svc.ToAiOptions();

            Assert.Single(aiOptions.Providers); // 默认兜底 deepseek
            Assert.DoesNotContain(aiOptions.Providers, p =>
                p.Id == "android" || p.Id == "sandbox" || p.Id == "deprecation");
        }
        finally
        {
            if (System.IO.Directory.Exists(root)) System.IO.Directory.Delete(root, recursive: true);
        }
    }
}
