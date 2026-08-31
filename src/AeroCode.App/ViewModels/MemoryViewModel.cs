using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using AeroCode.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AeroCode.App.ViewModels;

/// <summary>
/// V3 Memory Tab: 展示和编辑 Hermes-style MEMORY.md / USER.md。
/// 实际读/写到 %LOCALAPPDATA%/AeroCode/memories/。
/// 批次 B G5 升级：召回展示（SessionMemoryService.BuildMemoryBlockAsync 的真实笔记 Top-K）
/// + 人工沉淀按钮（ConsolidateManualAsync，来源=用户手写，非模型生成）。
/// </summary>
public partial class MemoryViewModel : ObservableObject
{
    private readonly string _memoryDir;
    private readonly SessionMemoryService? _memory;
    private const int MemoryMaxChars = 2200;  // Hermes hard rule
    private const int UserMaxChars = 1375;    // Hermes hard rule

    [ObservableProperty] private string _memoryContent = string.Empty;
    [ObservableProperty] private string _userContent = string.Empty;
    [ObservableProperty] private string _statusText = "就绪";
    [ObservableProperty] private int _memoryCharCount;
    [ObservableProperty] private int _userCharCount;

    /// <summary>召回查询词（G5 召回展示区）。</summary>
    [ObservableProperty] private string _recallQuery = string.Empty;

    /// <summary>最近一次召回的真实命中（分数+标题+预览，来自真实检索栈）。</summary>
    public ObservableCollection<RecalledNote> RecalledNotes { get; } = new();

    /// <summary>人工沉淀标题（留空自动生成时间戳标题）。</summary>
    [ObservableProperty] private string _manualTitle = string.Empty;

    /// <summary>人工沉淀内容（真实来源=用户手写，入库为 Fact 经验）。</summary>
    [ObservableProperty] private string _manualContent = string.Empty;

    public string MemoryMaxDisplay => $"{MemoryMaxChars} chars max";
    public string UserMaxDisplay => $"{UserMaxChars} chars max";

    public MemoryViewModel(AeroCode.App.Services.AppDataPaths paths, SessionMemoryService? memory = null)
    {
        _memoryDir = Path.Combine(paths.RootDirectory, "memories");
        Directory.CreateDirectory(_memoryDir);
        _memory = memory;
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

    /// <summary>
    /// 语义召回（G5 展示区）：以查询词经 SessionMemoryService 真实检索栈跑笔记 Top-K，
    /// 命中逐条展示（分数/标题/预览）。服务未装配/查询为空时如实提示，不伪造命中。
    /// </summary>
    [RelayCommand]
    private async Task RecallAsync()
    {
        RecalledNotes.Clear();
        if (_memory is null)
        {
            StatusText = "✗ 会话记忆服务未装配，召回不可用";
            return;
        }

        var query = RecallQuery.Trim();
        if (query.Length == 0)
        {
            StatusText = "输入召回查询后再检索";
            return;
        }

        try
        {
            var block = await _memory.BuildMemoryBlockAsync(query);
            foreach (var note in block.Recalled)
            {
                RecalledNotes.Add(note);
            }

            StatusText = block.Recalled.Count > 0
                ? $"召回 {block.Recalled.Count} 条相关笔记"
                : block.DegradedNote is not null
                    ? $"⚠ {block.DegradedNote}"
                    : "召回 0 条（无相关笔记或召回未启用）";
        }
        catch (Exception ex)
        {
            StatusText = $"✗ 召回失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 人工沉淀（G5 按钮）：用户在面板手写的内容作为 Fact 经验真实入库
    /// （SessionMemoryService.ConsolidateManualAsync，来源=用户，非模型生成）。
    /// </summary>
    [RelayCommand]
    private async Task ConsolidateManualAsync()
    {
        if (_memory is null)
        {
            StatusText = "✗ 会话记忆服务未装配，沉淀不可用";
            return;
        }

        try
        {
            var result = await _memory.ConsolidateManualAsync(ManualTitle, ManualContent);
            StatusText = result;
            if (!result.Contains("未沉淀"))
            {
                ManualContent = string.Empty;
            }
        }
        catch (Exception ex)
        {
            StatusText = $"✗ 沉淀失败：{ex.Message}";
        }
    }
}
