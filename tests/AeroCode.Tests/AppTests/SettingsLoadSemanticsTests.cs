// Copyright (c) AeroCode V3.0
// SettingsService Load 语义测试 — 损坏 JSON / 文件缺失 / 读取期 IOException 的真实行为。
// 结论（以 src/AeroCode.App/Configuration/SettingsService.cs 代码事实为准）：
// LoadAsync 只 catch JsonException；R3-δ 起损坏 JSON 加固——改名备份 *.corrupt-<时间戳> +
// 默认值继续运行 + LastLoadError 暴露拒载事实；IOException 等环境故障向上抛——符合审计预期。
using System;
using System.IO;
using System.Threading.Tasks;
using AeroCode.App.Configuration;
using AeroCode.App.Services;
using Xunit;

namespace AeroCode.Tests.AppTests;

/// <summary>
/// SettingsService 的加载语义：
/// 1) 损坏 JSON → 窄 catch JsonException：改名备份 *.corrupt-&lt;UTC时间戳&gt;（内容字节原样）+
///    默认值继续运行 + LastLoadError 非空；二次 Save 不丢备份；
/// 2) 文件缺失 → 默认配置并立即持久化（LoadAsync 内部调用 SaveAsync）；
/// 3) 文件存在但读取时被独占锁定 → IOException 向上抛（不静默吞掉）。
/// </summary>
public sealed class SettingsLoadSemanticsTests : IDisposable
{
    private readonly string _root;
    private readonly AppDataPaths _paths;

    public SettingsLoadSemanticsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"settings_load_{Guid.NewGuid():N}");
        _paths = new AppDataPaths(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>
    /// R3-δ 损坏 JSON 加固：改名备份 + 默认值运行 + 错误状态非空，且二次保存不丢备份、不丢损坏内容。
    /// </summary>
    [Fact]
    public async Task CorruptJson_BackupCreated_DefaultsRun_ErrorStateExposed_BackupSurvivesSave()
    {
        var svc = new SettingsService(_paths); // ctor EnsureAll 创建目录
        const string corrupt = "{这不是合法 JSON";
        await File.WriteAllTextAsync(_paths.SettingsFile, corrupt);

        await svc.LoadAsync();

        // 默认值继续运行。
        Assert.Equal(4, svc.Current.Ai.Providers.Count);
        Assert.Contains(svc.Current.Ai.Providers, p => p.Id == "deepseek");
        Assert.Equal("Dark", svc.Current.Ui.Theme);
        Assert.Equal(14, svc.Current.Ui.FontSize);

        // 错误状态非空（拒载事实可观测，仿 MoaOptions.LastLoadError）。
        Assert.NotNull(svc.LastLoadError);
        Assert.IsType<System.Text.Json.JsonException>(svc.LastLoadError);

        // 备份文件存在且内容字节原样；原路径的损坏文件已被改名移走（不覆盖原文件）。
        var backups = Directory.GetFiles(_root, "settings.json.corrupt-*");
        var backup = Assert.Single(backups);
        Assert.Matches(@"^settings\.json\.corrupt-\d{8}T\d{9}Z(?:-\d+)?$", Path.GetFileName(backup));
        Assert.Equal(corrupt, await File.ReadAllTextAsync(backup));
        Assert.False(File.Exists(_paths.SettingsFile)); // 只改名，不覆盖

        // 二次保存：settings.json 重建，备份不丢、损坏内容仍原样。
        await svc.SaveAsync();
        Assert.True(File.Exists(_paths.SettingsFile));
        Assert.Single(Directory.GetFiles(_root, "settings.json.corrupt-*"));
        Assert.Equal(corrupt, await File.ReadAllTextAsync(backups[0]));
        // 新 settings.json 是可解析的默认配置（含 R3-δ 三个代管字段节）。
        var reloaded = new SettingsService(_paths);
        await reloaded.LoadAsync();
        Assert.Null(reloaded.LastLoadError);
        Assert.Equal(4, reloaded.Current.Ai.Providers.Count);
    }

    /// <summary>成功加载清除错误状态；文件缺失分支同样不置错误状态。</summary>
    [Fact]
    public async Task LoadSuccess_MissingFile_ErrorStateCleared()
    {
        var svc = new SettingsService(_paths);
        Assert.False(File.Exists(_paths.SettingsFile));
        await svc.LoadAsync();
        Assert.Null(svc.LastLoadError); // 缺失 = 未配置，不算拒载

        var second = new SettingsService(_paths);
        await second.LoadAsync(); // 文件存在且合法
        Assert.Null(second.LastLoadError);
    }

    /// <summary>文件缺失：返回默认配置并立即持久化（真实行为：LoadAsync 在缺失分支调用 SaveAsync）。</summary>
    [Fact]
    public async Task MissingFile_LoadsDefaults_AndPersistsThem()
    {
        var svc = new SettingsService(_paths);
        Assert.False(File.Exists(_paths.SettingsFile));

        await svc.LoadAsync();

        Assert.Equal(4, svc.Current.Ai.Providers.Count);
        Assert.True(File.Exists(_paths.SettingsFile));

        // 第二个实例此时走"文件存在"分支，回读一致
        var second = new SettingsService(_paths);
        await second.LoadAsync();
        Assert.Equal(4, second.Current.Ai.Providers.Count);
        Assert.Equal(svc.Current.Ai.DefaultModel, second.Current.Ai.DefaultModel);
    }

    /// <summary>文件存在但读取时被独占锁定：IOException 向上抛（不吞）——与审计预期一致。
    /// 原因（见实现注释）：静默吞掉环境故障会让下次 Save 用默认配置覆盖用户真实文件。</summary>
    [Fact]
    public async Task LoadAsync_FileExclusivelyLocked_IOExceptionPropagates()
    {
        var svc = new SettingsService(_paths);
        await svc.LoadAsync(); // 缺失分支生成 settings.json
        Assert.True(File.Exists(_paths.SettingsFile));

        await using (var locker = new FileStream(
            _paths.SettingsFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            // FileShare.None 独占锁 → ReadAllTextAsync 抛 IOException；
            // LoadAsync 只 catch JsonException，故必须上抛。
            var fresh = new SettingsService(_paths);
            await Assert.ThrowsAsync<IOException>(() => fresh.LoadAsync());
        }

        // 解锁后读取恢复——证明是环境故障而非内容问题
        var recovered = new SettingsService(_paths);
        await recovered.LoadAsync();
        Assert.Equal(4, recovered.Current.Ai.Providers.Count);
        Assert.True(Directory.GetFiles(_root, "settings.json.corrupt-*").Length == 0); // 环境故障不做损坏备份
    }
}
