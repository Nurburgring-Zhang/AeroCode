using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AeroCode.AI.Models;
using AeroCode.AI.Providers;
using AeroCode.Skills;
using AeroCode.Skills.Bundled.Engineering;
using AeroCode.Skills.Registry;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AeroCode.App.ViewModels;

/// <summary>
/// V3 Code Review Tab: 选文件 / 粘代码, 调用 CodeReviewSkill 8 维度。
/// 启发式审查 + 可选 LLM 二次确认（V3.1 已实现，非占位）。
/// AIF-3：多角色对抗评审（批评者→辩护者→裁判）。仅配置 1 个 provider 时如实为
/// "同模型多角色对抗"；配置多 provider 时可扩展为真正跨模型对抗。
/// </summary>
public partial class CodeReviewViewModel : ObservableObject
{
    private readonly SkillHub _hub;
    private readonly ProviderFactory? _providers;

    [ObservableProperty] private string _selectedFilePath = string.Empty;
    [ObservableProperty] private string _sourceCode = string.Empty;
    [ObservableProperty] private string _reportText = string.Empty;
    [ObservableProperty] private string _statusText = "就绪";
    [ObservableProperty] private bool _isReviewing;

    public CodeReviewViewModel(SkillHub hub, ProviderFactory? providers = null)
    {
        _hub = hub;
        _providers = providers;
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

    /// <summary>
    /// AIF-3 多角色对抗评审：批评者挑错 → 辩护者反驳 → 裁判综合裁决。
    /// 三个角色各发一次真实 LLM 调用；仅 1 个 provider 时为同模型多角色对抗（如实标注）。
    /// </summary>
    [RelayCommand]
    private async Task AdversarialReviewAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(SourceCode)) { StatusText = "请先加载文件或粘贴代码"; return; }
        if (_providers is null) { StatusText = "AI 未装配（无 provider 工厂）"; return; }

        IAiProvider provider;
        try { provider = _providers.GetDefault(); }
        catch (Exception ex) { StatusText = $"无可用 provider：{ex.Message}"; return; }

        IsReviewing = true;
        ReportText = string.Empty;
        var code = SourceCode.Length > 30000 ? SourceCode[..30000] + "\n…(过长已截断)" : SourceCode;
        try
        {
            StatusText = "对抗评审 1/3：批评者审查…";
            var critique = await CallAsync(provider,
                "你是极其严格的代码评审者。找出这段代码的 bug、安全漏洞、性能问题、可维护性缺陷，逐条列出并给出严重级别（高/中/低）与依据。宁可苛刻，不要放过问题。",
                code, ct);

            StatusText = "对抗评审 2/3：辩护者反驳…";
            var defense = await CallAsync(provider,
                "你是这段代码的辩护者。下面是一位严格评审者的批评。请逐条评估：哪些批评成立、哪些是过度苛责或误判，并给出理由。客观，不要无脑护短。",
                $"[代码]\n{code}\n\n[评审者的批评]\n{critique}", ct);

            StatusText = "对抗评审 3/3：裁判综合裁决…";
            var verdict = await CallAsync(provider,
                "你是中立的裁判。综合评审者的批评与辩护者的反驳，给出最终裁决：逐条判定每个问题是否成立、最终严重级别、以及应优先修复的清单。输出结构化的 Markdown 裁决报告。",
                $"[评审者的批评]\n{critique}\n\n[辩护者的反驳]\n{defense}", ct);

            var sb = new StringBuilder();
            sb.AppendLine("# 多角色对抗代码评审报告");
            sb.AppendLine();
            sb.AppendLine("> 模式：批评者 → 辩护者 → 裁判。当前仅配置 1 个 provider，为**同模型多角色对抗**（非跨模型）；如需真正跨模型对抗请配置多个 provider。");
            sb.AppendLine();
            sb.AppendLine("## 一、批评者（严格审查）");
            sb.AppendLine();
            sb.AppendLine(critique);
            sb.AppendLine();
            sb.AppendLine("## 二、辩护者（反驳）");
            sb.AppendLine();
            sb.AppendLine(defense);
            sb.AppendLine();
            sb.AppendLine("## 三、裁判（最终裁决）");
            sb.AppendLine();
            sb.AppendLine(verdict);
            ReportText = sb.ToString();
            StatusText = "✓ 对抗评审完成（3 角色）";
        }
        catch (Exception ex)
        {
            StatusText = $"✗ 对抗评审失败：{ex.Message}";
        }
        finally
        {
            IsReviewing = false;
        }
    }

    /// <summary>单次非流式 LLM 调用（system + user），返回文本内容。</summary>
    private static async Task<string> CallAsync(IAiProvider provider, string systemPrompt, string userContent, CancellationToken ct)
    {
        var req = new ChatRequest
        {
            Model = string.Empty,
            Stream = false,
            EnableThinking = false,
            Temperature = 0.2,
            Messages = new[]
            {
                new ChatMessage { Role = "system", Content = systemPrompt },
                new ChatMessage { Role = "user", Content = userContent },
            },
        };
        var resp = await provider.ChatAsync(req, ct);
        return resp.Content ?? string.Empty;
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
