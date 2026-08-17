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

    private readonly string _filePath;

    public JsonMoaOptionsStore(string filePath)
    {
        _filePath = string.IsNullOrWhiteSpace(filePath)
            ? throw new ArgumentException("path required", nameof(filePath))
            : filePath;
    }

    public async Task<MoaOptions> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_filePath))
        {
            return new MoaOptions();
        }

        try
        {
            var json = await File.ReadAllTextAsync(_filePath, ct);
            if (string.IsNullOrWhiteSpace(json))
            {
                return new MoaOptions();
            }

            return JsonSerializer.Deserialize<MoaOptions>(json, JsonOptions) ?? new MoaOptions();
        }
        catch (OperationCanceledException)
        {
            throw; // 取消如实向上抛，不算"配置损坏"。
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // 配置损坏或文件被占用（杀软/备份锁）都不应阻塞启动：
            // 返回默认项，由用户下次保存覆盖。
            return new MoaOptions();
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
