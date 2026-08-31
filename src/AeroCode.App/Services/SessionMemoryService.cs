// Copyright (c) AeroCode
// 批次 B G2-3（builder-δ）：会话记忆服务——语义召回 + 对话沉淀。
// 召回：MEMORY.md/USER.md（文件级长期记忆，其文件头自述"自动注入 system prompt"的既有契约）
//       + 笔记 Top-K 语义召回（复用仓库真实检索栈 SemanticSearcher：真 embedding → LLM rank，
//       两级都不可用则如实跳过并标注——绝不伪造召回结果）。
// 沉淀：复用 Autonomy 经验库 ExperienceStore（不重建）。四型映射的诚实边界：
//       Trajectory（轮轨迹）与 Fact/Method（失败教训经 ExperienceClassifier 确定性分类）真实写入；
//       USER/MEMORY 记忆的自动扩写需要语义判断（无判定模型的提炼=伪造用户长期记忆）→ 显式不自动写，
//       由 Memory 面板人工沉淀按钮承载（用户手写内容 = 真实来源）。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AeroAgent.Autonomy.Learning;
using AeroCode.AI.Capabilities;
using AeroCode.AI.Embedding;
using AeroCode.AI.Providers;
using AeroCode.App.Configuration;
using AeroCode.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AeroCode.App.Services;

/// <summary>一条召回命中的 UI 投影（分数 + 标题 + 预览）。</summary>
public sealed record RecalledNote(long Id, string Title, double Score, string Preview);

/// <summary>一次记忆注入块构建的结果（块文本为空 = 本会话无记忆可注入）。</summary>
public sealed record MemoryBlock(string Text, IReadOnlyList<RecalledNote> Recalled, string? DegradedNote);

/// <summary>
/// 会话记忆服务。全部路径/检索都走真实来源；任何一级失败都降级为"缺该段 + 注记"，
/// 不让记忆故障阻塞对话主链。
/// </summary>
public sealed class SessionMemoryService
{
    /// <summary>召回候选取预览的字符上限（与 AIAssistantViewModel 同口径）。</summary>
    private const int NotePreviewChars = 200;

    private readonly AppDataPaths _paths;
    private readonly INoteService? _notes;
    private readonly IProviderRegistry? _providers;
    private readonly EmbeddingClient? _embedding;
    private readonly ExperienceStore? _experience;
    private readonly MemorySettings _settings;
    private readonly string _defaultProviderId;
    private readonly ILogger<SessionMemoryService> _logger;

    public SessionMemoryService(
        AppDataPaths paths,
        MemorySettings settings,
        INoteService? notes = null,
        IProviderRegistry? providers = null,
        EmbeddingClient? embedding = null,
        ExperienceStore? experience = null,
        string? defaultProviderId = null,
        ILogger<SessionMemoryService>? logger = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _notes = notes;
        _providers = providers;
        _embedding = embedding;
        _experience = experience;
        _defaultProviderId = defaultProviderId ?? string.Empty;
        _logger = logger ?? NullLogger<SessionMemoryService>.Instance;
        Directory.CreateDirectory(paths.MemoryDirectory);
    }

    private string MemoryFile => Path.Combine(_paths.MemoryDirectory, "MEMORY.md");
    private string UserFile => Path.Combine(_paths.MemoryDirectory, "USER.md");

    /// <summary>会话是否处于"首条 system"时机：历史中尚无任何用户消息（steer 插话也是用户消息）。</summary>
    public static bool IsSessionFirstTurn(IReadOnlyList<AeroAgent.Conversation.Models.ChatMessage> history)
        => history.All(m => m.Role != AeroAgent.Conversation.Models.ChatRole.User);

