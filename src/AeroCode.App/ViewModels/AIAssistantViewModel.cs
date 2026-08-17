using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AeroCode.AI.Capabilities;
using AeroCode.AI.Embedding;
using AeroCode.AI.Models;
using AeroCode.AI.Providers;
using AeroCode.Core.Models;
using AeroCode.Core.Services;
using AeroCode.Harness;
using AeroCode.Skills;
using AeroCode.Skills.Registry;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AeroCode.App.ViewModels;

/// <summary>
/// AI 助手面板:6 capability + 流式输出 + 多模型选择 + 消息操作。
/// 0 硬编码:provider/model 从 settings 读, capability 调真实 LLM。
/// </summary>
public partial class AIAssistantViewModel : ObservableObject
{
    private readonly ProviderFactory _factory;
    private readonly HarnessHost? _harness;
    private readonly SkillHub? _skills;
    private readonly EmbeddingClient? _embedding;
    private readonly VectorStore? _vectorStore;

    [ObservableProperty] private string _userInput = string.Empty;
    [ObservableProperty] private string _assistantReply = string.Empty;
    [ObservableProperty] private string _selectedProviderId = string.Empty;
    [ObservableProperty] private string _selectedModel = string.Empty;
    [ObservableProperty] private bool _isStreaming;
    [ObservableProperty] private string _statusText = "就绪";

    public ObservableCollection<string> AvailableProviders { get; } = new();
    public ObservableCollection<string> AvailableModels { get; } = new();
    public ObservableCollection<ChatMessage> History { get; } = new();
    public ObservableCollection<SkillSummary> AvailableSkills { get; } = new();

    [ObservableProperty] private SkillSummary? _selectedSkill;
    [ObservableProperty] private string _selectedLanguage = "English";
    [ObservableProperty] private string _translateInput = string.Empty;
    [ObservableProperty] private string _writeTopic = string.Empty;
    [ObservableProperty] private string _qaQuestion = string.Empty;
    [ObservableProperty] private string _semanticQuery = string.Empty;

    public string[] CommonLanguages { get; } = new[]
    {
        "English", "简体中文", "日本語", "한국어", "Français", "Deutsch",
        "Español", "Italiano", "Русский", "Português", "العربية", "हिन्दी"
    };

    public AIAssistantViewModel(ProviderFactory factory, HarnessHost? harness = null, SkillHub? skills = null, EmbeddingClient? embedding = null, VectorStore? vectorStore = null)
    {
        _factory = factory;
        _harness = harness;
        _skills = skills;
        _embedding = embedding;
        _vectorStore = vectorStore;
        foreach (var id in factory.ListConfiguredIds()) AvailableProviders.Add(id);
        if (AvailableProviders.Count > 0)
            SelectedProviderId = factory.GetDefault().ProviderId;
        RefreshSkills();
    }

    private void RefreshSkills()
    {
        AvailableSkills.Clear();
        if (_skills is null) return;
        foreach (var s in _skills.List())
            AvailableSkills.Add(new SkillSummary(s.Id, s.Name, s.Description, s.Category, s.Version, s.Tags));
    }

    partial void OnSelectedProviderIdChanged(string value)
    {
        AvailableModels.Clear();
        try
        {
            var p = _factory.Get(value);
            // Real models from provider config; we currently show default + common aliases.
            AvailableModels.Add($"{p.DisplayName} default");
            SelectedModel = string.Empty;
        }
        catch { }
    }

    // ============== Core: Send ==============

