using System;
using System.Linq;
using AeroCode.AI.Configuration;
using AeroCode.AI.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AeroCode.Tests.ServiceTests;

/// <summary>
/// Provider 热重载（Phase 3 S1）：设置变更后 Reload 换掉底层 provider 集合——
/// 缓存清掉（同 Id 也按新配置重建）、默认 provider 切换、变更事件发出，
/// 订阅方（画像目录/编排层/UI）据此响应。
/// </summary>
public sealed class ProviderFactoryReloadTests
{
    private const string TestKeyEnvVar = "AERO_RELOAD_TEST_KEY";

    private static ProviderConfig Config(string id, string model) => new()
    {
        Id = id,
        DisplayName = id,
        Kind = "OpenAICompatible",
        BaseUrl = $"https://example.invalid/{id}/v1",
        DefaultModel = model,
        ApiKeyEnvVar = TestKeyEnvVar,
    };

    private static AIOptions OptionsA() => new()
    {
        DefaultProviderId = "deepseek",
        Providers = new() { Config("deepseek", "model-v1") },
    };

    private static AIOptions OptionsB() => new()
    {
        DefaultProviderId = "kimi",
        Providers = new() { Config("kimi", "moonshot-v1"), Config("deepseek", "model-v2") },
    };

    [Fact]
    public void Reload_SwapsConfiguration_ClearsCache_FiresEvent()
    {
        Environment.SetEnvironmentVariable(TestKeyEnvVar, "sk-test-reload");
        try
        {
            var factory = new ProviderFactory(OptionsA(), NullLoggerFactory.Instance);
            var before = factory.Get("deepseek");
            Assert.Equal("deepseek", factory.DefaultProviderId);
            Assert.Equal("model-v1", factory.TryGetConfig("deepseek", out var cfgBefore) ? cfgBefore.DefaultModel : null);

            var fired = 0;
            factory.ProvidersChanged += () => fired++;

            factory.Reload(OptionsB());

            Assert.Equal(1, fired);
            Assert.Equal("kimi", factory.DefaultProviderId);
            Assert.Equal(2, factory.ListConfiguredIds().Count());
            Assert.Equal("model-v2", factory.TryGetConfig("deepseek", out var cfgAfter) ? cfgAfter.DefaultModel : null);

            // 缓存已清：同 Id 也是按新配置重建的新实例
            var after = factory.Get("deepseek");
            Assert.False(ReferenceEquals(before, after));

            // 默认 provider 跟随新配置
            Assert.Equal("kimi", factory.GetDefault().ProviderId);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TestKeyEnvVar, null);
        }
    }

    [Fact]
    public void Reload_EventFiredOutsideLock_SubscriberCanCallBackIntoFactory()
    {
        // 订阅方在事件里回查工厂不能死锁（事件必须在锁外触发）。
        Environment.SetEnvironmentVariable(TestKeyEnvVar, "sk-test-reload");
        try
        {
            var factory = new ProviderFactory(OptionsA(), NullLoggerFactory.Instance);
            string? seenDefault = null;
            factory.ProvidersChanged += () => seenDefault = factory.DefaultProviderId;

            factory.Reload(OptionsB());

            Assert.Equal("kimi", seenDefault);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TestKeyEnvVar, null);
        }
    }

    [Fact]
    public void Reload_NullOptions_Throws()
    {
        var factory = new ProviderFactory(OptionsA(), NullLoggerFactory.Instance);
        Assert.Throws<ArgumentNullException>(() => factory.Reload(null!));
    }

    [Fact]
    public void Get_UnconfiguredProvider_Throws()
    {
        var factory = new ProviderFactory(OptionsA(), NullLoggerFactory.Instance);
        var ex = Assert.Throws<InvalidOperationException>(() => factory.Get("ghost"));
        Assert.Contains("ghost", ex.Message);
    }

    [Fact]
    public void CreateProbe_ReturnsFreshInstances_DoesNotPolluteCacheOrConfig()
    {
        Environment.SetEnvironmentVariable(TestKeyEnvVar, "sk-test-probe");
        try
        {
            var factory = new ProviderFactory(OptionsA(), NullLoggerFactory.Instance);
            var cached = factory.Get("deepseek");

            // 探针配置 = 编辑中/未保存的配置（与已加载配置不同的 endpoint 与模型）
            var probeConfig = Config("deepseek", "probe-model-unreleased");
            probeConfig.BaseUrl = "https://probe.invalid/v1";

            var probe1 = factory.CreateProbe(probeConfig);
            var probe2 = factory.CreateProbe(probeConfig);

            Assert.NotNull(probe1);
            Assert.False(ReferenceEquals(probe1, probe2)); // 每次新建，不缓存
            Assert.False(ReferenceEquals(cached, probe1)); // 与缓存实例隔离

            // 探针不改变已加载配置与缓存：Get 仍返回旧缓存实例、配置仍是原值
            Assert.True(ReferenceEquals(cached, factory.Get("deepseek")));
            Assert.Equal("model-v1", factory.TryGetConfig("deepseek", out var cfg) ? cfg.DefaultModel : null);
            Assert.Equal($"https://example.invalid/deepseek/v1", cfg!.BaseUrl);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TestKeyEnvVar, null);
        }
    }

    [Fact]
    public void CreateProbe_NullConfig_Throws()
    {
        var factory = new ProviderFactory(OptionsA(), NullLoggerFactory.Instance);
        Assert.Throws<ArgumentNullException>(() => factory.CreateProbe(null!));
    }
}
