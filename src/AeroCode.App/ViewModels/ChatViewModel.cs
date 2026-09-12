using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AeroAgent.Conversation.Models;
using AeroAgent.Conversation.Orchestration;
using AeroAgent.Conversation.Services;
using AeroAgent.Moa.Gateway;
using AeroAgent.Moa.Safety;
using AeroAgent.Moa.Strategies;
using AeroAgent.Moa.Tools.Workspace;
using AeroCode.AI.Providers;
using AeroCode.App.Services;
using AeroCode.Harness.EventBus;
using AeroCode.Harness.Permission;
using AeroCode.Harness.PlanMode;
using Avalonia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AeroCode.App.ViewModels;

/// <summary>会话列表项。</summary>
public partial class SessionItemViewModel : ObservableObject
{
    public string Id { get; init; } = string.Empty;

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private bool _isPinned;

    [ObservableProperty]
    private OrchestrationStrategy _strategy;

    /// <summary>会话级 provider 偏好（空 = 全局默认）。</summary>
    public string? PreferredProviderId { get; init; }

    /// <summary>会话级模型偏好（空 = provider 默认）。</summary>
    public string? PreferredModel { get; init; }

    [ObservableProperty]
    private DateTime _updatedAtUtc;

    public string DisplayTime => UpdatedAtUtc.ToLocalTime().ToString("MM-dd HH:mm");

    partial void OnUpdatedAtUtcChanged(DateTime value) => OnPropertyChanged(nameof(DisplayTime));
}

/// <summary>消息渲染项。流式过程中 Content 持续增长。</summary>
public partial class MessageItemViewModel : ObservableObject
{
    public string Id { get; init; } = string.Empty;
    public ChatRole Role { get; init; }
    public bool IsUser => Role == ChatRole.User;
    public bool IsAssistant => Role == ChatRole.Assistant;

    /// <summary>工具结果消息（紧凑行渲染，区别于助手气泡）。</summary>
    public bool IsTool => Role == ChatRole.Tool;

    /// <summary>工具名（工具结果消息）。</summary>
    public string? ToolName { get; init; }

    /// <summary>工具调用 Id（工具结果消息，与上游 tool_calls 配对）。</summary>
    public string? ToolCallId { get; init; }

    /// <summary>模型给出的参数 JSON（工具执行中/完成后展示"模型要干什么"）。</summary>
    public string? ToolArguments { get; init; }

    [ObservableProperty]
    private string _content = string.Empty;

    [ObservableProperty]
    private string? _reasoningContent;

    [ObservableProperty]
    private MessageStatus _status = MessageStatus.Completed;

    [ObservableProperty]
    private string? _errorText;

    /// <summary>附件摘要文本（用户消息气泡底部，如"📎 photo.png (2.3MB)"）。</summary>
    [ObservableProperty]
    private string? _attachmentSummary;

    /// <summary>附件缩略图字节（PNG/JPEG 前 4KB 预览，R4-γ 最小实现暂不渲染）。</summary>
    [ObservableProperty]
    private byte[]? _attachmentPreview;

    [ObservableProperty]
    private int _tokensIn;

    [ObservableProperty]
    private int _tokensOut;

    [ObservableProperty]
    private double _costUsd;

    /// <summary>该助手消息是否携带工具调用（工具循环中间轮；正文可能为空）。</summary>
    [ObservableProperty]
    private bool _hasToolCalls;

    /// <summary>工具结果是否被授权策略拒绝（区别于执行失败）。</summary>
    [ObservableProperty]
    private bool _toolDenied;

    public string? ProviderId { get; init; }
    public string? ModelId { get; init; }
    public StrategyRole OrchestrationRole { get; init; }

    /// <summary>父消息 Id（MOA 归属树），顶层为 null。</summary>
    public string? ParentMessageId { get; init; }

    /// <summary>编排子任务标签（如"候选 A"/planner 分配的子任务名）。</summary>
    public string? Label { get; init; }

    /// <summary>归属树深度的左缩进（父级在上方时按父深度 +1 计算）。
    /// 流式期间父消息可能晚于子消息到达、缩进需要回填，必须可通知。</summary>
    [ObservableProperty]
    private Thickness _indentMargin;

    /// <summary>模型归属徽章文本（模型名；编排角色非 None 时附加角色）。</summary>
    public string? AttributionBadge
    {
        get
        {
            if (ModelId is null)
            {
                return null;
            }

            return OrchestrationRole == StrategyRole.None
                ? ModelId
                : $"{ModelId} · {RoleName(OrchestrationRole)}";
        }
    }

    /// <summary>工具结果徽章（"工具 · get_note"）。</summary>
    public string? ToolBadge => IsTool ? $"工具 · {ToolName}" : null;

    /// <summary>助手工具调用轮徽章（正文为空的中间轮也要可见）。</summary>
    public string? ToolCallsBadge => HasToolCalls ? "工具调用" : null;

    /// <summary>成本徽章（未计价不显示——不猜）。</summary>
    public string? CostBadge => CostUsd > 0 ? $"${CostUsd:F4}" : null;

    /// <summary>用量徽章（真实 usage 未报不显示）。</summary>
    public string? UsageBadge => TokensIn > 0 || TokensOut > 0 ? $"{TokensIn}→{TokensOut} tok" : null;

    /// <summary>非完成态的状态角标（降级/拒绝/失败/取消/流式中）。</summary>
    public string? StatusGlyph => Status switch
    {
        MessageStatus.Degraded => ToolDenied ? "已拒绝" : "降级",
        MessageStatus.Failed => "失败",
        MessageStatus.Cancelled => "已停止",
        MessageStatus.Streaming => "…",
        _ => null,
    };

    private static string RoleName(StrategyRole role) => role switch
    {
        StrategyRole.Router => "路由",
        StrategyRole.Planner => "规划",
        StrategyRole.Worker => "执行",
        StrategyRole.Judge => "评审",
        StrategyRole.Synthesizer => "汇总",
        _ => string.Empty,
    };

    partial void OnCostUsdChanged(double value) => OnPropertyChanged(nameof(CostBadge));
    partial void OnTokensInChanged(int value) => OnPropertyChanged(nameof(UsageBadge));
    partial void OnTokensOutChanged(int value) => OnPropertyChanged(nameof(UsageBadge));
    partial void OnStatusChanged(MessageStatus value) => OnPropertyChanged(nameof(StatusGlyph));
    partial void OnToolDeniedChanged(bool value) => OnPropertyChanged(nameof(StatusGlyph));
    partial void OnHasToolCallsChanged(bool value) => OnPropertyChanged(nameof(ToolCallsBadge));
}

/// <summary>todo 清单面板的一行（G5）：真实 TodoItem 投影 + 完成态切换/删除。</summary>
public partial class TodoItemViewModel : ObservableObject
{
    public string Id { get; }
    public int Position { get; }

    [ObservableProperty]
    private string _content;

    [ObservableProperty]
    private bool _isCompleted;

    public TodoItemViewModel(TodoItem item)
    {
        Id = item.Id;
        Position = item.Position;
        _content = item.Content;
        _isCompleted = item.IsCompleted;
    }
}

