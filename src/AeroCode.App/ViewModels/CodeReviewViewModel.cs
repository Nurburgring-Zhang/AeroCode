using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AeroCode.Skills;
using AeroCode.Skills.Bundled.Engineering;
using AeroCode.Skills.Registry;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AeroCode.App.ViewModels;

/// <summary>
/// V3 Code Review Tab: 选文件 / 粘代码, 调用 CodeReviewSkill 8 维度。
/// 真实 LLM 增强占位, 当前用启发式 + 可选 LLM 二次确认 (V3.1)。
/// </summary>
public partial class CodeReviewViewModel : ObservableObject
{
    private readonly SkillHub _hub;

    [ObservableProperty] private string _selectedFilePath = string.Empty;
    [ObservableProperty] private string _sourceCode = string.Empty;
    [ObservableProperty] private string _reportText = string.Empty;
    [ObservableProperty] private string _statusText = "就绪";
    [ObservableProperty] private bool _isReviewing;

    public CodeReviewViewModel(SkillHub hub)
    {
        _hub = hub;
    }

    [RelayCommand]
    private async Task PickFileAsync(CancellationToken ct)
    {
        try
        {
            // Avalonia 11 StorageProvider
            var lifetime = Avalonia.Application.Current?.ApplicationLifetime
                as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
            if (lifetime?.MainWindow is null) { StatusText = "无主窗口"; return; }
            var sp = lifetime.MainWindow.StorageProvider;
            var file = await sp.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                Title = "选择要 review 的源文件",
                AllowMultiple = false,
            });
            if (file.Count == 0) return;
            SelectedFilePath = file[0].Path.LocalPath;
            SourceCode = await File.ReadAllTextAsync(SelectedFilePath, ct);
            StatusText = $"已加载: {SelectedFilePath} ({SourceCode.Length} 字符)";
        }
        catch (Exception ex) { StatusText = $"✗ {ex.Message}"; }
    }

    [RelayCommand]
    private async Task ReviewAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(SourceCode)) { StatusText = "请先加载文件或粘贴代码"; return; }
        IsReviewing = true;
        StatusText = "Reviewing (8 维度)...";
        ReportText = string.Empty;
        try
        {
            var skill = _hub.Get("engineering/code-review") as CodeReviewSkill;
            if (skill is null)
            {
                StatusText = "CodeReviewSkill 未注册";
                return;
            }
            var input = new SkillInput
            {
                Args = new System.Collections.Generic.Dictionary<string, object?> { ["code"] = SourceCode },
                UserMessage = SelectedFilePath,
            };
            var ctx = new SkillContext { WorkspaceRoot = Environment.CurrentDirectory, UserMessage = SelectedFilePath };
            var result = await skill.ExecuteAsync(input, ctx, ct);
            ReportText = result.Text;
            _hub.Registry.RecordInvocation(skill.Id, result.Success);
            StatusText = result.Success ? $"✓ 8 维度 review 完成" : $"✗ {result.Text}";
        }
        catch (Exception ex) { StatusText = $"✗ {ex.Message}"; }
        finally { IsReviewing = false; }
    }

    [RelayCommand]
    private void Clear()
    {
        SourceCode = string.Empty;
        ReportText = string.Empty;
        SelectedFilePath = string.Empty;
        StatusText = "已清空";
    }
}