    /// <summary>
    /// 构建注入块。查询文本为空 / 召回关闭 / 笔记服务缺失 → 相应段落如实缺席。
    /// 返回的 Text 为空串表示无可注入（调用方跳过注入，不产生空 system 块）。
    /// </summary>
    public async Task<MemoryBlock> BuildMemoryBlockAsync(string? query, CancellationToken ct = default)
    {
        var memoryMd = ReadIfExists(MemoryFile);
        var userMd = ReadIfExists(UserFile);

        var recalled = new List<RecalledNote>();
        string? degraded = null;
        var topK = Math.Clamp(_settings.RecallTopK, 0, 20);
        if (topK > 0 && !string.IsNullOrWhiteSpace(query) && _notes is not null)
        {
            try
            {
                recalled = await RecallNotesAsync(query!, topK, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // 召回失败不影响注入块其余段落：如实降级并保留 MEMORY/USER 注入。
                degraded = $"笔记召回失败已跳过：{ex.Message}";
                _logger.LogWarning("[DEGRADED] {Note}", degraded);
            }
        }

        var text = ComposeBlock(memoryMd, userMd, recalled);
        return new MemoryBlock(text, recalled, degraded);
    }

    /// <summary>笔记 Top-K 语义召回：真 embedding 余弦（不可用 → LLM rank，语义栈内建降级序）。</summary>
    private async Task<List<RecalledNote>> RecallNotesAsync(string query, int topK, CancellationToken ct)
    {
        var notesResult = await _notes!.GetAllAsync(ct: ct).ConfigureAwait(false);
        if (!notesResult.IsSuccess || notesResult.Value is null || notesResult.Value.Count == 0)
        {
            return new List<RecalledNote>();
        }

        var candidates = notesResult.Value
            .Select(n => new SemanticSearcher.NoteCandidate(
                n.Id, n.Title, n.Content.Length > NotePreviewChars ? n.Content[..NotePreviewChars] : n.Content))
            .ToList();

        var provider = ResolveProvider();
        if (provider is null)
        {
            throw new InvalidOperationException("无已配置 provider，语义召回不可用");
        }

        var searcher = _embedding is not null
            ? new SemanticSearcher(provider, NullLogger<SemanticSearcher>.Instance, _embedding, null)
            : new SemanticSearcher(provider, NullLogger<SemanticSearcher>.Instance);

        // 独立短超时：召回卡死不能拖住首条消息的发送链。
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));
        var hits = await searcher.SearchAsync(query, candidates, topK, timeoutCts.Token).ConfigureAwait(false);