/// <summary>
/// 统一对话视图模型：会话 CRUD + 流式对话 + 事件流消费。
/// 持久化全部由 <see cref="IChatOrchestrationFacade"/> 负责，
/// 本 VM 只做 UI 投影（Dispatcher 保证 UI 线程安全）。
/// 事件路由按 MessageId 精确定位气泡——MOA 并行编排下多条消息
/// 交错产出，任何"当前消息"单指针都会串线。
/// 批次 B G5 接线：会话 fork / Steer 插话 / todo 清单 / 首轮记忆注入 / 熔断成本累计。
/// </summary>
public partial class ChatViewModel : ObservableObject
{
    private readonly ISessionService _sessions;
    private readonly IChatOrchestrationFacade _facade;
    private readonly IProviderRegistry _providers;
    private readonly MoaOptions _moaOptions;
    private readonly PermissionPolicy _permission;
    private readonly WorkspaceContext? _workspace;
    private readonly PlanWorkflow? _planWorkflow;
    private readonly EventBus? _events;
    private readonly SteerQueue? _steer;
    private readonly ISessionFork? _fork;
    private readonly ITodoStore? _todos;
    private readonly SessionMemoryService? _memory;
    private readonly ApprovalCircuitBreaker? _approvalBreaker;
    private readonly MoaGatewayClient? _gateway;

    private CancellationTokenSource? _streamCts;
    private bool _suppressStrategySync;
    private bool _suppressSessionLoad;

    /// <summary>当前轮是否出现过失败/取消事件（沉淀时如实标注轮成败；轮末消费后复位）。</summary>
    private bool _turnFailed;

    public ChatViewModel(
        ISessionService sessions,
        IChatOrchestrationFacade facade,
        IProviderRegistry providers,
        MoaOptions moaOptions,
        PermissionPolicy permission,
        WorkspaceContext? workspace = null,
        PlanWorkflow? planWorkflow = null,
        EventBus? eventBus = null,
        SteerQueue? steerQueue = null,
        ISessionFork? sessionFork = null,
        ITodoStore? todoStore = null,
        SessionMemoryService? memory = null,
        ApprovalCircuitBreaker? approvalBreaker = null,
        MoaGatewayClient? gateway = null)
    {
        _sessions = sessions;
        _facade = facade;
        _providers = providers;
        _moaOptions = moaOptions ?? throw new ArgumentNullException(nameof(moaOptions));
        _permission = permission ?? throw new ArgumentNullException(nameof(permission));
        _workspace = workspace;
        _planWorkflow = planWorkflow;
        _events = eventBus;
        _steer = steerQueue;
        _fork = sessionFork;
        _todos = todoStore;
        _memory = memory;
        _approvalBreaker = approvalBreaker;
        _gateway = gateway;

        ProviderIds = new ObservableCollection<string>(providers.ListConfiguredIds());
        _selectedProviderId = ProviderIds.FirstOrDefault() ?? string.Empty;
        Strategies = new ObservableCollection<OrchestrationStrategy>(
            Enum.GetValues<OrchestrationStrategy>());
        // 新会话默认策略：从 MOA 选项取（设置页可改），工具条仍可逐会话覆盖。
        _selectedStrategy = moaOptions.DefaultStrategy;
        // 档位下拉与裁决源同步初始值（直接写字段：构造期不走 ApplyPermissionMode）。
        _selectedMode = permission.CurrentMode;
        // 专家团网关徽标（G5/#68）：初始为环境变量配置态（字段初始化器），
        // 视图加载/选中专家团时经 RefreshGatewayStatusAsync 真实探活刷新。

        // 热重载链：设置保存 → ProviderFactory.Reload → ProvidersChanged → 下拉就地刷新。
        // 本 VM 与应用同生命周期（DI 单例），订阅无需退订。
        _providers.ProvidersChanged += OnProvidersChanged;
        // MOA 选项保存 → OptionsChanged → 无选中会话时刷新"新会话将使用的策略"。
        _moaOptions.OptionsChanged += OnMoaOptionsChanged;

        // UIR-5：对话指令队列 —— 执行体为本页 SendAsync。review M3：直接把队列条目令牌传入
        // SendAsync（其内部 _streamCts 联动该令牌，停止在前导/流式各阶段均生效）；
        // review M1：返回 executed/refused 供引擎区分"执行"与"拒绝"；
        // review L2：text 经 textOverride 直传，不再覆写用户正在编辑的输入框草稿。
        Queue = new CommandQueueEngine(
            async (text, ct) => await SendAsync(ct, text),
            () => !IsStreaming);
    }

    private void OnMoaOptionsChanged()
    {
        try
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                ApplyDefaultStrategy();
                return;
            }

