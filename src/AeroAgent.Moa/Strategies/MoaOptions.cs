using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using AeroAgent.Conversation.Models;

namespace AeroAgent.Moa.Strategies;

/// <summary>显式模型绑定：某编排角色固定使用某 provider/model。ModelId 为空 = 该 provider 默认模型。</summary>
public sealed record ModelBinding(string ProviderId, string? ModelId);

/// <summary>
/// MOA 编排选项：角色绑定（router/planner/synthesizer/judge）、集成规模、单轮预算。
/// 未绑定的角色由 ModelAssigner 按画像自动分配。
/// </summary>
public sealed class MoaOptions
{
    /// <summary>新建会话的默认编排策略（会话仍可在聊天工具条单独切换）。</summary>
    public OrchestrationStrategy DefaultStrategy { get; set; } = OrchestrationStrategy.Single;

    public ModelBinding? Router { get; set; }
    public ModelBinding? Planner { get; set; }
    public ModelBinding? Synthesizer { get; set; }
    public ModelBinding? Judge { get; set; }

    /// <summary>Ensemble 并行作答的模型数（2..4）。</summary>
    public int EnsembleSize { get; set; } = 2;

    /// <summary>
    /// 单轮成本上限（美元）；null = 不限制。
    /// 语义：只有已计价（画像里填写了单价）的调用计入预算；
    /// 未计价调用如实放行、不估算成本——绝不拿猜测值触发预算中止。
    /// 线程安全：double? 是 hasValue+value 双字段结构体，CLR 不保证原子读写；
    /// 设置页（UI 线程）保存与策略（线程池）每轮读取交错时可能撕裂出
    /// hasValue=true + 陈旧 value 的混合态（如 0.0 → TurnBudget 构造抛异常）。
    /// 故 get/set 成对持锁，保证读端永远看到完整的 null 或完整的数值。
    /// </summary>
    public double? MaxUsdPerTurn
    {
        get { lock (_budgetLock) { return _maxUsdPerTurn; } }
        set { lock (_budgetLock) { _maxUsdPerTurn = value; } }
    }

    private readonly object _budgetLock = new();
    private double? _maxUsdPerTurn;

    /// <summary>
    /// 是否启用工具循环（true = 注册中心有工具时 worker 携带 tools 多轮执行；
    /// false = 一律普通调用，不携带 tools）。默认 true；关闭只影响请求形态，
    /// 工具箱注册与授权裁决不受影响。
    /// </summary>
    public bool ToolsEnabled { get; set; } = true;

    /// <summary>
    /// A5 双编排原语（F-M2 收口，R2 缝合 #20 改为可空）：
    /// <see langword="null"/>（默认）= 未显式配置 → DecomposeStrategy 按 PrimitiveSelectionPolicy
    /// 形态自动判定（扇出/可并行 → AgentsAsTool；顺序链/上下文重 → Handoff）；
    /// 显式 <see cref="OrchestrationPrimitive.AgentsAsTool"/> = 抑制自动判定、强制基线并行分工；
    /// 显式 <see cref="OrchestrationPrimitive.Handoff"/> = 强制顺序移交。
    /// 旧 moaoptions.json（无此字段）反序列化后为 null（= 自动判定），行为与「显式 AgentsAsTool」
    /// 在基线并行计划上等价；过渡期（R2-δ）非空枚举无法区分「默认」与「显式选 AgentsAsTool」的
    /// 根因由此收口（显式 AgentsAsTool 抑制自动判定现在可达）。设置层 = MoaOptions（moaoptions.json），
    /// 与 DefaultStrategy/EnsembleSize 等编排选项同层，不在 settings.json 重复落字段。
    /// </summary>
    public OrchestrationPrimitive? OrchestrationPrimitive { get; set; }

    /// <summary>
    /// 选项在运行期被修改（设置页保存）。策略在每轮 ExecuteAsync 直接读字段，
    /// 就地修改单例即热生效；本事件仅供 UI 订阅（如 ChatViewModel 刷新新会话默认策略）。
    /// </summary>
    public event Action? OptionsChanged;

    /// <summary>由组合根/设置页在写回选项后调用，广播变更。</summary>
    public void RaiseOptionsChanged() => OptionsChanged?.Invoke();
}

