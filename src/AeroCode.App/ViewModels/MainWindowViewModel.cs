using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AeroCode.AI.Models;
using AeroCode.AI.Providers;
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
    private readonly ProviderFactory? _providers;

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private Note? _selectedNote;

    [ObservableProperty]
    private Notebook? _selectedNotebook;

    [ObservableProperty]
    private Tag? _selectedTag;

    // ── 笔记内 AI（AIF-1）：对当前选中笔记的真实 LLM 操作 ──
    [ObservableProperty]
    private string _noteAiInput = string.Empty;

    [ObservableProperty]
    private string _noteAiResult = string.Empty;

    [ObservableProperty]
    private bool _isNoteAiBusy;

    [ObservableProperty]
    private string _statusText = "就绪";

    /// <summary>侧边栏当前选中项索引（UIR 后：0=对话 1=笔记 2=AI助手 3=Mission 4=代码评审）。</summary>
    [ObservableProperty]
    private int _selectedNavIndex;

    /// <summary>R5 侧边栏导航项（UIR-1/2：对话置顶；技能/记忆/诊断移入设置）。
    /// 实例属性：{Binding NavItems} 按实例解析（静态属性绑定会静默失败）。</summary>
    public IReadOnlyList<string> NavItems { get; } = new[]
    {
        "对话", "笔记", "AI 助手", "Mission", "代码评审",
    };

    public ObservableCollection<Notebook> Notebooks { get; } = new();
    public ObservableCollection<Tag> Tags { get; } = new();
    public ObservableCollection<Note> Notes { get; } = new();
    public ObservableCollection<Note> SearchResults { get; } = new();

    public MainWindowViewModel(
        INoteService notes,
        INotebookService notebooks,
        ITagService tags,
        ISearchService search,
        IDialogService dialog,
        ProviderFactory? providers = null)
    {
        _notes = notes;
        _notebooks = notebooks;
        _tags = tags;
        _search = search;
        _dialog = dialog;
        _providers = providers;
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

    partial void OnSearchQueryChanged(string value) => _ = ObserveAsync(RunSearchAsync());

    /// <summary>
    /// 观察后台任务异常：fire-and-forget 场景下把异常落到 StatusText，
    /// 而不是成为 UnobservedTaskException 静默汇。
    /// </summary>
    private async Task ObserveAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception ex)
        {
            StatusText = $"✗ {ex.Message}";
        }
    }

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

    partial void OnSelectedNotebookChanged(Notebook? value) => _ = ObserveAsync(LoadByNotebookAsync(value));

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

    partial void OnSelectedTagChanged(Tag? value) => _ = ObserveAsync(LoadByTagAsync(value));

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

    // ============== 笔记内 AI（AIF-1）：真实 LLM 对当前笔记操作 ==============

    /// <summary>对当前笔记提问/划线检索：问题 + 笔记全文作为上下文，真实调用默认 provider。</summary>
    [RelayCommand]
    private async Task NoteAiAskAsync()
    {
        if (string.IsNullOrWhiteSpace(NoteAiInput))
        {
            StatusText = "请先输入要问的问题";
            return;
        }

        await RunNoteAiAsync(
            "你是笔记助手。仅依据提供的笔记内容回答问题；笔记中没有的信息要明确说明没有，不要编造。",
            $"[问题] {NoteAiInput.Trim()}");
    }

    /// <summary>AI 分析：提炼要点、结构、潜在问题。</summary>
    [RelayCommand]
    private Task NoteAiAnalyzeAsync() => RunNoteAiAsync(
        "你是资深分析助手。对笔记内容做结构化分析：核心要点、逻辑结构、论据充分性、潜在问题或矛盾，分条输出。",
        "[任务] 分析这篇笔记");

    /// <summary>AI 整理：重排为清晰的 Markdown（结果可一键应用到笔记）。</summary>
    [RelayCommand]
    private Task NoteAiOrganizeAsync() => RunNoteAiAsync(
        "你是编辑助手。把笔记重新整理为结构清晰的 Markdown：合理标题层级、列表、必要的分段；保留原意与事实，不新增虚构内容。只输出整理后的 Markdown 正文。",
        "[任务] 整理这篇笔记");

    /// <summary>AI 摘要：压缩为简洁摘要。</summary>
    [RelayCommand]
    private Task NoteAiSummarizeAsync() => RunNoteAiAsync(
        "你是摘要助手。把笔记压缩为 3-5 句的简洁摘要，保留核心信息。",
        "[任务] 摘要这篇笔记");

    /// <summary>把 AI 结果应用为笔记正文（用于"整理"后一键采纳）。</summary>
    [RelayCommand]
    private async Task NoteAiApplyAsync()
    {
        if (SelectedNote is null || string.IsNullOrWhiteSpace(NoteAiResult))
        {
            StatusText = "没有可应用的 AI 结果";
            return;
        }

        SelectedNote.Content = NoteAiResult;
        var r = await _notes.UpdateAsync(SelectedNote.Id, SelectedNote.Title, SelectedNote.Content, null, null);
        StatusText = r.IsSuccess ? "已应用 AI 结果到笔记并保存" : $"应用失败: {r.Error}";
        if (r.IsSuccess) await LoadAllNotesAsync();
    }

    /// <summary>
    /// 笔记 AI 公共执行路径：取当前选中笔记全文作上下文，流式调用默认 provider，
    /// 逐块写入 NoteAiResult。无 provider/无笔记时如实提示，不伪造结果。
    /// </summary>
    private async Task RunNoteAiAsync(string systemPrompt, string taskLine)
    {
        if (_providers is null)
        {
            StatusText = "AI 未装配（无 provider 工厂）";
            return;
        }

        if (SelectedNote is null || string.IsNullOrWhiteSpace(SelectedNote.Content))
        {
            StatusText = "请先选中一篇有内容的笔记";
            return;
        }

        IAiProvider provider;
        try
        {
            provider = _providers.GetDefault();
        }
        catch (Exception ex)
        {
            StatusText = $"无可用 provider：{ex.Message}";
            return;
        }

        IsNoteAiBusy = true;
        NoteAiResult = string.Empty;
        StatusText = "AI 处理中…";
        var sb = new StringBuilder();
        try
        {
            var req = new ChatRequest
            {
                Model = string.Empty,
                Stream = provider.SupportsStreaming,
                EnableThinking = false,
                Temperature = 0.3,
                Messages = new[]
                {
                    new ChatMessage { Role = "system", Content = systemPrompt },
                    new ChatMessage
                    {
                        Role = "user",
                        Content = $"{taskLine}\n\n[笔记标题] {SelectedNote.Title}\n[笔记内容]\n{SelectedNote.Content}",
                    },
                },
            };

            if (provider.SupportsStreaming)
            {
                await foreach (var chunk in provider.StreamChatAsync(req))
                {
                    if (chunk.DeltaContent is { Length: > 0 } c)
                    {
                        sb.Append(c);
                        NoteAiResult = sb.ToString();
                    }
                }
            }
            else
            {
                var resp = await provider.ChatAsync(req);
                sb.Append(resp.Content);
                NoteAiResult = sb.ToString();
            }

            StatusText = sb.Length > 0 ? "AI 完成（可点「应用到笔记」采纳整理结果）" : "AI 未返回内容";
        }
        catch (Exception ex)
        {
            StatusText = $"AI 失败：{ex.Message}";
        }
        finally
        {
            IsNoteAiBusy = false;
        }
    }
}