            Dispatcher.UIThread.Post(ApplyDefaultStrategy);
        }
        catch (InvalidOperationException)
        {
            // 无 UI 调度器（无头测试环境）：就地应用。
            ApplyDefaultStrategy();
        }
    }

    /// <summary>
    /// 默认策略只作用于"下一个新会话"：选中会话时工具条下拉与该会话策略同步，
    /// 代表的是会话自身选择，不得被设置页改写；仅在无选中会话（下拉即新会话默认值）
    /// 且非流式中时应用。
    /// </summary>
    private void ApplyDefaultStrategy()
    {
        if (SelectedSession is not null || IsStreaming)
        {
            return;
        }

        SelectedStrategy = _moaOptions.DefaultStrategy;
    }

    private void OnProvidersChanged()
    {
        var ids = _providers.ListConfiguredIds().ToList();
        try
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                ApplyProviderIds(ids);
                return;
            }

            Dispatcher.UIThread.Post(() => ApplyProviderIds(ids));
        }
        catch (InvalidOperationException)
        {
            // 无 UI 调度器（无头测试环境）：就地应用。
            // 生产路径恒为设置页 UI 线程保存触发，CheckAccess 必真。
            ApplyProviderIds(ids);
        }
    }

    /// <summary>就地刷新 provider 下拉：仍存在的选中项保留，否则回退第一个（无则空串）。</summary>
    private void ApplyProviderIds(IReadOnlyList<string> ids)
    {
        var current = SelectedProviderId;
        ProviderIds.Clear();
        foreach (var id in ids)
        {
            ProviderIds.Add(id);
        }

        SelectedProviderId = ids.Contains(current) ? current : ids.FirstOrDefault() ?? string.Empty;
    }

    public ObservableCollection<SessionItemViewModel> Sessions { get; } = new();
    public ObservableCollection<MessageItemViewModel> Messages { get; } = new();
    public ObservableCollection<string> ProviderIds { get; }
    public ObservableCollection<OrchestrationStrategy> Strategies { get; }

    [ObservableProperty]
    private SessionItemViewModel? _selectedSession;

    [ObservableProperty]
    private string _inputText = string.Empty;

    [ObservableProperty]
    private bool _isStreaming;

    /// <summary>对话指令队列引擎（UIR-5 铺全输入框；执行体为本页 SendAsync，构造函数注入）。</summary>
    public CommandQueueEngine Queue { get; }

    [ObservableProperty]
    private string _selectedProviderId;

    [ObservableProperty]
    private OrchestrationStrategy _selectedStrategy = OrchestrationStrategy.Single;

    [ObservableProperty]
    private string _statusText = "就绪";

    /// <summary>Steer 插话输入（流式进行中可见；经 SteerQueue 下一轮注入）。</summary>
    [ObservableProperty]
    private string _steerInput = string.Empty;

    /// <summary>当前会话的 todo 清单（G5 面板；经 ITodoStore 真实读写）。</summary>
    public ObservableCollection<TodoItemViewModel> Todos { get; } = new();

    /// <summary>待发送附件列表（R5.3：任意类型，选中后显示预览条，发送时传给门面分块注入）。</summary>
    public ObservableCollection<MessageAttachment> PendingAttachments { get; } = new();

    // ── R5.3 多附件上限与文本抽取预算 ──
    /// <summary>单条消息最多附件数量。</summary>
    private const int MaxAttachmentCount = 100;

    /// <summary>单条消息附件总大小上限（10GB）。</summary>
    private const long MaxAttachmentTotalBytes = 10L * 1024 * 1024 * 1024;

    /// <summary>单个文本类附件抽取正文的字符预算（超出标注截断）。</summary>
    private const int PerFileTextReadBudgetChars = 128 * 1024;

    /// <summary>R5.3：打开文件选择器添加附件（任意类型；上限 100 个 / 合计 10GB）。</summary>
    [RelayCommand]
    private async Task AttachFileAsync()
    {
        if (IsStreaming)
        {
            return;
        }

        var lifetime = Application.Current?.ApplicationLifetime
            as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
        if (lifetime?.MainWindow is null)
        {
            return;
        }

        var files = await lifetime.MainWindow.StorageProvider.OpenFilePickerAsync(
            new Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                Title = $"选择附件（任意类型，最多 {MaxAttachmentCount} 个 / 合计 10GB）",
                AllowMultiple = true,
                // 不限定类型：接受任意文件，MIME 按扩展名推断。
            });

        // review I-1：先用文件元信息做上限筛选（不读内容、无 await），再一次性后台构建，
        // 最后同步添加——避免逐文件 await 期间 Send/粘贴插入导致上限超限或附件集被拆分。
        var existingBytes = PendingAttachments.Sum(a => a.SizeBytes);
        var accepted = new List<FileInfo>();
        var batchBytes = 0L;
        foreach (var file in files)
        {
            var path = file.Path.LocalPath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                continue;
            }

            // 上限：数量（已有 + 本批已接受）。
            if (PendingAttachments.Count + accepted.Count >= MaxAttachmentCount)
            {
                StatusText = $"已达附件数量上限（{MaxAttachmentCount} 个），其余未添加";
                break;
            }

            var info = new FileInfo(path);

            // 上限：总大小（已有 + 本批已接受 + 当前文件）。
            if (existingBytes + batchBytes + info.Length > MaxAttachmentTotalBytes)
            {
                StatusText = $"附件总大小将超 10GB，{info.Name} 未添加";
                continue;
            }

            accepted.Add(info);
            batchBytes += info.Length;
        }

        if (accepted.Count > 0)
        {
            // review M6：文件读取/文本解码挪到后台线程（一次性批量构建，单文件失败相互隔离）。
            var built = await Task.Run(() =>
            {
                var results = new List<(MessageAttachment? Attachment, string? Error)>(accepted.Count);
                foreach (var info in accepted)
                {
                    try
                    {
                        results.Add((BuildAttachment(info), null));
                    }
                    catch (Exception ex)
                    {
                        results.Add((null, $"附件 {info.Name} 读取失败：{ex.Message}"));
                    }
                }

                return results;
            });

            // 同步添加：上限判定与添加之间无 await，杜绝超限窗口。
            foreach (var item in built)
            {
                if (item.Attachment is not null)
                {
                    PendingAttachments.Add(item.Attachment);
                    StatusText = $"已添加附件 {item.Attachment.FileName}（共 {PendingAttachments.Count} 个）";
                }
                else if (item.Error is not null)
                {
                    // 单文件失败不影响其他：如实报告。
                    StatusText = item.Error;
                }
            }
        }
    }

    /// <summary>
    /// 由单个文件构建附件：推断 MIME、读前 4KB 预览占位、对文本类文件按单文件预算抽取正文
    /// （超出标注截断）。抽成独立方法便于真实文件的单元测试。
    /// </summary>
    internal static MessageAttachment BuildAttachment(FileInfo info)
    {
        var ext = info.Extension.TrimStart('.').ToLowerInvariant();
        var mime = GuessMime(ext);

        byte[]? preview = null;
        if (info.Length > 0)
        {
            var read = (int)Math.Min(info.Length, 4096);
            preview = new byte[read];
            using var fs = info.OpenRead();
            fs.ReadExactly(preview.AsSpan());
        }

        string? textContent = null;
        var truncated = false;
        var extractionFailed = false; // review L3：文本抽取失败标记（区别于本就是二进制）。
        if (info.Length > 0 && IsTextLike(ext))
        {
            try
            {
                using var reader = info.OpenText();
                var buf = new char[PerFileTextReadBudgetChars + 1];
                var n = reader.Read(buf.AsSpan());
                truncated = n > PerFileTextReadBudgetChars;
                textContent = new string(buf, 0, Math.Min(n, PerFileTextReadBudgetChars));
            }
            catch
            {
                // 抽取失败（编码/锁等）：降级为仅元信息，不阻塞附加。
                textContent = null;
                truncated = false;
                extractionFailed = true;
            }
        }

        return new MessageAttachment(info.Name, mime, info.Length, preview, textContent, truncated)
        {
            SourcePath = info.FullName,
            ExtractionFailed = extractionFailed,
        };
    }

    /// <summary>按扩展名推断 MIME（未知回落 application/octet-stream）。</summary>
    private static string GuessMime(string ext) => ext switch
    {
        "png" => "image/png",
        "jpg" or "jpeg" => "image/jpeg",
        "gif" => "image/gif",
        "webp" => "image/webp",
        "bmp" => "image/bmp",
        "svg" => "image/svg+xml",
        "pdf" => "application/pdf",
        "txt" or "log" or "ini" or "cfg" or "conf" => "text/plain",
        "md" or "markdown" => "text/markdown",
        "csv" => "text/csv",
        "html" or "htm" => "text/html",
        "css" => "text/css",
        "json" => "application/json",
        "xml" => "application/xml",
        "yaml" or "yml" => "text/yaml",
        "zip" => "application/zip",
        _ => IsTextLike(ext) ? "text/plain" : "application/octet-stream",
    };

    /// <summary>扩展名是否视为可抽取正文的文本类文件。</summary>
    private static bool IsTextLike(string ext) => ext switch
    {
        "txt" or "md" or "markdown" or "log" or "csv" or "tsv" or "ini" or "cfg" or "conf"
            or "toml" or "env" or "json" or "xml" or "yaml" or "yml" or "html" or "htm"
            or "css" or "scss" or "js" or "mjs" or "ts" or "tsx" or "jsx" or "cs" or "java"
            or "py" or "rb" or "php" or "go" or "rs" or "c" or "h" or "cpp" or "hpp" or "cc"
            or "swift" or "kt" or "sql" or "sh" or "bash" or "bat" or "ps1" or "gitignore"
            or "dockerfile" or "makefile" => true,
        _ => false,
    };

    /// <summary>R4-γ：从待发送列表移除一个附件。</summary>
    [RelayCommand]
    private void RemoveAttachment(MessageAttachment? attachment)
    {
        if (attachment is not null)
        {
            PendingAttachments.Remove(attachment);
        }
    }

    /// <summary>R4-γ：从剪贴板粘贴图片附件（code-behind 调用）。</summary>
    public void AttachFromClipboard(byte[] data, string fileName, string mimeType)
    {
        if (IsStreaming || data.Length == 0)
        {
            return;
        }

        // review M5：剪贴板粘贴与文件选择器同口径受 100 个 / 10GB 上限约束，
        // 否则"单条消息附件永不超限"的不变量可被粘贴绕过。
        if (PendingAttachments.Count >= MaxAttachmentCount)
        {
            StatusText = $"已达附件数量上限（{MaxAttachmentCount} 个），粘贴未添加";
            return;
        }

        var currentTotal = PendingAttachments.Sum(a => a.SizeBytes);
        if (currentTotal + data.Length > MaxAttachmentTotalBytes)
        {
            StatusText = "附件总大小将超 10GB，粘贴未添加";
            return;
        }

        var preview = data.Length > 4096 ? data.AsSpan(0, 4096).ToArray() : data;
        PendingAttachments.Add(new MessageAttachment(fileName, mimeType, data.Length, preview));
    }

    /// <summary>权限档位下拉数据源（顺序即枚举定义序：Default→AcceptEdits→Plan→Bypass）。</summary>
    public IReadOnlyList<PermissionMode> PermissionModes { get; } =
        new[] { PermissionMode.Default, PermissionMode.AcceptEdits, PermissionMode.Plan, PermissionMode.Bypass };

    [ObservableProperty]
    private PermissionMode _selectedMode;

    /// <summary>当前档位说明（档位下拉旁常显文本，语义与 PermissionModeTransform 对齐）。</summary>
    public string ModeDescription => SelectedMode switch
    {
        PermissionMode.AcceptEdits => "自动接受文件编辑",
        PermissionMode.Plan => "规划（只读 + 计划）",
        PermissionMode.Bypass => "跳过询问（显式拒绝/危险命令仍拦截）",
        _ => "默认（写/执行需确认）",
    };

    /// <summary>专家团策略是否选中（策略下拉旁网关提示的可见性，G5）。</summary>
    public bool IsExpertsSelected => SelectedStrategy == OrchestrationStrategy.Experts;

    /// <summary>
    /// 专家团网关状态徽标文本（G5/#68，诚实展示）。初始为环境变量配置态
    /// （<see cref="BuildExpertsGatewayHint"/>），视图加载/选中专家团后经
    /// <see cref="RefreshGatewayStatusAsync"/> 真实探活刷新为在线/不可达。
    /// </summary>
    [ObservableProperty]
    private string _expertsGatewayHint = BuildExpertsGatewayHint();

    /// <summary>网关是否可达（真实探活结果，驱动徽标点着色；初始 false = 未证实连通）。</summary>
    [ObservableProperty]
    private bool _gatewayReachable;

    private static string BuildExpertsGatewayHint()
    {
        var url = Environment.GetEnvironmentVariable("MOA_GATEWAY_URL");
        var key = Environment.GetEnvironmentVariable("MOA_GATEWAY_KEY");
        var hasUrl = !string.IsNullOrWhiteSpace(url);
        var hasKey = !string.IsNullOrWhiteSpace(key);
        if (!hasUrl && !hasKey)
        {
            return "⚠ 专家团经 moa-gateway-pro 网关：未检测到 MOA_GATEWAY_URL / MOA_GATEWAY_KEY"
                   + "（默认探 http://127.0.0.1:8910），网关未运行时该轮将诚实失败";
        }

        var config = hasUrl ? url! : "默认 http://127.0.0.1:8910";
        return hasKey
            ? $"专家团经 moa-gateway-pro 网关：{config}（已配置 KEY）；网关不可达时该轮诚实失败"
            : $"专家团经 moa-gateway-pro 网关：{config}（未设 KEY，仅健康探活可用）；网关不可达时该轮诚实失败";
    }

    /// <summary>探活代号（review M1）：每次探活自增，await 返回后若已有更新的探活则丢弃本次过期结果。</summary>
    private int _probeGeneration;

    /// <summary>
    /// 真实探活网关并刷新徽标（#68）。成功 → 展示版本/端点/mock 可见性；失败 → 明说不可达与原因。
    /// 未注入 _gateway（手动构造/测试）→ 回落环境变量配置态，绝不伪造连通。
    /// 序列化（review M1）：视图加载与选中专家团都会即发即忘触发探活，慢速/超时的旧探活
    /// 不得覆盖新探活写入的状态——以 <c>_probeGeneration</c> 代号判定，await 返回后若已有
    /// 更新的探活则丢弃本次过期结果。
    /// </summary>
    public async Task RefreshGatewayStatusAsync()
    {
        var generation = Interlocked.Increment(ref _probeGeneration);

        if (_gateway is null)
        {
            ExpertsGatewayHint = BuildExpertsGatewayHint();
            GatewayReachable = false;
            return;
        }

        GatewayResult<MoaGatewayHealth> health;
        try
        {
            health = await _gateway.HealthAsync();
        }
        catch (Exception ex) // 未传调用方令牌，HealthAsync 不抛 OCE；其余异常一律收敛为不可达
        {
            if (generation != Volatile.Read(ref _probeGeneration)) return; // 已被更新的探活取代
            GatewayReachable = false;
            ExpertsGatewayHint = $"网关探活异常：{ex.Message} —— 专家团该轮将诚实失败";
            return;
        }

        if (generation != Volatile.Read(ref _probeGeneration)) return; // 已被更新的探活取代，丢弃过期结果

        if (health.IsSuccess && health.Value is { } h)
        {
            GatewayReachable = true;
            ExpertsGatewayHint = $"网关在线 v{h.Version} · 端点 {h.EndpointsEnabled}/{h.EndpointsTotal}"
                + $" · mock {h.MockEndpointsCount}（mode={h.MockMode}）· 专家团经此网关";
        }
        else
        {
            GatewayReachable = false;
            ExpertsGatewayHint = $"网关不可达（{_gateway.Options.BaseUrl}）：{health.Error} —— 专家团该轮将诚实失败";
        }
    }

    partial void OnSelectedModeChanged(PermissionMode value) => ApplyPermissionMode(value);

    /// <summary>视图加载时调用：拉取会话列表，并顺带探活网关徽标（#68，不阻塞加载）。</summary>
    public async Task InitializeAsync()
    {
        await ReloadSessionsAsync();
        // 探活失败会自行收敛为"不可达"文案，不抛异常，可安全即发即忘。
        _ = RefreshGatewayStatusAsync();
    }

    private async Task ReloadSessionsAsync()
    {
        var result = await _sessions.ListSessionsAsync();
        if (!result.IsSuccess || result.Value is null)
        {
            StatusText = $"会话列表加载失败：{result.Error}";
            return;
        }

        // 就地更新：同 Id 的会话项保留原实例、只刷可变字段。
        // 若整体替换实例，SelectedSession 每轮都会变成新对象 →
        // OnSelectedSessionChanged 触发消息全量重载：UI 闪烁，且
        // ReasoningContent 等实时态（未持久化）会被重载冲掉。
        var selected = SelectedSession; // Clear() 会经列表双向绑定把选中项写回 null，先捕获
        var existingById = Sessions.ToDictionary(s => s.Id, StringComparer.Ordinal);
        var items = new List<SessionItemViewModel>(result.Value.Count);
        foreach (var s in result.Value)
        {
            SessionItemViewModel item;
            if (existingById.TryGetValue(s.Id, out var existing))
            {
                existing.Title = s.Title;
                existing.IsPinned = s.IsPinned;
                existing.Strategy = s.Strategy;
                existing.UpdatedAtUtc = s.UpdatedAtUtc;
                item = existing;
            }
            else
            {
                item = new SessionItemViewModel
                {
                    Id = s.Id,
                    Title = s.Title,
                    IsPinned = s.IsPinned,
                    Strategy = s.Strategy,
                    PreferredProviderId = s.PreferredProviderId,
                    PreferredModel = s.PreferredModel,
                    UpdatedAtUtc = s.UpdatedAtUtc,
                };
            }

            items.Add(item);
        }

        Sessions.Clear();
        foreach (var item in items)
        {
            Sessions.Add(item);
        }

        if (selected is not null && items.Contains(selected))
        {
            // 选中会话仍在列：恢复被绑定清空前的选中实例。
            // 同一实例无需再同步策略/重载消息——压制 OnSelectedSessionChanged。
            if (!ReferenceEquals(SelectedSession, selected))
            {
                _suppressSessionLoad = true;
                try
                {
                    SelectedSession = selected;
                }
                finally
                {
                    _suppressSessionLoad = false;
                }
            }
        }
        else
        {
            // 尚未选中或选中的会话已被删除 → 重选，正常触发加载。
            SelectedSession = items.FirstOrDefault();
        }
    }

    private async Task LoadMessagesAsync(string sessionId)
    {
        var result = await _sessions.GetMessagesAsync(sessionId);
        Messages.Clear();
        if (!result.IsSuccess || result.Value is null)
        {
            StatusText = $"消息加载失败：{result.Error}";
            return;
        }

        foreach (var m in result.Value)
        {
            Messages.Add(new MessageItemViewModel
            {
                Id = m.Id,
                Role = m.Role,
                Content = m.Content,
                Status = m.Status,
                ErrorText = m.Error,
                ProviderId = m.ProviderId,
                ModelId = m.ModelId,
                OrchestrationRole = m.OrchestrationRole,
                ParentMessageId = m.ParentMessageId,
                Label = m.Label,
                ToolName = m.Name,
                ToolCallId = m.ToolCallId,
                HasToolCalls = !string.IsNullOrEmpty(m.ToolCallsJson),
                // R4-γ：从 AttachmentsJson 恢复附件摘要投影。
                AttachmentSummary = BuildAttachmentSummary(m.AttachmentsJson),
                // 落库时拒绝与失败同为 Degraded；UI 依错误文本恢复"已拒绝"标识。
                ToolDenied = m.Role == ChatRole.Tool
                    && m.Status == MessageStatus.Degraded
                    && (m.Error?.StartsWith("Permission denied", StringComparison.Ordinal) ?? false),
                TokensIn = m.TokensIn,
                TokensOut = m.TokensOut,
                CostUsd = m.CostUsd,
            });
        }

        RecomputeIndents();
    }

    /// <summary>按父消息链计算缩进：有父级的编排消息向右缩进，形成归属树视觉。</summary>
    private void RecomputeIndents()
    {
        var depthById = new Dictionary<string, int>(Messages.Count);
        foreach (var m in Messages)
        {
            var depth = 0;
            if (m.ParentMessageId is not null && depthById.TryGetValue(m.ParentMessageId, out var parentDepth))
            {
                depth = Math.Min(parentDepth + 1, 4);
            }

            depthById[m.Id] = depth;
            m.IndentMargin = new Thickness(depth * 28, 0, 0, 0);
        }
    }

    /// <summary>切换会话时同步策略/provider 选择并加载消息流。</summary>
    partial void OnSelectedSessionChanged(SessionItemViewModel? value)
    {
        if (value is null || _suppressSessionLoad)
        {
            return;
        }

        _suppressStrategySync = true;
        try
        {
            SelectedStrategy = value.Strategy;
            if (!string.IsNullOrEmpty(value.PreferredProviderId)
                && ProviderIds.Contains(value.PreferredProviderId))
            {
                SelectedProviderId = value.PreferredProviderId;
            }
        }
        finally
        {
            _suppressStrategySync = false;
        }
        _ = ObserveAsync(LoadMessagesAsync(value.Id));
        _ = ObserveAsync(RefreshTodosAsync(value.Id));
    }

    /// <summary>刷新当前会话 todo 清单（G5 面板）。清单服务未注册时面板如实为空。</summary>
    private async Task RefreshTodosAsync(string sessionId)
    {
        if (_todos is null)
        {
            return;
        }

        var result = await _todos.ListAsync(sessionId);
        Todos.Clear();
        if (!result.IsSuccess || result.Value is null)
        {
            return;
        }

        foreach (var item in result.Value)
        {
            Todos.Add(new TodoItemViewModel(item));
        }
    }

    /// <summary>切换 todo 完成态（真实落库后刷新面板）。</summary>
    [RelayCommand]
    private async Task ToggleTodoAsync(TodoItemViewModel? item)
    {
        if (_todos is null || item is null || SelectedSession is null)
        {
            return;
        }

        var result = await _todos.UpdateAsync(item.Id, isCompleted: !item.IsCompleted);
        StatusText = result.IsSuccess
            ? (item.IsCompleted ? "待办已标记未完成" : "待办已完成")
            : $"待办更新失败：{result.Error}";
        await RefreshTodosAsync(SelectedSession.Id);
    }

    /// <summary>删除一条 todo（真实落库后刷新面板）。</summary>
    [RelayCommand]
    private async Task DeleteTodoAsync(TodoItemViewModel? item)
    {
        if (_todos is null || item is null || SelectedSession is null)
        {
            return;
        }

        var result = await _todos.DeleteAsync(item.Id);
        StatusText = result.IsSuccess ? "待办已删除" : $"待办删除失败：{result.Error}";
        await RefreshTodosAsync(SelectedSession.Id);
    }

    /// <summary>
    /// 会话 fork（G5 按钮）：经真实 ISessionFork 分叉当前会话，新会话标题带"（fork）"。
    /// 未装配 fork 能力（理论上仅测试替身场景）时如实提示，不静默。
    /// </summary>
    [RelayCommand]
    private async Task ForkSessionAsync(SessionItemViewModel? session)
    {
        session ??= SelectedSession;
        if (session is null)
        {
            return;
        }

        if (_fork is null)
        {
            StatusText = "✗ 当前会话服务不支持分叉";
            return;
        }

        var result = await _fork.ForkAsync(session.Id);
        if (!result.IsSuccess || result.Value is null)
        {
            StatusText = $"fork 失败：{result.Error}";
            return;
        }

        StatusText = $"已分叉为新会话「{result.Value.Title}」";
        await ReloadSessionsAsync();
        SelectedSession = Sessions.FirstOrDefault(s => s.Id == result.Value!.Id);
    }

    // ============== 消息级操作（复制/编辑/重跑/分叉，悬停工具栏） ==============

    /// <summary>把文本写入桌面剪贴板；不可用（无窗口/非桌面）时返回 false 由调用方如实提示。</summary>
    private static async Task<bool> CopyTextToClipboardAsync(string text)
    {
        var clipboard = Avalonia.Application.Current?.ApplicationLifetime
            is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime d
            ? d.MainWindow?.Clipboard
            : null;
        if (clipboard is null) return false;
        await clipboard.SetTextAsync(text);
        return true;
    }

    /// <summary>复制单条消息正文到剪贴板。</summary>
    [RelayCommand]
    private async Task CopyMessageAsync(MessageItemViewModel? msg)
    {
        if (msg is null) return;
        try
        {
            if (!await CopyTextToClipboardAsync(msg.Content ?? string.Empty))
            {
                StatusText = "✗ 剪贴板不可用";
                return;
            }
            StatusText = "✓ 已复制该条消息";
        }
        catch (Exception ex) { StatusText = $"✗ 复制失败：{ex.Message}"; }
    }

    /// <summary>一键复制整段对话（按 用户/助手 轮次格式化）。</summary>
    [RelayCommand]
    private async Task CopyConversationAsync()
    {
        if (Messages.Count == 0) { StatusText = "当前无对话内容"; return; }
        try
        {
            var sb = new System.Text.StringBuilder();
            foreach (var m in Messages)
            {
                if (m.IsTool) continue; // 工具行不入整段复制（噪声大）
                var who = m.IsUser ? "用户" : "助手";
                sb.Append('[').Append(who).Append("] ").AppendLine(m.Content ?? string.Empty);
            }
            if (!await CopyTextToClipboardAsync(sb.ToString().TrimEnd()))
            {
                StatusText = "✗ 剪贴板不可用";
                return;
            }
            StatusText = "✓ 已复制整段对话";
        }
        catch (Exception ex) { StatusText = $"✗ 复制失败：{ex.Message}"; }
    }

    /// <summary>编辑用户消息：把正文载入输入框供修改，之后可发送（重跑）或分叉运行。</summary>
    [RelayCommand]
    private void EditMessage(MessageItemViewModel? msg)
    {
        if (msg is null || !msg.IsUser) { StatusText = "仅支持编辑用户消息"; return; }
        InputText = msg.Content ?? string.Empty;
        StatusText = "已载入输入框，可修改后发送（重跑）或分叉运行";
    }

    /// <summary>重新运行某条用户消息：以其正文作为新一轮输入在当前会话重新执行。</summary>
    [RelayCommand]
    private async Task RerunMessageAsync(MessageItemViewModel? msg)
    {
        if (msg is null || !msg.IsUser) { StatusText = "仅支持重跑用户消息"; return; }
        if (IsStreaming) { StatusText = "正在流式中，请先停止"; return; }
        InputText = msg.Content ?? string.Empty;
        await SendAsync();
    }

    /// <summary>分叉并运行：先 fork 当前会话为新分支，再在新分支以该消息正文重新执行。</summary>
    [RelayCommand]
    private async Task ForkAndRunMessageAsync(MessageItemViewModel? msg)
    {
        if (msg is null || !msg.IsUser) { StatusText = "仅支持对用户消息分叉运行"; return; }
        if (SelectedSession is null) { StatusText = "请先选择会话"; return; }
        if (_fork is null) { StatusText = "✗ 当前会话服务不支持分叉"; return; }
        if (IsStreaming) { StatusText = "正在流式中，请先停止"; return; }

        var result = await _fork.ForkAsync(SelectedSession.Id);
        if (!result.IsSuccess || result.Value is null)
        {
            StatusText = $"fork 失败：{result.Error}";
            return;
        }

        await ReloadSessionsAsync();
        SelectedSession = Sessions.FirstOrDefault(s => s.Id == result.Value!.Id);
        InputText = msg.Content ?? string.Empty;
        StatusText = $"已分叉为「{result.Value.Title}」，正在新分支重新运行…";
        await SendAsync();
    }

    /// <summary>
    /// Steer 插话（G5 输入框，流式进行中可见）：入队下一轮注入。
    /// 队列满/无会话 = 诚实失败提示；入队成功发布 SteerRequestedEvent（审计留痕）。
    /// </summary>
    [RelayCommand]
    private void Steer()
    {
        var text = SteerInput.Trim();
        if (text.Length == 0 || !IsStreaming || SelectedSession is null)
        {
            return;
        }

        if (_steer is null)
        {
            StatusText = "✗ 插话通道未装配";
            return;
        }

        if (_steer.TryEnqueue(SelectedSession.Id, text))
        {
            SteerInput = string.Empty;
            StatusText = "↪ 插话已排队，下一轮注入";
            _events?.Publish(new SteerRequestedEvent(SelectedSession.Id, text, DateTime.UtcNow));
        }
        else
        {
            StatusText = "✗ 插话队列已满，请稍后再试";
        }
    }

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

    /// <summary>策略下拉变更：持久化到当前会话（新会话则作为创建参数）。</summary>
    partial void OnSelectedStrategyChanged(OrchestrationStrategy value)
    {
        OnPropertyChanged(nameof(IsExpertsSelected));
        // 选中专家团即刷新一次网关徽标（#68）：用前即见真实可达性，失败自行收敛为不可达。
        if (value == OrchestrationStrategy.Experts)
        {
            _ = RefreshGatewayStatusAsync();
        }

        if (_suppressStrategySync || IsStreaming)
        {
            return;
        }

        var session = SelectedSession;
        if (session is null || session.Strategy == value)
        {
            return;
        }

        session.Strategy = value;
        _ = ObserveAsync(PersistStrategyAsync(session, value));
    }

    private async Task PersistStrategyAsync(SessionItemViewModel session, OrchestrationStrategy strategy)
    {
        var result = await _sessions.SetStrategyAsync(
            session.Id, strategy, session.PreferredProviderId, session.PreferredModel);
        StatusText = result.IsSuccess
            ? $"本会话策略已切换为 {strategy}"
            : $"策略切换失败：{result.Error}";
    }

    [RelayCommand]
    private async Task NewSessionAsync()
    {
        var result = await _sessions.CreateSessionAsync(
            SelectedStrategy, SelectedProviderId, null);
        if (!result.IsSuccess)
        {
            StatusText = $"新建会话失败：{result.Error}";
            return;
        }

        await ReloadSessionsAsync();
        SelectedSession = Sessions.FirstOrDefault(s => s.Id == result.Value!.Id);
        Messages.Clear();
    }

    [RelayCommand]
    private async Task SelectSessionAsync(SessionItemViewModel? session)
    {
        if (session is null || session.Id == SelectedSession?.Id)
        {
            return;
        }

        SelectedSession = session;
        await LoadMessagesAsync(session.Id);
    }

    [RelayCommand]
    private async Task DeleteSessionAsync(SessionItemViewModel? session)
    {
        if (session is null)
        {
            return;
        }

        var result = await _sessions.DeleteSessionAsync(session.Id);
        if (!result.IsSuccess)
        {
            StatusText = $"删除失败：{result.Error}";
            return;
        }

        if (SelectedSession?.Id == session.Id)
        {
            SelectedSession = null;
            Messages.Clear();
        }

        await ReloadSessionsAsync();
    }

    [RelayCommand]
    private async Task TogglePinAsync(SessionItemViewModel? session)
    {
        if (session is null)
        {
            return;
        }

        await _sessions.TogglePinAsync(session.Id);
        await ReloadSessionsAsync();
    }

    [RelayCommand]
    private void Stop()
    {
        _streamCts?.Cancel();
        StatusText = "正在停止…";
    }

    /// <summary>把对话输入框指令加入队列（UIR-5）。空闲时引擎自动开始执行。</summary>
    [RelayCommand]
    private void EnqueueCommand()
    {
        if (string.IsNullOrWhiteSpace(InputText))
        {
            StatusText = "请输入要加入队列的指令";
            return;
        }

        var text = InputText.Trim();
        InputText = string.Empty;
        Queue.Enqueue(text);
    }

    /// <summary>"发送"按钮命令：转发到 <see cref="SendAsync"/>（默认令牌，非队列入口）。</summary>
    [RelayCommand]
    private Task Send() => SendAsync();

    private async Task<bool> SendAsync(CancellationToken externalCt = default, string? textOverride = null)
    {
        // review L2：队列执行体直接传 textOverride，不读/不覆写用户正在编辑的输入框草稿。
        var text = (textOverride ?? InputText).Trim();
        if (text.Length == 0 || IsStreaming)
        {
            return false; // review M1：拒绝执行（空输入或正在流式中）。
        }

        // @引用先于指令前缀展开：Expand 只扫原始输入，避免把指令内容误当 @记号解析。
        text = AtReference.Expand(text, ReadAtReference);

        // SOUL + AGENTS.md/CLAUDE.md 已下沉到门面请求组装层，作为独立 system 消息
        // 每轮前置注入（不持久化、不占用户消息体）——长系统提示词不再前缀拼接。

        // review M3：流 CTS 提前创建并联动外部（队列条目）令牌——"停止"不再只在流式阶段
        // 生效，前导（记忆注入/建会话）阶段的 await 之间也会响应取消。
        _streamCts = externalCt.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(externalCt)
            : new CancellationTokenSource();
        var streamToken = _streamCts.Token;

        try
        {
            // G2-3 记忆注入点：会话首轮（投影中尚无用户消息）把 MEMORY.md/USER.md
            // + 以本轮输入为查询的 Top-K 笔记语义召回，作为 <memory-context> 块前缀注入。
            // 召回失败降级为仅文件记忆（BuildMemoryBlockAsync 内部如实标注，不阻塞发送）。
            if (_memory is not null && Messages.All(m => !m.IsUser))
            {
                try
                {
                    var block = await _memory.BuildMemoryBlockAsync(text);
                    if (!string.IsNullOrEmpty(block.Text))
                    {
                        text = block.Text + "\n\n" + text;
                        if (block.DegradedNote is not null)
                        {
                            StatusText = $"⚠ {block.DegradedNote}";
                        }
                    }
                }
                catch (Exception ex)
                {
                    // 记忆装配异常不阻塞发送：如实标注后按无记忆继续。
                    StatusText = $"⚠ 记忆注入失败已跳过：{ex.Message}";
                }

                streamToken.ThrowIfCancellationRequested(); // M3：前导阶段响应停止。
            }

            // 无会话则先按当前 provider/策略建一个。
            if (SelectedSession is null)
            {
                var created = await _sessions.CreateSessionAsync(
                    SelectedStrategy, SelectedProviderId, null);
                if (!created.IsSuccess)
                {
                    StatusText = $"新建会话失败：{created.Error}";
                    return true; // 已如实上报（非拒绝），该条按已处理消费。
                }

                await ReloadSessionsAsync();
                SelectedSession = Sessions.FirstOrDefault(s => s.Id == created.Value!.Id);
                streamToken.ThrowIfCancellationRequested(); // M3：前导阶段响应停止。
            }

            var sessionId = SelectedSession!.Id;

            // R4-γ：附件快照后清待发送列表（门面负责序列化，UI 只持快照供投影）。
            var attachments = PendingAttachments.Count > 0
                ? PendingAttachments.ToList()
                : null;
            PendingAttachments.Clear();

            // 附件摘要投影（用户气泡底部显示文件名列表）。
            string? attachmentSummary = null;
            if (attachments is { Count: > 0 })
            {
                attachmentSummary = string.Join("\n",
                    attachments.Select(a => $"📎 {a.FileName} ({a.DisplaySize})"));
            }

            // review L2：仅当本次发送确实消费了输入框（非队列 textOverride）才清空，
            // 队列执行时保留用户正在编辑的草稿。
            if (textOverride is null)
            {
                InputText = string.Empty;
            }

            IsStreaming = true;
            StatusText = "思考中…";

            // 用户消息即时投影（门面负责持久化）。
            Messages.Add(new MessageItemViewModel
            {
                Id = Guid.NewGuid().ToString("N"),
                Role = ChatRole.User,
                Content = text,
                AttachmentSummary = attachmentSummary,
            });

            await foreach (var ev in _facade.SendAsync(
                sessionId, text, attachments, streamToken))
            {
                await Dispatcher.UIThread.InvokeAsync(() => HandleEvent(ev));
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            StatusText = "已停止";
            return true; // 中断也算已处理。
        }
        catch (Exception ex)
        {
            StatusText = $"对话失败：{ex.Message}";
            return true;
        }
        finally
        {
            IsStreaming = false;
            _streamCts?.Dispose();
            _streamCts = null;
            await ReloadSessionsAsync(); // 标题可能因首条消息自动更新
            Queue.NotifyHostIdle(); // review M2：转空闲后接续执行积压队列（队列循环中为无操作）。
        }
    }

    /// <summary>
    /// 档位切换：唯一裁决源是 PermissionPolicy.CurrentMode。进出 Plan 档走
    /// PlanWorkflow 状态机——Enter 建 PLAN.md 骨架并推进 Planning；切出即 Approve
    /// （规格的最小实现，Approve 幂等，Cancel 交互留待完整审批 UI）。
    /// 无工作区时无 PlanWorkflow：直接置档，Plan 档退化为纯只读白名单
    /// （write_plan 工具域未注册，未知工具在 Plan 档被策略 Deny）。
    /// </summary>
    private void ApplyPermissionMode(PermissionMode value)
    {
        if (value != PermissionMode.Plan && _permission.CurrentMode == PermissionMode.Plan)
        {
            _planWorkflow?.Approve();
        }

        if (value == PermissionMode.Plan)
        {
            _planWorkflow?.Enter();
        }

        _permission.CurrentMode = value;
        OnPropertyChanged(nameof(ModeDescription));
    }

    /// <summary>
    /// R4-γ：从 AttachmentsJson（[{FileName, MimeType, SizeBytes}]）恢复摘要文本。
    /// 解析失败返回 null（不阻塞加载；缺失字段如实降级）。
    /// </summary>
    private static string? BuildAttachmentSummary(string? attachmentsJson)
    {
        if (string.IsNullOrEmpty(attachmentsJson))
        {
            return null;
        }

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(attachmentsJson);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                return null;
            }

            var parts = new List<string>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var name = el.TryGetProperty("FileName", out var n) ? n.GetString() : null;
                var size = el.TryGetProperty("SizeBytes", out var s) && s.TryGetInt64(out var sz)
                    ? sz
                    : 0L;
                if (name is null)
                {
                    continue;
                }

                var display = size switch
                {
                    < 1024 => $"{size}B",
                    < 1024 * 1024 => $"{size / 1024.0:F1}KB",
                    _ => $"{size / (1024.0 * 1024.0):F1}MB",
                };
                parts.Add($"📎 {name} ({display})");
            }

            return parts.Count > 0 ? string.Join("\n", parts) : null;
        }
        catch
        {
            // JSON 损坏或格式变化：不阻塞消息加载。
            return null;
        }
    }

    /// <summary>
    /// @引用读取：经 WorkspaceContext 边界解析后读全文；无工作区/越界/不存在/
    /// 超过 <see cref="WorkspaceToolbox.MaxReadBytes"/>（与 read_file 同上限）返回 null，
    /// AtReference.Expand 会把它如实列进未解析清单，不静默丢弃。
    /// </summary>
    private string? ReadAtReference(string reference)
    {
        if (_workspace is null)
        {
            return null;
        }

        var abs = _workspace.Resolve(reference);
        if (abs is null || !File.Exists(abs))
        {
            return null;
        }

        return new FileInfo(abs).Length > WorkspaceToolbox.MaxReadBytes
            ? null
            : File.ReadAllText(abs);
    }

    internal void HandleEvent(ChatEvent ev)
    {
        // 跨会话守卫：流式进行中用户切走会话时，旧轮次的事件不得写入新会话的
        // 气泡列表（否则消息串流）。DB 已由门面/runner 落库，此处丢弃只影响投影。
        if (!string.Equals(ev.SessionId, SelectedSession?.Id, StringComparison.Ordinal))
        {
            return;
        }

        switch (ev)
        {
            case AssistantMessageStarted started:
                Messages.Add(new MessageItemViewModel
                {
                    Id = started.MessageId,
                    Role = ChatRole.Assistant,
                    ProviderId = started.ProviderId,
                    ModelId = started.ModelId,
                    OrchestrationRole = started.OrchestrationRole,
                    ParentMessageId = started.ParentMessageId,
                    Label = started.Label,
                    HasToolCalls = started.HasToolCalls,
                    Status = MessageStatus.Streaming,
                });
                RecomputeIndents();
                StatusText = $"生成中（{started.ModelId}）…";
                break;

            case ToolCallStartedEvent toolStarted:
                Messages.Add(new MessageItemViewModel
                {
                    Id = toolStarted.MessageId,
                    Role = ChatRole.Tool,
                    ToolName = toolStarted.ToolName,
                    ToolCallId = toolStarted.ToolCallId,
                    ToolArguments = toolStarted.ArgumentsJson,
                    ParentMessageId = toolStarted.ParentMessageId,
                    Status = MessageStatus.Streaming,
                });
                RecomputeIndents();
                StatusText = $"正在调用工具 {toolStarted.ToolName}…";
                break;

            case ToolCallCompletedEvent toolDone:
                if (FindMessage(toolDone.MessageId) is { } toolVm)
                {
                    toolVm.Status = toolDone.Success ? MessageStatus.Completed : MessageStatus.Degraded;
                    toolVm.ToolDenied = toolDone.Denied;
                    // 事件只带预览（全文以 DB 消息正文为准，重载会话时展示完整内容）。
                    toolVm.Content = toolDone.OutputPreview ?? string.Empty;
                }

                StatusText = toolDone.Success
                    ? $"工具 {toolDone.ToolName} 完成 · {toolDone.LatencyMs}ms"
                    : toolDone.Denied
                        ? $"工具 {toolDone.ToolName} 已被授权策略拒绝"
                        : $"工具 {toolDone.ToolName} 执行失败";
                break;

            case TextDeltaEvent delta:
                if (FindMessage(delta.MessageId) is { } target)
                {
                    target.Content += delta.Delta;
                }

                break;

            case ReasoningDeltaEvent reasoning:
                if (FindMessage(reasoning.MessageId) is { } reasoningTarget)
                {
                    reasoningTarget.ReasoningContent =
                        (reasoningTarget.ReasoningContent ?? string.Empty) + reasoning.Delta;
                }

                break;

            case MessageCompletedEvent completed:
                if (FindMessage(completed.MessageId) is { } done)
                {
                    done.Status = MessageStatus.Completed;
                    done.TokensIn = completed.TokensIn;
                    done.TokensOut = completed.TokensOut;
                    done.CostUsd = completed.CostUsd;
                }

                StatusText = $"完成 · {completed.TokensIn}→{completed.TokensOut} tokens · {completed.LatencyMs}ms";
                break;

            case MessageFailedEvent failed:
                _turnFailed = true;
                if (!string.IsNullOrEmpty(failed.MessageId)
                    && FindMessage(failed.MessageId) is { } failedVm)
                {
                    failedVm.Status = MessageStatus.Failed;
                    failedVm.ErrorText = failed.Error;
                }
                else
                {
                    // 轮级失败（无对应消息）：如实投一条错误气泡，不静默。
                    Messages.Add(new MessageItemViewModel
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        Role = ChatRole.Assistant,
                        Status = MessageStatus.Failed,
                        ErrorText = failed.Error,
                    });
                }

                StatusText = $"失败：{failed.Error}";
                break;

            case MessageCancelledEvent cancelled:
                _turnFailed = true;
                if (!string.IsNullOrEmpty(cancelled.MessageId)
                    && FindMessage(cancelled.MessageId) is { } cancelledVm)
                {
                    cancelledVm.Status = MessageStatus.Cancelled;
                }

                StatusText = "已取消";
                break;

            case TurnCompletedEvent turn:
                // 成本只在真实计价时展示：未配置单价的模型不显示 $0.0000（那会伪装成已核算）。
                var costPart = turn.TotalCostUsd > 0 ? $" · 成本 ${turn.TotalCostUsd:F4}" : string.Empty;
                StatusText = $"本轮结束 · {turn.TotalMessages} 条回复{costPart}";

                // 审批熔断成本通道（G3）：真实 usage 计费逐轮累计，达到阈值自动熔断。
                if (turn.TotalCostUsd > 0 && _approvalBreaker is not null)
                {
                    _approvalBreaker.RecordCost(turn.TotalCostUsd);
                }

                // G2-3 对话沉淀：真实轨迹 + 失败教训写入经验库（fire-and-forget，失败只降级记录）。
                if (_memory is not null && SelectedSession is not null)
                {
                    var lastUser = Messages.LastOrDefault(m => m.IsUser)?.Content ?? string.Empty;
                    var lastAssistant = Messages.LastOrDefault(m => m.IsAssistant)?.Content;
                    var sessionIdForMemory = SelectedSession.Id;
                    var turnFailed = _turnFailed;
                    _turnFailed = false;
                    _ = ObserveAsync(_memory.ConsolidateTurnAsync(
                        sessionIdForMemory, lastUser, lastAssistant,
                        succeeded: !turnFailed, costUsd: turn.TotalCostUsd));

                    // todo 清单可能被本轮工具更新：轮末刷新（G5 面板）。
                    _ = ObserveAsync(RefreshTodosAsync(sessionIdForMemory));
                }

                break;
        }
    }

    private MessageItemViewModel? FindMessage(string messageId)
        => string.IsNullOrEmpty(messageId)
            ? null
            : Messages.LastOrDefault(m => m.Id == messageId);
}
