// Copyright (c) AeroCode V3.0
// SettingsService Load 语义测试 — 损坏 JSON / 文件缺失 / 读取期 IOException 的真实行为。
// 结论（以 src/AeroCode.App/Configuration/SettingsService.cs 代码事实为准）：
// LoadAsync 只 catch JsonException（降级默认且不回写磁盘）；IOException 等环境故障向上抛——符合审计预期。
using System.IO;
using System.Threading.Tasks;
using AeroCode.App.Configuration;
using AeroCode.App.Services;
using Xunit;

namespace AeroCode.Tests.AppTests;

/// <summary>
/// SettingsService 的加载语义：
/// 1) 损坏 JSON → 窄 catch JsonException，降级默认配置且不回写磁盘；
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

    /// <summary>损坏 JSON：降级为默认四 provider，且不回写磁盘——损坏文件原样保留，等待用户下次保存修复。</summary>
    [Fact]
    public async Task CorruptJson_FallsBackToDefaults_DoesNotOverwriteFile()
    {
        var svc = new SettingsService(_paths); // ctor EnsureAll 创建目录
        const string corrupt = "{这不是合法 JSON";
        await File.WriteAllTextAsync(_paths.SettingsFile, corrupt);

        await svc.LoadAsync();

        Assert.Equal(4, svc.Current.Ai.Providers.Count);
        Assert.Contains(svc.Current.Ai.Providers, p => p.Id == "deepseek");
        Assert.Equal("Dark", svc.Current.Ui.Theme);
        Assert.Equal(2200, svc.Current.Ui.MemoryMaxChars);
        // 降级不回写：静默回写默认值会擦掉用户真实配置
        Assert.Equal(corrupt, await File.ReadAllTextAsync(_paths.SettingsFile));
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
    }
}
