using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using AeroCode.App.Services;
using AeroCode.Core.Models;
using AeroCode.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AeroCode.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly INoteService _notes;
    private readonly INotebookService _notebooks;
    private readonly ITagService _tags;
    private readonly ISearchService _search;
    private readonly IDialogService _dialog;

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private Note? _selectedNote;

    [ObservableProperty]
    private Notebook? _selectedNotebook;

    [ObservableProperty]
    private Tag? _selectedTag;

    [ObservableProperty]
    private string _statusText = "就绪";

    public ObservableCollection<Notebook> Notebooks { get; } = new();
    public ObservableCollection<Tag> Tags { get; } = new();
    public ObservableCollection<Note> Notes { get; } = new();
    public ObservableCollection<Note> SearchResults { get; } = new();

    public MainWindowViewModel(
        INoteService notes,
        INotebookService notebooks,
        ITagService tags,
        ISearchService search,
        IDialogService dialog)
    {
        _notes = notes;
        _notebooks = notebooks;
        _tags = tags;
        _search = search;
        _dialog = dialog;
    }

    public async Task InitializeAsync()
    {
        await LoadNotebooksAsync();
        await LoadTagsAsync();
        await LoadAllNotesAsync();
    }

    [RelayCommand]
    private async Task LoadNotebooksAsync()
    {
        var r = await _notebooks.GetRootsAsync();
        if (r.IsSuccess)
        {
            Notebooks.Clear();
            foreach (var nb in r.Value!) Notebooks.Add(nb);
        }
        StatusText = r.IsSuccess ? $"已加载 {Notebooks.Count} 个笔记本" : $"加载笔记本失败: {r.Error}";
    }

    [RelayCommand]
    private async Task LoadTagsAsync()
    {
        var r = await _tags.GetAllAsync();
        if (r.IsSuccess)
        {
            Tags.Clear();
            foreach (var t in r.Value!) Tags.Add(t);
        }
    }

    [RelayCommand]
    private async Task LoadAllNotesAsync()
    {
        var r = await _notes.GetAllAsync();
        if (r.IsSuccess)
        {
            Notes.Clear();
            foreach (var n in r.Value!) Notes.Add(n);
            StatusText = $"共 {Notes.Count} 条笔记";
        }
        else StatusText = $"加载笔记失败: {r.Error}";
    }

    [RelayCommand]
    private async Task CreateNoteAsync()
    {
        var r = await _notes.CreateAsync("新建笔记", string.Empty, SelectedNotebook?.Id);
        if (r.IsSuccess)
        {
            await LoadAllNotesAsync();
            SelectedNote = r.Value;
            StatusText = $"已创建笔记 #{r.Value!.Id}";
        }
        else
        {
            await _dialog.ShowMessageAsync("创建失败", r.Error!);
        }
    }

    [RelayCommand]
    private async Task CreateNotebookAsync()
    {
        var r = await _notebooks.CreateAsync("新建笔记本", null, SelectedNotebook?.Id);
        if (r.IsSuccess)
        {
            await LoadNotebooksAsync();
            StatusText = $"已创建笔记本 #{r.Value!.Id}";
        }
        else await _dialog.ShowMessageAsync("创建失败", r.Error!);
    }

    [RelayCommand]
    private async Task DeleteNoteAsync()
    {
        if (SelectedNote is null) return;
        var ok = await _dialog.ConfirmAsync("删除确认", $"确定要删除笔记「{SelectedNote.Title}」吗?");
        if (!ok) return;
        var r = await _notes.SoftDeleteAsync(SelectedNote.Id);
        if (r.IsSuccess)
        {
            await LoadAllNotesAsync();
            SelectedNote = null;
            StatusText = "笔记已删除(可恢复)";
        }
    }

    [RelayCommand]
    private async Task TogglePinAsync()
    {
        if (SelectedNote is null) return;
        var r = await _notes.TogglePinAsync(SelectedNote.Id);
        if (r.IsSuccess) await LoadAllNotesAsync();
    }

    [RelayCommand]
    private async Task SaveNoteAsync()
    {
        if (SelectedNote is null) return;
        var r = await _notes.UpdateAsync(
            SelectedNote.Id,
            SelectedNote.Title,
            SelectedNote.Content,
            null,
            null);
        StatusText = r.IsSuccess ? "已保存" : $"保存失败: {r.Error}";
        if (r.IsSuccess) await LoadAllNotesAsync();
    }

    partial void OnSearchQueryChanged(string value) => _ = RunSearchAsync();

    [RelayCommand]
    private async Task RunSearchAsync()
    {
        if (string.IsNullOrWhiteSpace(SearchQuery))
        {
            SearchResults.Clear();
            return;
        }
        var r = await _search.SearchAsync(SearchQuery);
        SearchResults.Clear();
        if (r.IsSuccess)
        {
            foreach (var n in r.Value!) SearchResults.Add(n);
            StatusText = $"搜索: 找到 {SearchResults.Count} 条";
        }
    }

    partial void OnSelectedNotebookChanged(Notebook? value) => _ = LoadByNotebookAsync(value);

    private async Task LoadByNotebookAsync(Notebook? nb)
    {
        if (nb is null) { await LoadAllNotesAsync(); return; }
        var r = await _notes.GetByNotebookAsync(nb.Id, recursive: true);
        if (r.IsSuccess)
        {
            Notes.Clear();
            foreach (var n in r.Value!) Notes.Add(n);
            StatusText = $"笔记本「{nb.Name}」: {Notes.Count} 条";
        }
    }

    partial void OnSelectedTagChanged(Tag? value) => _ = LoadByTagAsync(value);

    private async Task LoadByTagAsync(Tag? tag)
    {
        if (tag is null) return;
        var r = await _tags.GetNotesByTagAsync(tag.Id);
        if (r.IsSuccess)
        {
            Notes.Clear();
            foreach (var n in r.Value!) Notes.Add(n);
            StatusText = $"标签 #{tag.Name}: {Notes.Count} 条";
        }
    }
}
