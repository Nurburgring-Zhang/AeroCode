using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using AeroCode.Skills;
using AeroCode.Skills.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AeroCode.App.ViewModels;

/// <summary>
/// V3 Skills Tab: 展示已注册 Skills, Auto-created 标识, usage stats, 描述。
/// 真实从 SkillHub.Registry 读, 0 硬编码。
/// </summary>
public partial class SkillsViewModel : ObservableObject
{
    private readonly SkillHub _hub;

    [ObservableProperty] private string _statusText = "就绪";
    [ObservableProperty] private string _filterText = string.Empty;
    [ObservableProperty] private string _selectedCategory = "全部";

    public ObservableCollection<SkillRow> Skills { get; } = new();
    public ObservableCollection<SkillRow> FilteredSkills { get; } = new();
    public string[] Categories { get; } = new[] { "全部", "engineering", "productivity", "bundled", "user" };

    public SkillsViewModel(SkillHub hub)
    {
        _hub = hub;
        Refresh();
    }

    [RelayCommand]
    private void Refresh()
    {
        Skills.Clear();
        foreach (var s in _hub.List())
        {
            var (invocations, successRate) = _hub.Registry.GetStats(s.Id);
            Skills.Add(new SkillRow(
                Id: s.Id,
                Name: s.Name,
                Description: s.Description,
                Category: s.Category,
                Author: s.Author,
                Version: s.Version,
                Tags: string.Join(", ", s.Tags),
                Invocations: invocations,
                SuccessRate: successRate,
                IsAutoCreated: _hub.Creator != null));
        }
        ApplyFilter();
        StatusText = $"已加载 {Skills.Count} 个 Skills";
    }

    partial void OnFilterTextChanged(string value) => ApplyFilter();
    partial void OnSelectedCategoryChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        FilteredSkills.Clear();
        var q = (FilterText ?? string.Empty).Trim().ToLowerInvariant();
        var cat = SelectedCategory ?? "全部";
        foreach (var s in Skills)
        {
            if (cat != "全部" && !string.Equals(s.Category, cat, System.StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrEmpty(q) &&
                !s.Id.ToLowerInvariant().Contains(q) &&
                !s.Name.ToLowerInvariant().Contains(q) &&
                !s.Description.ToLowerInvariant().Contains(q)) continue;
            FilteredSkills.Add(s);
        }
        StatusText = $"过滤后: {FilteredSkills.Count} / {Skills.Count}";
    }
}

public sealed record SkillRow(
    string Id,
    string Name,
    string Description,
    string Category,
    string Author,
    string Version,
    string Tags,
    int Invocations,
    double SuccessRate,
    bool IsAutoCreated);