    [RelayCommand]
    private async Task SendAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(UserInput) || IsStreaming) return;
        var provider = _factory.Get(SelectedProviderId);
        History.Add(new ChatMessage { Role = "user", Content = UserInput });
        var inputSnapshot = UserInput;
        UserInput = string.Empty;
        AssistantReply = string.Empty;
        IsStreaming = true;
        StatusText = "生成中...";
        try
        {
            var req = new ChatRequest
            {
                Model = SelectedModel,
                Messages = History.ToArray(),
                Stream = true,
                EnableThinking = true,
                ThinkingEffort = "high",
                Temperature = 0.7
            };
            var sb = new StringBuilder();
            await foreach (var chunk in provider.StreamChatAsync(req, ct))
            {
                if (chunk.DeltaContent is { Length: > 0 } c) sb.Append(c);
                if (chunk.DeltaReasoning is { Length: > 0 } r) sb.Append(r);
                AssistantReply = sb.ToString();
            }
            History.Add(new ChatMessage { Role = "assistant", Content = sb.ToString() });
            StatusText = $"✓ {provider.DisplayName}";
        }
        catch (Exception ex)
        {
            StatusText = $"✗ {ex.Message}";
            History.Add(new ChatMessage { Role = "assistant", Content = $"[错误] {ex.Message}" });
        }
        finally
        {
            IsStreaming = false;
        }
    }

    [RelayCommand]
    private void ClearHistory() { History.Clear(); AssistantReply = string.Empty; StatusText = "已清空"; }

    [RelayCommand]
    private async Task CopyLastAsync()
    {
        if (History.Count == 0) return;
        var last = History.Last();
        try
        {
            var clipboard = Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime d
                ? d.MainWindow?.Clipboard : null;
            if (clipboard is null) { StatusText = "✗ 剪贴板不可用"; return; }
            await clipboard.SetTextAsync(last.Content ?? string.Empty);
            StatusText = "✓ 已复制";
        }
        catch (Exception ex) { StatusText = $"✗ 复制失败: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task RegenerateAsync(CancellationToken ct)
    {
        // Find last user message, drop last assistant, re-send
        for (var i = History.Count - 1; i >= 0; i--)
        {
            if (History[i].Role == "user") { UserInput = History[i].Content; break; }
        }
        if (History.Count > 0 && History[History.Count - 1].Role == "assistant")
            History.RemoveAt(History.Count - 1);
        await SendAsync(ct);
    }

    // ============== Capability 1: Summarize ==============

    [RelayCommand]
    private async Task SummarizeLastAsync(CancellationToken ct)
    {
        var lastUser = History.LastOrDefault(m => m.Role == "user");
        if (lastUser is null) { StatusText = "无内容可摘要"; return; }
        await RunCapabilityAsync(
            () => new Summarizer(_factory.Get(SelectedProviderId), NullLogger<Summarizer>.Instance),
            "summarize",
            lastUser.Content,
            "压缩到 2-3 句",
            ct);
    }

    // ============== Capability 2: Translate ==============

    [RelayCommand]
    private async Task TranslateAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(TranslateInput)) { StatusText = "请输入要翻译的文本"; return; }
        IsStreaming = true;
        StatusText = $"翻译到 {SelectedLanguage}...";
        try
        {
            var translator = new Translator(_factory.Get(SelectedProviderId), NullLogger<Translator>.Instance);
            var result = await translator.TranslateAsync(TranslateInput, SelectedLanguage, ct: ct);
            History.Add(new ChatMessage { Role = "user", Content = $"[翻译 → {SelectedLanguage}] {TranslateInput}" });
            History.Add(new ChatMessage { Role = "assistant", Content = result });
            AssistantReply = result;
            TranslateInput = string.Empty;
            StatusText = $"✓ 翻译完成 ({SelectedLanguage})";
        }
        catch (Exception ex) { StatusText = $"✗ {ex.Message}"; }
        finally { IsStreaming = false; }
    }

    // ============== Capability 3: Auto Tag ==============

    [RelayCommand]
    private async Task AutoTagLastAsync(CancellationToken ct)
    {
        var lastUser = History.LastOrDefault(m => m.Role == "user");
        if (lastUser is null) { StatusText = "无内容可打标签"; return; }
        IsStreaming = true;
        StatusText = "提取标签中...";
        try
        {
            var tagger = new AutoTagger(_factory.Get(SelectedProviderId), NullLogger<AutoTagger>.Instance);
            var tags = await tagger.ExtractAsync(lastUser.Content, 5, ct);
            if (tags.Count == 0) { StatusText = "未提取到标签"; return; }
            var formatted = string.Join(", ", tags);
            History.Add(new ChatMessage { Role = "user", Content = "[自动标签] " + lastUser.Content });
            History.Add(new ChatMessage { Role = "assistant", Content = $"🏷️ {formatted}" });
            AssistantReply = formatted;
            StatusText = $"✓ 提取到 {tags.Count} 个标签";
        }
        catch (Exception ex) { StatusText = $"✗ {ex.Message}"; }
        finally { IsStreaming = false; }
    }

    // ============== Capability 4: Semantic Search ==============

    [RelayCommand]
    private async Task SemanticSearchAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(SemanticQuery)) { StatusText = "请输入查询"; return; }
        // 真实实现:从所有笔记中找 top-K
        IsStreaming = true;
        StatusText = "语义搜索中...";
        try
        {
            var noteSvc = App.Services.GetRequiredService<INoteService>();
            var notesRes = await noteSvc.GetAllAsync(false, ct);
            if (!notesRes.IsSuccess || notesRes.Value is null || notesRes.Value.Count == 0)
            {
                StatusText = "无笔记可搜索";
                return;
            }
            var candidates = notesRes.Value
                .Select(n => new SemanticSearcher.NoteCandidate(n.Id, n.Title, n.Content.Length > 200 ? n.Content[..200] : n.Content))
                .ToList();
            // V3.2: prefer real embedding cosine (Ollama / OpenAI HTTP), fall back to LLM rank.
            SemanticSearcher searcher = _embedding is not null
                ? new SemanticSearcher(_factory.Get(SelectedProviderId), NullLogger<SemanticSearcher>.Instance, _embedding, _vectorStore)
                : new SemanticSearcher(_factory.Get(SelectedProviderId), NullLogger<SemanticSearcher>.Instance);
            var scored = await searcher.SearchAsync(SemanticQuery, candidates, 5, ct);
            var sb = new StringBuilder();
            sb.AppendLine($"🔍 找到 {scored.Count} 条相关笔记:");
            foreach (var s in scored) sb.AppendLine($"  • #{s.Id} {s.Title} (score: {s.Score:F1})");
            History.Add(new ChatMessage { Role = "user", Content = "[语义搜索] " + SemanticQuery });
            History.Add(new ChatMessage { Role = "assistant", Content = sb.ToString() });
            AssistantReply = sb.ToString();
            StatusText = $"✓ 找到 {scored.Count} 条";
        }
        catch (Exception ex) { StatusText = $"✗ {ex.Message}"; }
        finally { IsStreaming = false; }
    }

    // ============== Capability 5: Write ==============

    [RelayCommand]
    private async Task WriteAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(WriteTopic)) { StatusText = "请输入写作主题"; return; }
        await RunCapabilityAsync(
            () => new Writer(_factory.Get(SelectedProviderId), NullLogger<Writer>.Instance),
            "write",
            WriteTopic,
            "结构清晰,Markdown 排版",
            ct,
            input => input,
            sb =>
            {
                History.Add(new ChatMessage { Role = "user", Content = "[写作] " + WriteTopic });
                History.Add(new ChatMessage { Role = "assistant", Content = sb.ToString() });
                WriteTopic = string.Empty;
            });
    }

    // ============== Capability 6: QA (RAG) ==============

    [RelayCommand]
    private async Task AnswerFromNotesAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(QaQuestion)) { StatusText = "请输入问题"; return; }
        IsStreaming = true;
        StatusText = "从笔记中找答案...";
        try
        {
            var noteSvc = App.Services.GetRequiredService<INoteService>();
            var searchSvc = App.Services.GetRequiredService<ISearchService>();
            // 1) 全文搜索找候选
            var searchRes = await searchSvc.SearchAsync(QaQuestion, 10, ct);
            var candidates = (searchRes.Value ?? new List<Note>())
                .Select(n => ((long Id, string Title, string Content))(n.Id, n.Title, n.Content))
                .ToList();
            if (candidates.Count == 0) candidates = (await noteSvc.GetAllAsync(false, ct)).Value?
                .Take(5)
                .Select(n => ((long Id, string Title, string Content))(n.Id, n.Title, n.Content))
                .ToList() ?? new();
            var qa = new QuestionAnswerer(_factory.Get(SelectedProviderId), NullLogger<QuestionAnswerer>.Instance);
            var answer = await qa.AnswerAsync(QaQuestion, candidates, ct);
            History.Add(new ChatMessage { Role = "user", Content = "[问答] " + QaQuestion });
            History.Add(new ChatMessage { Role = "assistant", Content = answer });
            AssistantReply = answer;
            QaQuestion = string.Empty;
            StatusText = $"✓ 基于 {candidates.Count} 条笔记回答";
        }
        catch (Exception ex) { StatusText = $"✗ {ex.Message}"; }
        finally { IsStreaming = false; }
    }

    // ============== Skill Execution (V3) ==============

    [RelayCommand]
    private async Task RunSelectedSkillAsync(CancellationToken ct)
    {
        if (SelectedSkill is null || _skills is null) { StatusText = "未选择 Skill"; return; }
        var skill = _skills.Get(SelectedSkill.Id);
        if (skill is null) { StatusText = "Skill 不在 registry"; return; }
        IsStreaming = true;
        StatusText = $"执行 {SelectedSkill.Name}...";
        try
        {
            var input = new SkillInput
            {
                Args = new Dictionary<string, object?> { ["code"] = UserInput, ["feature"] = UserInput, ["symptom"] = UserInput },
                UserMessage = UserInput,
            };
            var ctx = new SkillContext { WorkspaceRoot = Environment.CurrentDirectory, UserMessage = UserInput };
            var result = await skill.ExecuteAsync(input, ctx, ct);
            History.Add(new ChatMessage { Role = "user", Content = $"[Skill: {SelectedSkill.Id}] {UserInput}" });
            History.Add(new ChatMessage { Role = "assistant", Content = result.Text });
            AssistantReply = result.Text;
            StatusText = $"✓ {SelectedSkill.Name} 完成";
            // Record invocation (Hermes pattern)
            _skills.Registry.RecordInvocation(SelectedSkill.Id, result.Success);
        }
        catch (Exception ex) { StatusText = $"✗ {ex.Message}"; }
        finally { IsStreaming = false; }
    }

    // ============== Helper ==============

    private async Task RunCapabilityAsync(
        Func<object> capFactory, string capName, string input, string? hint, CancellationToken ct,
        Func<string, string>? inputTransform = null, Action<StringBuilder>? onResult = null)
    {
        IsStreaming = true;
        StatusText = $"{capName} 中...";
        try
        {
            // Build the request through the capability abstraction
            var req = new ChatRequest
            {
                Model = SelectedModel,
                Messages = new[]
                {
                    new ChatMessage { Role = "system", Content = GetCapabilitySystemPrompt(capName) },
                    new ChatMessage { Role = "user", Content = string.IsNullOrEmpty(hint) ? input : $"[要求] {hint}\n\n[输入]\n{input}" }
                },
                Stream = true,
                EnableThinking = false,
                Temperature = 0.3,
                MaxTokens = 2048
            };
            var provider = _factory.Get(SelectedProviderId);
            var sb = new StringBuilder();
            await foreach (var chunk in provider.StreamChatAsync(req, ct))
            {
                if (chunk.DeltaContent is { Length: > 0 } c) sb.Append(c);
                AssistantReply = sb.ToString();
            }
            onResult?.Invoke(sb);
            StatusText = $"✓ {capName} 完成";
        }
        catch (Exception ex) { StatusText = $"✗ {ex.Message}"; }
        finally { IsStreaming = false; }
    }

    private static string GetCapabilitySystemPrompt(string cap) => cap switch
    {
        "summarize" => "你是一名专业编辑。请压缩文本为简洁摘要,保留核心信息。",
        "write" => "你是资深写作助手。基于主题生成结构化内容,Markdown 排版。",
        _ => "你是 helpful AI 助手。",
    };
}

/// <summary>UI projection of ISkill — minimal projection to avoid pulling Skills into App at design-time.</summary>
public sealed record SkillSummary(string Id, string Name, string Description, string Category, string Version, IReadOnlyList<string> Tags);
