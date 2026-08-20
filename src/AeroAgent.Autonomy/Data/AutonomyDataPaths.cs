using System;
using System.IO;

namespace AeroAgent.Autonomy.Data;

/// <summary>
/// 自主内核数据路径。所有产物（SQLite 库、复盘 md）都落在此根目录下，
/// 与笔记库/对话库分离。构造时指定根目录（测试指向临时目录，生产指向应用数据根）。
/// </summary>
public sealed class AutonomyDataPaths
{
    /// <summary>自主数据根目录。</summary>
    public string RootDirectory { get; }

    /// <summary>自主 SQLite 库文件（missions + lessons）。</summary>
    public string DatabaseFile { get; }

    /// <summary>复盘 md 落盘目录。</summary>
    public string RetrospectivesDirectory { get; }

    /// <summary>指定根目录构造（自动派生子路径并确保目录存在）。</summary>
    public AutonomyDataPaths(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            throw new ArgumentException("rootDirectory 不能为空。", nameof(rootDirectory));
        }

        RootDirectory = rootDirectory;
        DatabaseFile = Path.Combine(RootDirectory, "AeroCode.Autonomy.db");
        RetrospectivesDirectory = Path.Combine(RootDirectory, "retrospectives");
    }

    /// <summary>确保根目录与复盘目录存在（幂等）。</summary>
    public void EnsureDirectories()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(RetrospectivesDirectory);
    }
}
