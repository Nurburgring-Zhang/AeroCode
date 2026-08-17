// Copyright (c) AeroCode V3.0
// SettingsViewModel S8 CRUD tests — provider 编辑补全（Supports*/ExtraHeaders/ExtraBody 校验）、
// 单点连通性测试（真实本地 HTTP 服务）、模型画像增改删 + moa-profiles.json 落盘断言、
// 保存 → ProviderFactory.Reload 热重载事件全链。全部走真实 SettingsService/工厂/目录，零桩数据。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AeroAgent.Moa.Profiles;
using AeroCode.App.Configuration;
using AeroCode.App.Services;
using AeroCode.App.ViewModels;
using AeroCode.Harness.EventBus;
using AeroCode.Harness.Permission;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AeroCode.Tests.AppTests;

/// <summary>
/// 本地回环 OpenAI 兼容补全服务（真实 HttpListener）：
/// 对任意请求返回最小合法补全响应——探针 HealthCheck 的绿路径端到端验证。
/// </summary>
internal sealed class LocalCompletionServer : IDisposable
{
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _cts = new();

    public string BaseUrl { get; }

    private int _requestsSeen;

    /// <summary>服务实际收到的请求数（验证探针真的发出了 HTTP 流量）。</summary>
    public int RequestsSeen => _requestsSeen;

    public LocalCompletionServer()
    {
        var port = GetFreePort();
        BaseUrl = $"http://127.0.0.1:{port}/v1";
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        _ = AcceptLoopAsync();
    }

    private static int GetFreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private async Task AcceptLoopAsync()
    {
        const string responseJson =
            "{\"id\":\"probe-1\",\"model\":\"probe-model\"," +
            "\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"pong\"},\"finish_reason\":\"stop\"}]," +
            "\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1,\"total_tokens\":2}}";
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync();
            }
            catch (Exception)
            {
                return; // listener 已停止
            }

            try
            {
                Interlocked.Increment(ref _requestsSeen);
                using (var reader = new StreamReader(ctx.Request.InputStream))
                {
                    await reader.ReadToEndAsync();
                }

                var bytes = Encoding.UTF8.GetBytes(responseJson);
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
            catch (Exception)
            {
                // 单个请求失败不影响服务循环
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch (Exception)
        {
            // 关闭竞态可忽略
        }
    }
}

/// <summary>S8：Provider/画像全 CRUD + 连通性测试 + 热重载链的行为验证。</summary>
public sealed class SettingsViewModelCrudTests : IDisposable
{
    private readonly string _root;
    private readonly AppDataPaths _paths;
    private readonly SettingsService _settings;
    private readonly AeroCode.AI.Providers.ProviderFactory _factory;
    private readonly ModelProfileCatalog _catalog;
    private readonly SettingsViewModel _vm;

    public SettingsViewModelCrudTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"settings_crud_{Guid.NewGuid():N}");
        _paths = new AppDataPaths(_root);
        _settings = new SettingsService(_paths);
        _settings.LoadAsync().GetAwaiter().GetResult();

