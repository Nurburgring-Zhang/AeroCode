using System;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AeroCode.App.ViewModels;

/// <summary>
/// V3 Memory Tab: 展示和编辑 Hermes-style MEMORY.md / USER.md。
/// 实际读/写到 %LOCALAPPDATA%/AeroCode/memories/。
/// </summary>
public partial class MemoryViewModel : ObservableObject
{
    private readonly string _memoryDir;
    private const int MemoryMaxChars = 2200;  // Hermes hard rule
    private const int UserMaxChars = 1375;    // Hermes hard rule

    [ObservableProperty] private string _memoryContent = string.Empty;
    [ObservableProperty] private string _userContent = string.Empty;
    [ObservableProperty] private string _statusText = "就绪";
    [ObservableProperty] private int _memoryCharCount;
    [ObservableProperty] private int _userCharCount;

    public string MemoryMaxDisplay => $"{MemoryMaxChars} chars max";
    public string UserMaxDisplay => $"{UserMaxChars} chars max";

    public MemoryViewModel(AeroCode.App.Services.AppDataPaths paths)
    {
        _memoryDir = Path.Combine(paths.RootDirectory, "memories");
        Directory.CreateDirectory(_memoryDir);
        Load();
    }

    private string MemoryFile => Path.Combine(_memoryDir, "MEMORY.md");
    private string UserFile => Path.Combine(_memoryDir, "USER.md");

    [RelayCommand]
    private void Load()
    {
        try
        {
            MemoryContent = File.Exists(MemoryFile) ? File.ReadAllText(MemoryFile) : DefaultMemory();
            UserContent = File.Exists(UserFile) ? File.ReadAllText(UserFile) : DefaultUser();
            MemoryCharCount = MemoryContent.Length;
            UserCharCount = UserContent.Length;
            StatusText = "已加载";
        }
        catch (Exception ex) { StatusText = $"✗ {ex.Message}"; }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        try
        {
            if (MemoryContent.Length > MemoryMaxChars)
            {
                StatusText = $"⚠ MEMORY.md 超过 {MemoryMaxChars} 字符 (当前 {MemoryContent.Length}), 截断保存";
                MemoryContent = MemoryContent[..MemoryMaxChars];
            }
            if (UserContent.Length > UserMaxChars)
            {
                StatusText = $"⚠ USER.md 超过 {UserMaxChars} 字符, 截断保存";
                UserContent = UserContent[..UserMaxChars];
            }
            await File.WriteAllTextAsync(MemoryFile, MemoryContent);
            await File.WriteAllTextAsync(UserFile, UserContent);
            MemoryCharCount = MemoryContent.Length;
            UserCharCount = UserContent.Length;
            StatusText = $"✓ 已保存 ({DateTime.Now:HH:mm:ss})";
        }
        catch (Exception ex) { StatusText = $"✗ {ex.Message}"; }
    }

    private static string DefaultMemory() => """
        # MEMORY.md (AeroCode 长期记忆)
        # 此文件内容会自动注入到每次对话的 system prompt。
        # 字符上限 2200。超过会截断。

        ## 用户工程偏好
        - C# / .NET 9, 启用 Nullable + TreatWarningsAsErrors
        - Avalonia 11 桌面应用
        - EF Core SQLite 本地优先

        ## 当前项目
        - AeroCode: 本地优先笔记 + AI 助手 + Agent Harness
        - V3 集成 8 个项目: Hermes / OpenCode / DeepSeek-Harness / Reasonix / MattPocock / eng-practices / CodeFlow / Avernet
        """;

    private static string DefaultUser() => """
        # USER.md (用户画像)
        # 字符上限 1375。超过会截断。
        - 阿里通义千问 Qwen 多媒体/多模态数据策略团队负责人
        - 关注: 大模型训练数据 / 质量 / 审美 / 策略
        - 沟通风格: 直接, 要数据, 要质量, 不接受批量模板
        """;
}
