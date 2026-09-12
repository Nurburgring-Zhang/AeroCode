using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
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

    /// <summary>笔记 AI「问答」系统提示词（按钮与队列共用，保证行为一致）。</summary>
    private const string NoteAiAskSystemPrompt =
        "你是笔记助手。仅依据提供的笔记内容回答问题；笔记中没有的信息要明确说明没有，不要编造。";

    /// <summary>笔记 AI 指令队列引擎（UIR-5 铺全输入框；执行体为笔记问答，构造函数注入）。</summary>
    public CommandQueueEngine NoteAiQueue { get; }

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

        // UIR-5：笔记 AI 指令队列 —— 执行体为「问答」，空闲守卫为 IsNoteAiBusy。
        NoteAiQueue = new CommandQueueEngine(
            (text, ct) => RunNoteAiAsync(NoteAiAskSystemPrompt, $"[问题] {text.Trim()}", ct),
            () => !IsNoteAiBusy);
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

    /// <summary>导出全部笔记为 Markdown 文件（每篇一个 .md）到用户选定文件夹。</summary>
    [RelayCommand]
    private async Task ExportNotesMarkdownAsync()
    {
        var lifetime = Avalonia.Application.Current?.ApplicationLifetime
            as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
        if (lifetime?.MainWindow is null) { StatusText = "无主窗口，无法导出"; return; }

        var folders = await lifetime.MainWindow.StorageProvider.OpenFolderPickerAsync(
            new Avalonia.Platform.Storage.FolderPickerOpenOptions { Title = "选择导出目录", AllowMultiple = false });
        if (folders.Count == 0) return;
        var dir = folders[0].Path.LocalPath;

        var r = await _notes.GetAllAsync();
        if (!r.IsSuccess || r.Value is null) { StatusText = $"导出失败：{r.Error}"; return; }
        if (r.Value.Count == 0) { StatusText = "没有可导出的笔记"; return; }

        var count = 0;
        foreach (var n in r.Value)
        {
            var title = string.IsNullOrWhiteSpace(n.Title) ? $"note_{n.Id}" : n.Title!;
            var path = Path.Combine(dir, $"{SanitizeFileName(title)}_{n.Id}.md");
            await File.WriteAllTextAsync(path, $"# {n.Title}\n\n{n.Content}");
            count++;
        }
        StatusText = $"✓ 已导出 {count} 篇笔记到 {dir}";
    }

    /// <summary>导出全部笔记为单个 JSON 文件（含标题/正文/元数据）。</summary>
    [RelayCommand]
    private async Task ExportNotesJsonAsync()
    {
        var lifetime = Avalonia.Application.Current?.ApplicationLifetime
            as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
        if (lifetime?.MainWindow is null) { StatusText = "无主窗口，无法导出"; return; }

        var file = await lifetime.MainWindow.StorageProvider.SaveFilePickerAsync(
            new Avalonia.Platform.Storage.FilePickerSaveOptions
            {
                Title = "导出笔记为 JSON",
                SuggestedFileName = "aerocode_notes.json",
                FileTypeChoices = new[] { new Avalonia.Platform.Storage.FilePickerFileType("JSON") { Patterns = new[] { "*.json" } } },
            });
        if (file is null) return;
        var path = file.Path.LocalPath;

        var r = await _notes.GetAllAsync();
        if (!r.IsSuccess || r.Value is null) { StatusText = $"导出失败：{r.Error}"; return; }

        var payload = r.Value.Select(n => new { n.Id, n.Title, n.Content, n.IsPinned, n.CreatedAt, n.UpdatedAt });
        var json = System.Text.Json.JsonSerializer.Serialize(payload, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(path, json);
        StatusText = $"✓ 已导出 {r.Value.Count} 篇笔记到 {path}";
    }

    /// <summary>把文件名中的非法字符替换为下划线。</summary>
    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name) sb.Append(invalid.Contains(ch) ? '_' : ch);
        return sb.ToString();
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

        await RunNoteAiAsync(NoteAiAskSystemPrompt, $"[问题] {NoteAiInput.Trim()}");
    }

    /// <summary>把笔记 AI 输入框指令加入队列（UIR-5）。空闲时引擎自动开始执行。</summary>
    [RelayCommand]
    private void EnqueueNoteAi()
    {
        if (string.IsNullOrWhiteSpace(NoteAiInput))
        {
            StatusText = "请输入要加入队列的指令";
            return;
        }

        var text = NoteAiInput.Trim();
        NoteAiInput = string.Empty;
        NoteAiQueue.Enqueue(text);
    }

    /// <summary>AI 分析：提炼要点、结构、潜在问题。</summary>
    [RelayCommand]
    private async Task NoteAiAnalyzeAsync()
    {
        await RunNoteAiAsync(
            "你是资深分析助手。对笔记内容做结构化分析：核心要点、逻辑结构、论据充分性、潜在问题或矛盾，分条输出。",
            "[任务] 分析这篇笔记");
    }

    /// <summary>AI 整理：重排为清晰的 Markdown（结果可一键应用到笔记）。</summary>
    [RelayCommand]
    private async Task NoteAiOrganizeAsync()
    {
        await RunNoteAiAsync(
            "你是编辑助手。把笔记重新整理为结构清晰的 Markdown：合理标题层级、列表、必要的分段；保留原意与事实，不新增虚构内容。只输出整理后的 Markdown 正文。",
            "[任务] 整理这篇笔记");
    }

    /// <summary>AI 摘要：压缩为简洁摘要。</summary>
    [RelayCommand]
    private async Task NoteAiSummarizeAsync()
    {
        await RunNoteAiAsync(
            "你是摘要助手。把笔记压缩为 3-5 句的简洁摘要，保留核心信息。",
            "[任务] 摘要这篇笔记");
    }

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
    /// ct 供队列引擎中断当前条（默认 None，按钮直接调用不受影响）。
    /// </summary>
    private async Task<bool> RunNoteAiAsync(string systemPrompt, string taskLine, CancellationToken ct = default)
    {
        if (_providers is null)
        {
            StatusText = "AI 未装配（无 provider 工厂）";
            return false; // review M1：拒绝执行。
        }

        if (SelectedNote is null || string.IsNullOrWhiteSpace(SelectedNote.Content))
        {
            StatusText = "请先选中一篇有内容的笔记";
            return false; // review M1：拒绝执行。
        }

        IAiProvider provider;
        try
        {
            provider = _providers.GetDefault();
        }
        catch (Exception ex)
        {
            StatusText = $"无可用 provider：{ex.Message}";
            return false; // review M1：拒绝执行。
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
                await foreach (var chunk in provider.StreamChatAsync(req, ct))
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
                var resp = await provider.ChatAsync(req, ct);
                sb.Append(resp.Content);
                NoteAiResult = sb.ToString();
            }

            StatusText = sb.Length > 0 ? "AI 完成（可点「应用到笔记」采纳整理结果）" : "AI 未返回内容";
        }
        catch (OperationCanceledException)
        {
            StatusText = "AI 已中断";
            throw;
        }
        catch (Exception ex)
        {
            StatusText = $"AI 失败：{ex.Message}";
        }
        finally
        {
            IsNoteAiBusy = false;
            NoteAiQueue.NotifyHostIdle(); // review M2：转空闲后接续执行积压队列（队列循环中为无操作）。
        }

        return true; // review M1：已处理（含完成与 catch 已收敛的错误；OCE 已在上面 rethrow）。
    }
}
