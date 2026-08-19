using System;
using System.IO;

namespace AeroCode.App.Services;

/// <summary>
/// 跨平台数据路径。Windows: %LOCALAPPDATA%/AeroCode/; Android: Avalonia 注入到内部存储。
/// 22 铁律之"无硬编码"：所有路径走此服务。
/// </summary>
public class AppDataPaths
{
    public string RootDirectory { get; }
    public string DatabaseFile { get; }
    public string ConversationDatabaseFile { get; }
    public string LogDirectory { get; }
    public string ExportDirectory { get; }
    public string SettingsFile { get; }

    /// <summary>MOA 模型画像存储（强项/费率/自学习统计）。</summary>
    public string MoaProfilesFile { get; }

    /// <summary>MOA 编排选项（角色绑定/集成规模/单轮预算）。</summary>
    public string MoaOptionsFile { get; }

    /// <summary>工具授权决策（用户"记住选择"/设置页修改的持久化）。</summary>
    public string PermissionsFile { get; }

    private static string? _rootDirectoryOverride;

    /// <summary>
    /// 平台数据根覆盖：Android 头项目在应用服务构建前设置为 app 私有内部存储路径
    /// （Context.FilesDir/AeroCode，免任何存储权限）。null = 走默认 LocalApplicationData。
    /// set-once 语义：一经设置，后续写入忽略——防止进程内重入（Activity 重建、
    /// AppBuilder 二次构造）把数据根改到别处造成双根分裂。
    /// </summary>
    public static string? RootDirectoryOverride
    {
        get => _rootDirectoryOverride;
        set
        {
            if (_rootDirectoryOverride is not null)
            {
                return;
            }
            _rootDirectoryOverride = value;
        }
    }

    public AppDataPaths()
        : this(RootDirectoryOverride
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AeroCode"))
    {
    }

    /// <summary>指定根目录构造（测试/隔离环境：指向临时目录，不触碰用户真实数据）。</summary>
    public AppDataPaths(string rootDirectory)
    {
        RootDirectory = rootDirectory;
        DatabaseFile = Path.Combine(RootDirectory, "AeroCode.db");
        // 统一对话独立库，避免与笔记库两个 EF 上下文互相干扰。
        ConversationDatabaseFile = Path.Combine(RootDirectory, "AeroCode.Conversations.db");
        LogDirectory = Path.Combine(RootDirectory, "logs");
        ExportDirectory = Path.Combine(RootDirectory, "exports");
        SettingsFile = Path.Combine(RootDirectory, "settings.json");
        MoaProfilesFile = Path.Combine(RootDirectory, "moa-profiles.json");
        MoaOptionsFile = Path.Combine(RootDirectory, "moa-options.json");
        PermissionsFile = Path.Combine(RootDirectory, "permissions.json");
    }

    public void EnsureAll()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(ExportDirectory);
    }
}
