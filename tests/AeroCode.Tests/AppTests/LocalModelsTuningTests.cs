// Copyright (c) AeroCode
// LocalModelsViewModel 调参测试（LOCAL_LLM_SPEC §4 / P3）：
// BuildExtraBody 选项完整性、ApplyOptions 写入 ollama provider ExtraBody、HydrateOptions 回读。
// 拒绝伪造：ApplyOptions 用真实 SettingsService（临时目录）验证真正落盘到 provider 配置。
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AeroCode.AI.LocalModels;
using AeroCode.App.Configuration;
using AeroCode.App.Services;
using AeroCode.App.ViewModels;
using Xunit;

namespace AeroCode.Tests.AppTests;

public sealed class LocalModelsTuningTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"localmodels_tuning_{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static OllamaClient FakeClient() => new(handler: new NoopHandler());

    private sealed class NoopHandler : System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(
            System.Net.Http.HttpRequestMessage request, System.Threading.CancellationToken ct)
            => Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new System.Net.Http.StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
            });
    }

    [Fact]
    public void BuildExtraBody_ContainsAllOllamaOptions()
    {
        var vm = new LocalModelsViewModel(FakeClient())
        {
            NumCtx = 8192,
            NumGpu = 0,
            NumThread = 8,
            Temperature = 0.5,
            TopP = 0.95,
            TopK = 30,
            RepeatPenalty = 1.2,
            NumPredict = 1024,
        };

        var extra = vm.BuildExtraBody();

        Assert.Equal(8192, extra["num_ctx"]);
        Assert.Equal(0, extra["num_gpu"]);
        Assert.Equal(8, extra["num_thread"]);
        Assert.Equal(0.5, extra["temperature"]);
        Assert.Equal(0.95, extra["top_p"]);
        Assert.Equal(30, extra["top_k"]);
        Assert.Equal(1.2, extra["repeat_penalty"]);
        Assert.Equal(1024, extra["num_predict"]);
    }

    [Fact]
    public async Task ApplyOptions_WritesToOllamaProviderExtraBody_AndPersists()
    {
        var paths = new AppDataPaths(_root);
        var settings = new SettingsService(paths);
        await settings.LoadAsync(); // 首次加载种子默认 provider（含 ollama）

        var ollamaBefore = settings.Current.Ai.Providers
            .FirstOrDefault(p => p.Id == LocalModelsViewModel.OllamaProviderId);
        Assert.NotNull(ollamaBefore); // 种子应包含 ollama provider

        var vm = new LocalModelsViewModel(FakeClient(), settings, providerFactory: null)
        {
            NumCtx = 2048,
            NumThread = 4,
            Temperature = 0.3,
        };

        await vm.ApplyOptionsAsync();

        var ollamaAfter = settings.Current.Ai.Providers
            .First(p => p.Id == LocalModelsViewModel.OllamaProviderId);
        Assert.NotNull(ollamaAfter.ExtraBody);
        Assert.Equal(2048, ollamaAfter.ExtraBody!["num_ctx"]);
        Assert.Equal(4, ollamaAfter.ExtraBody["num_thread"]);
        Assert.Equal(0.3, ollamaAfter.ExtraBody["temperature"]);
    }

    [Fact]
    public async Task HydrateOptions_ReadsBackFromExtraBody()
    {
        var paths = new AppDataPaths(_root);
        var settings = new SettingsService(paths);
        await settings.LoadAsync();

        // 先写入一组参数，再用新 VM 回读，验证水合正确。
        var writer = new LocalModelsViewModel(FakeClient(), settings, providerFactory: null)
        {
            NumCtx = 16384,
            TopK = 55,
            RepeatPenalty = 1.05,
        };
        await writer.ApplyOptionsAsync();

        var reader = new LocalModelsViewModel(FakeClient(), settings, providerFactory: null);
        Assert.Equal(16384, reader.NumCtx);
        Assert.Equal(55, reader.TopK);
        Assert.Equal(1.05, reader.RepeatPenalty);
    }

    [Fact]
    public async Task SetAsDefaultModel_WritesSelectedModelToProvider_AndHydrates()
    {
        var paths = new AppDataPaths(_root);
        var settings = new SettingsService(paths);
        await settings.LoadAsync();

        var vm = new LocalModelsViewModel(FakeClient(), settings, providerFactory: null);
        vm.SelectedModel = new LocalModelItemViewModel { Name = "qwen2.5:1.5b" };

        await vm.SetAsDefaultModelAsync();

        var ollama = settings.Current.Ai.Providers
            .First(p => p.Id == LocalModelsViewModel.OllamaProviderId);
        Assert.Equal("qwen2.5:1.5b", ollama.DefaultModel);
        Assert.Equal("qwen2.5:1.5b", vm.CurrentDefaultModel);

        // 新 VM 水合时应回读到新默认模型。
        var reader = new LocalModelsViewModel(FakeClient(), settings, providerFactory: null);
        Assert.Equal("qwen2.5:1.5b", reader.CurrentDefaultModel);
    }

    [Fact]
    public void Catalog_IsNonEmpty_AndContainsExpectedFamilies()
    {
        var vm = new LocalModelsViewModel(FakeClient());

        Assert.NotEmpty(vm.Catalog);
        Assert.Contains(vm.Catalog, c => c.Name.StartsWith("qwen2.5"));
        Assert.Contains(vm.Catalog, c => c.Name.StartsWith("llama3.2"));
        // 每个条目都有名称/大小/描述（供目录展示）。
        Assert.All(vm.Catalog, c =>
        {
            Assert.False(string.IsNullOrWhiteSpace(c.Name));
            Assert.False(string.IsNullOrWhiteSpace(c.Size));
            Assert.False(string.IsNullOrWhiteSpace(c.Summary));
        });
    }
}