        _factory = new AeroCode.AI.Providers.ProviderFactory(
            _settings.ToAiOptions(), NullLoggerFactory.Instance);
        var policy = PermissionPolicy.CreateDefault(new EventBus());
        var permStore = new JsonPermissionStore(_paths.PermissionsFile);
        _catalog = new ModelProfileCatalog(new JsonFileProfileStore(_paths.MoaProfilesFile));
        _catalog.LoadAsync(BuiltInProfiles.Seed()).GetAwaiter().GetResult();
        var moaOptions = new AeroAgent.Moa.Strategies.MoaOptions();
        var moaStore = new AeroAgent.Moa.Strategies.JsonMoaOptionsStore(_paths.MoaOptionsFile);
        _vm = new SettingsViewModel(
            _settings, new ThemeService(), _factory, policy, permStore, _catalog,
            moaOptions, moaStore, NullLogger<SettingsViewModel>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    // ────────────────────────── ExtraHeaders/ExtraBody 解析 ──────────────────────────

    [Fact]
    public void TryParseExtraHeaders_Variants()
    {
        Assert.True(SettingsViewModel.TryParseExtraHeaders("", out var empty, out _));
        Assert.Null(empty);

        Assert.True(SettingsViewModel.TryParseExtraHeaders("{\"X-A\":\"1\",\"X-B\":\"2\"}", out var ok, out _));
        Assert.Equal("1", ok!["X-A"]);
        Assert.Equal("2", ok["X-B"]);

        Assert.False(SettingsViewModel.TryParseExtraHeaders("{bad", out _, out var badErr));
        Assert.Contains("JSON 非法", badErr);

        Assert.False(SettingsViewModel.TryParseExtraHeaders("[1,2]", out _, out var arrErr));
        Assert.NotNull(arrErr); // 数组根 → 反序列化失败或结构不符

        Assert.False(SettingsViewModel.TryParseExtraHeaders("{\"\":\"v\"}", out _, out var keyErr));
        Assert.Contains("名不能为空", keyErr);

        Assert.False(SettingsViewModel.TryParseExtraHeaders("{\"X-A\":null}", out _, out var nullErr));
        Assert.Contains("null", nullErr);
    }

    [Fact]
    public void TryParseExtraBody_Variants()
    {
        Assert.True(SettingsViewModel.TryParseExtraBody("  ", out var empty, out _));
        Assert.Null(empty);

        Assert.True(SettingsViewModel.TryParseExtraBody(
            "{\"reasoning_split\":true,\"top_k\":5,\"note\":\"x\",\"nested\":{\"a\":1}}",
            out var ok, out _));
        Assert.Equal(4, ok!.Count);

        Assert.False(SettingsViewModel.TryParseExtraBody("not json", out _, out var err));
        Assert.Contains("JSON 非法", err);

        Assert.False(SettingsViewModel.TryParseExtraBody("{\"\":1}", out _, out var keyErr));
        Assert.Contains("字段名不能为空", keyErr);
    }

    // ────────────────────────── 切换 Provider 的提交语义 ──────────────────────────

    [Fact]
    public void SwitchProvider_CommitsValidExtraTexts_IntoConfig()
    {
        var deepseek = _vm.SelectedProvider!; // 默认选中 deepseek
        Assert.Equal("deepseek", deepseek.Id);

        _vm.ExtraHeadersJson = "{\"X-A\":\"1\"}";
        _vm.ExtraBodyJson = "{\"reasoning_split\":true}";

        _vm.SelectProviderCommand.Execute("qwen"); // 切走即提交

        Assert.NotNull(deepseek.ExtraHeaders);
        Assert.Equal("1", deepseek.ExtraHeaders!["X-A"]);
        Assert.NotNull(deepseek.ExtraBody);
        Assert.True(deepseek.ExtraBody!.ContainsKey("reasoning_split"));
        // qwen 无 Extra*：编辑框载入空文本
        Assert.Equal(string.Empty, _vm.ExtraHeadersJson);
        Assert.Equal(string.Empty, _vm.ExtraBodyJson);
    }

    [Fact]
    public async Task InvalidExtraJson_PreservedAcrossSwitch_AndBlocksSave()
    {
        var deepseek = _vm.SelectedProvider!;
        var settingsBefore = await File.ReadAllTextAsync(_paths.SettingsFile);

        _vm.ExtraHeadersJson = "{bad json";
        _vm.SelectProviderCommand.Execute("qwen");

        // 提交失败：config 不被污染
        Assert.Null(deepseek.ExtraHeaders);

        // 切回：用户原文还在（不丢输入）
        _vm.SelectProviderCommand.Execute("deepseek");
        Assert.Equal("{bad json", _vm.ExtraHeadersJson);

        // Save 整体阻止：磁盘 settings.json 一字不动
        await _vm.SaveCommand.ExecuteAsync(null);
        Assert.StartsWith("❌", _vm.StatusText);
        Assert.Contains("deepseek", _vm.StatusText);
        Assert.Equal(settingsBefore, await File.ReadAllTextAsync(_paths.SettingsFile));
    }

    [Fact]
    public async Task Save_PersistsExtraHeadersAndBody_ToSettingsJson_AndRoundTrips()
    {
        _vm.ExtraHeadersJson = "{\"X-Custom\":\"v1\"}";
        _vm.ExtraBodyJson = "{\"reasoning_split\":true,\"top_k\":3}";

        await _vm.SaveCommand.ExecuteAsync(null);
        Assert.StartsWith("✅", _vm.StatusText);

        var json = await File.ReadAllTextAsync(_paths.SettingsFile);
        using (var doc = JsonDocument.Parse(json))
        {
            var prov = doc.RootElement.GetProperty("ai").GetProperty("providers").EnumerateArray()
                .First(e => e.GetProperty("id").GetString() == "deepseek");
            Assert.Equal("v1", prov.GetProperty("extraHeaders").GetProperty("X-Custom").GetString());
            Assert.True(prov.GetProperty("extraBody").GetProperty("reasoning_split").GetBoolean());
            Assert.Equal(3, prov.GetProperty("extraBody").GetProperty("top_k").GetInt32());
        }

        // Reload 回读进 config 并重新载入编辑框
        await _vm.ReloadCommand.ExecuteAsync(null);
        var ds = _vm.Providers.First(p => p.Id == "deepseek");
        Assert.Equal("v1", ds.ExtraHeaders!["X-Custom"]);
        Assert.Contains("reasoning_split", _vm.ExtraBodyJson);
    }

    [Fact]
    public async Task SettingsService_RoundTrip_PreservesAllProviderFields()
    {
        // 回归：写 camelCase / 读大小写不敏感——重载后 provider 字段一个不丢。
        var p = _settings.Current.Ai.Providers.First(x => x.Id == "deepseek");
        p.SupportsThinking = false;
        p.ThinkingEfforts = "low,max";
        p.TimeoutSeconds = 77;
        p.ExtraHeaders = new Dictionary<string, string> { ["X-Rt"] = "1" };
        p.ExtraBody = new Dictionary<string, object> { ["flag"] = true };
        await _settings.SaveAsync();

        var fresh = new SettingsService(_paths);
        await fresh.LoadAsync();

        var loaded = fresh.Current.Ai.Providers.First(x => x.Id == "deepseek");
        Assert.Equal("DeepSeek V4 (default)", loaded.DisplayName);
        Assert.Equal("https://api.deepseek.com/v1", loaded.BaseUrl);
        Assert.False(loaded.SupportsThinking);
        Assert.True(loaded.SupportsStreaming); // 默认值未被错误覆盖
        Assert.Equal("low,max", loaded.ThinkingEfforts);
        Assert.Equal(77, loaded.TimeoutSeconds);
        Assert.Equal("1", loaded.ExtraHeaders!["X-Rt"]);
        Assert.True(loaded.ExtraBody!.ContainsKey("flag"));
    }

    [Fact]
    public async Task Save_NormalizesSupportsFlagsAndThinkingEfforts()
    {
        var p = _vm.SelectedProvider!;
        p.SupportsStreaming = false;
        p.SupportsToolCalling = false;
        p.SupportsThinking = true;
        p.ThinkingEfforts = "low,high";

        await _vm.SaveCommand.ExecuteAsync(null);
        Assert.StartsWith("✅", _vm.StatusText);

        var saved = _settings.Current.Ai.Providers.First(x => x.Id == p.Id);
        Assert.False(saved.SupportsStreaming);
        Assert.False(saved.SupportsToolCalling);
        Assert.True(saved.SupportsThinking);
        Assert.Equal("low,high", saved.ThinkingEfforts);
    }

    // ────────────────────────── 单点连通性测试 ──────────────────────────

    [Fact]
    public async Task TestSelectedProvider_RealLocalEndpoint_ReportsGreen()
    {
        using var server = new LocalCompletionServer();
        var p = _vm.SelectedProvider!;
        p.BaseUrl = server.BaseUrl;
        p.DefaultModel = "probe-model";
        p.RequiresApiKey = false;

        await _vm.TestSelectedProviderCommand.ExecuteAsync(null);

        Assert.StartsWith("🟢", _vm.StatusText);
        Assert.Contains("连通正常", _vm.StatusText);
        Assert.True(server.RequestsSeen >= 1); // 真实 HTTP 请求到达了本地服务
    }

    [Fact]
    public async Task TestSelectedProvider_UnreachableEndpoint_ReportsRedHonest()
    {
        var p = _vm.SelectedProvider!;
        p.BaseUrl = "http://127.0.0.1:9/v1"; // discard 端口：连接必被拒
        p.RequiresApiKey = false;

        await _vm.TestSelectedProviderCommand.ExecuteAsync(null);

        Assert.StartsWith("🔴", _vm.StatusText);
        Assert.Contains("连通失败", _vm.StatusText);
    }

    [Fact]
    public async Task TestSelectedProvider_InvalidExtraJson_RefusesToProbe()
    {
        _vm.ExtraHeadersJson = "{oops";
        await _vm.TestSelectedProviderCommand.ExecuteAsync(null);
        Assert.StartsWith("❌", _vm.StatusText);
    }

    // ────────────────────────── 画像 CRUD + 落盘 ──────────────────────────

    [Fact]
    public void HydrateProfiles_ListsSeedProfiles_StatsReadOnlyText()
    {
        Assert.Equal(BuiltInProfiles.Seed().Count, _vm.ProfileEditors.Count);
        var deepseek = _vm.ProfileEditors.Single(e => e.ProviderId == "deepseek");
        Assert.Equal("暂无调用记录", deepseek.StatsText);
        // 种子强项如实呈现
        Assert.Contains(deepseek.Strengths, t => t.Name == ModelStrength.Code && t.IsChecked);
        Assert.Contains(deepseek.Strengths, t => t.Name == ModelStrength.Writing && !t.IsChecked);
    }

    [Fact]
    public async Task Save_UpsertsEditedProfile_AndPersistsToDisk()
    {
        var item = _vm.ProfileEditors.Single(e => e.ProviderId == "deepseek" && e.ModelId == "");
        foreach (var t in item.Strengths)
        {
            t.IsChecked = t.Name == ModelStrength.Translation;
        }
        item.ContextWindow = 123456;
        item.MaxOutputTokens = 8192;
        item.CostInText = "0.27";
        item.CostOutText = "1.1";
        item.Speed = ProfileEditorItem.AllSpeeds.Single(c => c.Value == SpeedTier.Fast);

        await _vm.SaveCommand.ExecuteAsync(null);
        Assert.StartsWith("✅", _vm.StatusText);

        var live = _catalog.Find("deepseek", "");
        Assert.NotNull(live);
        Assert.Equal(new[] { ModelStrength.Translation }, live!.Strengths);
        Assert.Equal(123456, live.ContextWindow);
        Assert.Equal(8192, live.MaxOutputTokens);
        Assert.Equal(0.27, live.CostPerMIn);
        Assert.Equal(1.1, live.CostPerMOut);
        Assert.Equal(SpeedTier.Fast, live.SpeedTier);

        // 计划要求：画像保存后 catalog 落盘断言
        var diskJson = await File.ReadAllTextAsync(_paths.MoaProfilesFile);
        using var doc = JsonDocument.Parse(diskJson);
        var diskProfile = doc.RootElement.GetProperty("Profiles").EnumerateArray()
            .First(e => e.GetProperty("ProviderId").GetString() == "deepseek"
                        && e.GetProperty("ModelId").GetString() == "");
        Assert.Equal(123456, diskProfile.GetProperty("ContextWindow").GetInt32());
        Assert.Contains(diskProfile.GetProperty("Strengths").EnumerateArray(),
            e => e.GetString() == ModelStrength.Translation);
        Assert.Equal(0.27, diskProfile.GetProperty("CostPerMIn").GetDouble());
    }

    [Fact]
    public async Task AddProfile_ThenSave_PersistsNewRow()
    {
        _vm.AddProfileCommand.Execute(null);
        var added = _vm.SelectedProfile!;
        added.ProviderId = "custom-probe";
        added.ModelId = "m1";
        added.ContextWindow = 4096;
        added.CostInText = "0.5";
        foreach (var t in added.Strengths)
        {
            t.IsChecked = t.Name == ModelStrength.Code;
        }

        await _vm.SaveCommand.ExecuteAsync(null);
        Assert.StartsWith("✅", _vm.StatusText);

        var live = _catalog.Find("custom-probe", "m1");
        Assert.NotNull(live);
        Assert.Equal(new[] { ModelStrength.Code }, live!.Strengths);
        Assert.Equal(0.5, live.CostPerMIn);
        Assert.Equal(4096, live.ContextWindow);
    }

    [Fact]
    public async Task RemoveProfile_ThenSave_DeletesFromCatalogAndDisk_ButKeepsRuntimeAdded()
    {
        var minimax = _vm.ProfileEditors.Single(e => e.ProviderId == "minimax");
        _vm.SelectedProfile = minimax;
        _vm.RemoveSelectedProfileCommand.Execute(null);
        Assert.DoesNotContain(_vm.ProfileEditors, e => e.ProviderId == "minimax");

        // 窗口打开期间运行时自学习新增的画像：Save 不得误删
        _catalog.RecordUsage("runtime-new", "m-x", 150, failed: false);

        await _vm.SaveCommand.ExecuteAsync(null);
        Assert.StartsWith("✅", _vm.StatusText);

        Assert.Null(_catalog.Find("minimax", ""));
        Assert.NotNull(_catalog.Find("runtime-new", "m-x"));

        var diskJson = await File.ReadAllTextAsync(_paths.MoaProfilesFile);
        using var doc = JsonDocument.Parse(diskJson);
        Assert.DoesNotContain(doc.RootElement.GetProperty("Profiles").EnumerateArray(),
            e => e.GetProperty("ProviderId").GetString() == "minimax");
        Assert.Contains(doc.RootElement.GetProperty("Profiles").EnumerateArray(),
            e => e.GetProperty("ProviderId").GetString() == "runtime-new");
    }

    [Fact]
    public async Task Save_InvalidCost_BlocksEntirely_NothingWritten()
    {
        var settingsBefore = await File.ReadAllTextAsync(_paths.SettingsFile);
        var item = _vm.ProfileEditors.First(e => e.ProviderId == "deepseek");
        item.CostInText = "abc";

        await _vm.SaveCommand.ExecuteAsync(null);

        Assert.StartsWith("❌", _vm.StatusText);
        Assert.Contains("输入成本非法", _vm.StatusText);
        Assert.Equal(settingsBefore, await File.ReadAllTextAsync(_paths.SettingsFile));
        Assert.False(File.Exists(_paths.MoaProfilesFile)); // 画像文件从未写出
    }

    [Fact]
    public async Task Save_DuplicateProfileKeys_Blocks()
    {
        _vm.AddProfileCommand.Execute(null);
        _vm.SelectedProfile!.ProviderId = "deepseek";
        _vm.SelectedProfile.ModelId = ""; // 与既有 deepseek 默认画像同键

        await _vm.SaveCommand.ExecuteAsync(null);

        Assert.StartsWith("❌", _vm.StatusText);
        Assert.Contains("重复", _vm.StatusText);
    }

    [Fact]
    public async Task Save_DuplicateProviderId_BlocksEntirely()
    {
        // 校验门 0（Reviewer-B P2-4）：重复 Id 会让工厂检索与 MOA 角色绑定语义含混
        var settingsBefore = await File.ReadAllTextAsync(_paths.SettingsFile);
        _vm.Providers[1].Id = _vm.Providers[0].Id;

        await _vm.SaveCommand.ExecuteAsync(null);

        Assert.StartsWith("❌", _vm.StatusText);
        Assert.Contains("Id 重复", _vm.StatusText);
        Assert.Equal(settingsBefore, await File.ReadAllTextAsync(_paths.SettingsFile));
    }

    [Fact]
    public async Task Save_EmptyProviderId_BlocksEntirely()
    {
        var settingsBefore = await File.ReadAllTextAsync(_paths.SettingsFile);
        _vm.Providers[0].Id = "   ";

        await _vm.SaveCommand.ExecuteAsync(null);

        Assert.StartsWith("❌", _vm.StatusText);
        Assert.Contains("Id 不能为空", _vm.StatusText);
        Assert.Equal(settingsBefore, await File.ReadAllTextAsync(_paths.SettingsFile));
    }

    [Fact]
    public async Task SettingsService_CorruptFile_FallsBackToDefaults_NotThrow()
    {
        // 内容损坏如实降级默认（窄 catch 语义回归）：不抛、不是空白，
        // 默认四 provider 在位，用户打开设置页仍可正常编辑并覆盖修复。
        await File.WriteAllTextAsync(_paths.SettingsFile, "{这不是合法 JSON");

        var service = new SettingsService(_paths);
        await service.LoadAsync();

        Assert.Equal(4, service.Current.Ai.Providers.Count);
        Assert.Contains(service.Current.Ai.Providers, p => p.Id == "deepseek");
    }

    [Fact]
    public async Task SettingsService_SaveAtomic_NoLeftoverTmpFiles()
    {
        // 原子写回归：随机 tmp + Move 覆盖，目录内不留任何 .tmp 残骸
        _settings.Current.Ui.FontSize = 16;
        await _settings.SaveAsync();
        await _settings.SaveAsync(); // 连续两次：随机名互不覆盖在途文件

        var dir = Path.GetDirectoryName(_paths.SettingsFile)!;
        Assert.DoesNotContain(Directory.EnumerateFiles(dir), f => f.EndsWith(".tmp"));
        var reloaded = new SettingsService(_paths);
        await reloaded.LoadAsync();
        Assert.Equal(16, reloaded.Current.Ui.FontSize);
    }

    [Fact]
    public async Task Save_PreservesRuntimeStats_RecordedWhileWindowOpen()
    {
        var item = _vm.ProfileEditors.Single(e => e.ProviderId == "qwen" && e.ModelId == "");
        // 水合后运行时又记了一笔调用——UI 保存不得擦掉
        _catalog.RecordUsage("qwen", "", 250, failed: false);
        item.ContextWindow = 4096;

        await _vm.SaveCommand.ExecuteAsync(null);
        Assert.StartsWith("✅", _vm.StatusText);

        var live = _catalog.Find("qwen", "");
        Assert.NotNull(live);
        Assert.Equal(4096, live!.ContextWindow);       // UI 编辑生效
        Assert.Equal(1, live.Stats.Calls);            // 运行统计保留
        Assert.Equal(250, live.Stats.TotalLatencyMs);
    }

    [Fact]
    public void RefreshFromSources_PicksUpRuntimeProfileChanges()
    {
        _catalog.Upsert(new ModelProfile
        {
            ProviderId = "late-added",
            ModelId = "x",
            Strengths = { ModelStrength.Planning },
        });

        _vm.RefreshFromSources();

        Assert.Contains(_vm.ProfileEditors, e => e.ProviderId == "late-added");
    }

    // ────────────────────────── 热重载链 ──────────────────────────

    [Fact]
    public async Task Save_ReloadsFactory_FiresProvidersChanged_NewProviderResolvable()
    {
        var fired = 0;
        _factory.ProvidersChanged += () => fired++;

        _vm.AddProviderCommand.Execute(null);
        var newId = _vm.SelectedProvider!.Id;
        _vm.SelectedProvider.DefaultModel = "added-model";

        await _vm.SaveCommand.ExecuteAsync(null);
        Assert.StartsWith("✅", _vm.StatusText);

        Assert.Equal(1, fired);
        Assert.Contains(newId, _factory.ListConfiguredIds());
        Assert.True(_factory.TryGetConfig(newId, out var cfg));
        Assert.Equal("added-model", cfg!.DefaultModel);
    }

    [Fact]
    public async Task Save_SnapshotsConfig_UnsavedUiEditsDoNotLeakIntoRuntime()
    {
        // 热重载契约：运行时 = 最近一次保存时点的配置。
        // 保存后再改 UI（未保存）不得影响工厂持有的配置/探针。
        await _vm.SaveCommand.ExecuteAsync(null);
        Assert.StartsWith("✅", _vm.StatusText);
        var savedBaseUrl = _vm.SelectedProvider!.BaseUrl;

        _vm.SelectedProvider.BaseUrl = "https://unsaved.edit/v1";
        _vm.SelectedProvider.DefaultModel = "unsaved-model";

        Assert.True(_factory.TryGetConfig("deepseek", out var cfg));
        Assert.Equal(savedBaseUrl, cfg!.BaseUrl);
        Assert.NotEqual("unsaved-model", cfg.DefaultModel);
    }

    [Fact]
    public async Task RemoveProvider_ThenSave_ComesOutOfFactory()
    {
        // 移除 minimax → 保存 → 工厂不再认识它
        var minimax = _vm.Providers.Single(p => p.Id == "minimax");
        _vm.SelectedProvider = minimax;
        _vm.RemoveSelectedProviderCommand.Execute(null);
        Assert.DoesNotContain(_vm.Providers, p => p.Id == "minimax");

        await _vm.SaveCommand.ExecuteAsync(null);
        Assert.StartsWith("✅", _vm.StatusText);

        Assert.DoesNotContain("minimax", _factory.ListConfiguredIds());
        Assert.False(_factory.TryGetConfig("minimax", out _));
    }
}