/// <summary>MOA 选项的 JSON 文件存储（与画像存储同样的原子写策略）。</summary>
public sealed class JsonMoaOptionsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // 枚举按字符串落盘（DefaultStrategy 等）：用户可读可改，与 permissions.json 一致。
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>R2 修复 MED-3：预算上限字段抢救用严格形态匹配（PascalCase/camelCase + 有限数值字面量）。</summary>
    private static readonly System.Text.RegularExpressions.Regex BudgetCapPattern = new(
        "\"(?:MaxUsdPerTurn|maxUsdPerTurn)\"\\s*:\\s*(-?[0-9]+(?:\\.[0-9]+)?)",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private readonly string _filePath;

    public JsonMoaOptionsStore(string filePath)
    {
        _filePath = string.IsNullOrWhiteSpace(filePath)
            ? throw new ArgumentException("path required", nameof(filePath))
            : filePath;
    }

    /// <summary>
    /// R2 修复 MED-3：最近一次 LoadAsync 的拒载异常（null = 正常加载 / 文件不存在 / 空文件）。
    /// moaoptions.json 非法时选项仍按既有 fail-safe 语义回退默认（不阻塞启动），但通过本属性
    /// 向组合根暴露拒载事实——组合根据此显式 WARN，使「预算上限/角色绑定丢失」可观测而非静默。
    /// </summary>
    public Exception? LastLoadError { get; private set; }

    public async Task<MoaOptions> LoadAsync(CancellationToken ct = default)
    {
        string? json = null;
        if (!File.Exists(_filePath))
        {
            LastLoadError = null;
            return new MoaOptions();
        }

        try
        {
            json = await File.ReadAllTextAsync(_filePath, ct);
            if (string.IsNullOrWhiteSpace(json))
            {
                LastLoadError = null; // 空文件 = 未配置，不算拒载
                return new MoaOptions();
            }

            var options = JsonSerializer.Deserialize<MoaOptions>(json, JsonOptions) ?? new MoaOptions();
            LastLoadError = null;
            return options;
        }
        catch (OperationCanceledException)
        {
            throw; // 取消如实向上抛，不算"配置损坏"。
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // 配置损坏或文件被占用（杀软/备份锁）都不应阻塞启动：
            // 返回默认项，由用户下次保存覆盖。
            // R2 修复 MED-3：不再完全静默——记录拒载异常（组合根 WARN），并尽力从原始文本
            // 抢救预算上限（最具安全意义的单字段），降低静默丢失的安全敞口。
            LastLoadError = ex;
            var fallback = new MoaOptions();
            TrySalvageBudgetCap(json, fallback);
            return fallback;
        }
    }

    /// <summary>
    /// R2 修复 MED-3：moaoptions.json 非法时尽力从原始文本抢救 MaxUsdPerTurn（预算上限）。
    /// 关键点：文件既然进了拒载分支，整体 JSON 解析必然失败——抢救不能依赖整体解析，
    /// 只能对原始文本做严格形态的正则提取（本 store 落盘的 PascalCase 与常见 camelCase 变体），
    /// 且要求正的有限数值。尽力而为：任何异常一律吞掉（原始错误已经 <see cref="LastLoadError"/>
    /// 可观测），绝不阻塞启动、不伪造成功。
    /// </summary>
    private static void TrySalvageBudgetCap(string? json, MoaOptions options)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        try
        {
            var match = BudgetCapPattern.Match(json);
            if (!match.Success ||
                !double.TryParse(
                    match.Groups[1].Value,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var cap) ||
                !double.IsFinite(cap) ||
                cap <= 0)
            {
                return;
            }

            options.MaxUsdPerTurn = cap;
        }
        catch
        {
            // 抢救失败保持默认（无上限语义不变）；拒载事实由 LastLoadError 承载。
        }
    }

    public async Task SaveAsync(MoaOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var json = JsonSerializer.Serialize(options, JsonOptions);
        // 随机临时名：固定 .tmp 在快速连续保存时会互相覆盖在途文件。
        var tmp = $"{_filePath}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(tmp, json, ct);
        File.Move(tmp, _filePath, overwrite: true);
    }
}
