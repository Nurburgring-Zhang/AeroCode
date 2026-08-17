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

    public AppDataPaths()
    {
        RootDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AeroCode");
        DatabaseFile = Path.Combine(RootDirectory, "AeroCode.db");
        // 统一对话独立库，避免与笔记库两个 EF 上下文互相干扰。
        ConversationDatabaseFile = Path.Combine(RootDirectory, "AeroCode.Conversations.db");
        LogDirectory = Path.Combine(RootDirectory, "logs");
        ExportDirectory = Path.Combine(RootDirectory, "exports");
        SettingsFile = Path.Combine(RootDirectory, "settings.json");
        MoaProfilesFile = Path.Combine(RootDirectory, "moa-profiles.json");
        MoaOptionsFile = Path.Combine(RootDirectory, "moa-options.json");
    }

    public void EnsureAll()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(ExportDirectory);
    }
}