        var previewById = notesResult.Value.ToDictionary(n => n.Id, n => n.Content);
        var titleById = notesResult.Value.ToDictionary(n => n.Id, n => n.Title);
        return hits
            .Select(h => new RecalledNote(
                h.Id,
                titleById.TryGetValue(h.Id, out var t) ? t : $"#{h.Id}",
                h.Score,
                previewById.TryGetValue(h.Id, out var c)
                    ? (c.Length > NotePreviewChars ? c[..NotePreviewChars] : c)
                    : string.Empty))
            .ToList();
    }

    private AeroCode.AI.Providers.IAiProvider? ResolveProvider()
    {
        if (_providers is null)
        {
            return null;
        }

        try
        {
            return _providers.Get(_defaultProviderId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[DEGRADED] 默认 provider '{Id}' 不可用，语义召回跳过：{Error}",
                _defaultProviderId, ex.Message);
            return null;
        }
    }

    /// <summary>纯函数组装（注入点单测钉住格式契约）：空段省略，全空 = 空串（不注入空块）。</summary>
    public static string ComposeBlock(
        string? memoryMd, string? userMd, IReadOnlyList<RecalledNote> recalled)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<memory-context>");
        var hasAny = false;
        if (!string.IsNullOrWhiteSpace(memoryMd))
        {
            sb.AppendLine("[长期记忆 MEMORY.md]");
            sb.AppendLine(memoryMd.TrimEnd());
            hasAny = true;
        }

        if (!string.IsNullOrWhiteSpace(userMd))
        {
            sb.AppendLine("[用户画像 USER.md]");
            sb.AppendLine(userMd.TrimEnd());
            hasAny = true;
        }

        if (recalled.Count > 0)
        {
            sb.AppendLine("[相关笔记 Top-K]");
            foreach (var note in recalled)
            {
                sb.Append($"- ({note.Score:F2}) {note.Title}");
                if (!string.IsNullOrWhiteSpace(note.Preview))
                {
                    sb.Append($"：{note.Preview.ReplaceLineEndings(" ")}");
                }

                sb.AppendLine();
            }

            hasAny = true;
        }

        sb.AppendLine("</memory-context>");
        return hasAny ? sb.ToString() : string.Empty;
    }

    /// <summary>
    /// 对话沉淀（真实写入 ExperienceStore）：
    /// 1) Trajectory：本轮真实轨迹（任务/回复/成本/成败，全部事实字段）；
    /// 2) 失败轮：错误文本作为缺口，经 ExperienceClassifier 确定性分类（环境关键词→Fact，否则 Trajectory）。
    /// 返回给 UI 的沉淀摘要（含诚实边界说明）。
    /// </summary>
    public async Task<string> ConsolidateTurnAsync(
        string sessionId,
        string userText,
        string? assistantText,
        bool succeeded,
        double costUsd,
        CancellationToken ct = default)
    {
        if (_experience is null)
        {
            return "[DEGRADED] 经验库未装配，本轮未沉淀";
        }

        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var summary = new StringBuilder();
        try
        {
            var trajectory =
                $"任务：{Truncate(userText, 200)}\n" +
                $"回复：{Truncate(assistantText ?? string.Empty, 200)}\n" +
                $"结果：{(succeeded ? "成功" : "失败")} · 成本 ${costUsd:F4} · 会话 {Truncate(sessionId, 12)}";
            var trajectoryResult = await _experience.AddAsync(
                ExperienceKind.Trajectory,
                title: $"会话轮迹 {stamp}",
                content: trajectory,
                sourceKey: $"turn:{sessionId}:{stamp}",
                sourcePhase: "Conversation",
                tags: new[] { "conversation" },
                ct: ct).ConfigureAwait(false);
            summary.Append(trajectoryResult.CreatedNew ? "轨迹已沉淀" : "轨迹已存在（幂等跳过）");

            if (!succeeded && !string.IsNullOrWhiteSpace(assistantText))
            {
                // 失败轮：真实错误文本作为缺口；分类是确定性契约（环境关键词→Fact，否则 Trajectory）。
                var lesson = new AeroAgent.Autonomy.Data.LessonRecord
                {
                    MissionId = sessionId,
                    Phase = "Conversation",
                    Gap = Truncate(assistantText, 400),
                    Suggestion = string.Empty,
                    Severity = "warning",
                };
                var kind = ExperienceClassifier.Classify(lesson);
                var failureResult = await _experience.AddAsync(
                    kind,
                    title: $"对话失败教训 {stamp}",
                    content: lesson.Gap,
                    sourceKey: $"turnfail:{sessionId}:{stamp}",
                    sourceMissionId: sessionId,
                    sourcePhase: "Conversation",
                    tags: new[] { "conversation", "failure" },
                    ct: ct).ConfigureAwait(false);
                summary.Append($"；失败教训已沉淀（{kind}{(failureResult.CreatedNew ? string.Empty : "，幂等跳过")}）");
            }

            // 四型诚实边界：Method（可执行建议）与 USER/MEMORY 记忆的自动扩写需要语义判断；
            // 无判定模型的自动提炼等于伪造——不写，由 Memory 面板人工沉淀按钮承载。
            summary.Append("；Method/USER/MEMORY 自动扩写需语义判定，未自动写（人工沉淀入口在 Memory 面板）");
            return summary.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[DEGRADED] 会话经验沉淀失败（不影响对话主链）：{Error}", ex.Message);
            return $"[DEGRADED] 沉淀失败：{ex.Message}";
        }
    }

    /// <summary>人工沉淀：把用户在 Memory 面板确认的手写内容作为 Fact 经验真实入库（来源=用户，非模型生成）。</summary>
    public async Task<string> ConsolidateManualAsync(string title, string content, CancellationToken ct = default)
    {
        if (_experience is null)
        {
            return "[DEGRADED] 经验库未装配，未沉淀";
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            return "内容为空，未沉淀（不写空经验）";
        }

        var result = await _experience.AddAsync(
            ExperienceKind.Fact,
            title: string.IsNullOrWhiteSpace(title) ? $"人工沉淀 {DateTime.UtcNow:yyyyMMdd-HHmmss}" : title.Trim(),
            content: content.Trim(),
            sourceKey: $"manual:{Guid.NewGuid():N}",
            sourcePhase: "MemoryPanel",
            tags: new[] { "manual" },
            ct: ct).ConfigureAwait(false);
        return result.CreatedNew
            ? $"已沉淀为 Fact 经验（#{result.Entry.Id[..Math.Min(8, result.Entry.Id.Length)]}，Pending→下轮生效）"
            : "相同来源的经验已存在（幂等跳过）";
    }

    private string? ReadIfExists(string file)
    {
        try
        {
            return File.Exists(file) ? File.ReadAllText(file) : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[DEGRADED] 记忆文件读取失败 '{File}'：{Error}", file, ex.Message);
            return null;
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
