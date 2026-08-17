using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AeroAgent.Moa.Profiles;

/// <summary>
/// 单个模型的能力画像。用户可编辑；运行统计（调用次数/延迟/失败率）自学习回填。
/// 成本为 null 表示"未知"——成本核算只对已知价格的模型计费，绝不估算。
/// </summary>
public sealed class ModelProfile
{
    /// <summary>Provider Id（对应 IProviderRegistry 的已配置 provider）。</summary>
    public string ProviderId { get; set; } = string.Empty;

    /// <summary>模型 Id。空串表示"该 provider 的默认模型"。</summary>
    public string ModelId { get; set; } = string.Empty;

    /// <summary>
    /// 强项标签列表（<see cref="ModelStrength"/> 词汇或自定义）。
    /// 默认空列表：JSON 反序列化时 System.Text.Json 会向 getter 返回的实例追加元素
    /// （不调 setter），若此处预置默认值会污染从文件加载的画像。
    /// 自动创建的兜底画像由 GetOrAddDefault 显式赋 general。
    /// </summary>
    public List<string> Strengths { get; set; } = new();

    public int ContextWindow { get; set; }
    public int MaxOutputTokens { get; set; }

    /// <summary>每百万输入 token 的美元成本；null = 未知。</summary>
    public double? CostPerMIn { get; set; }

    /// <summary>每百万输出 token 的美元成本；null = 未知。</summary>
    public double? CostPerMOut { get; set; }

    public SpeedTier SpeedTier { get; set; } = SpeedTier.Medium;

    /// <summary>运行自学习统计。</summary>
    public ProfileStats Stats { get; set; } = new();

    /// <summary>画像键：providerId::modelId（modelId 可为空串）。</summary>
    public string Key => MakeKey(ProviderId, ModelId);

    public static string MakeKey(string providerId, string modelId) =>
        $"{providerId}::{modelId}";

    /// <summary>
    /// 深拷贝快照（持久化用）。存活画像的 Stats/Strengths 会被并发修改，
    /// 序列化必须拿一致时点的副本，不能拿活引用——否则边序列化边变化会产出脏 JSON。
    /// </summary>
    public ModelProfile Snapshot() => new()
    {
        ProviderId = ProviderId,
        ModelId = ModelId,
        Strengths = new List<string>(Strengths),
        ContextWindow = ContextWindow,
        MaxOutputTokens = MaxOutputTokens,
        CostPerMIn = CostPerMIn,
        CostPerMOut = CostPerMOut,
        SpeedTier = SpeedTier,
        Stats = Stats.Snapshot(),
    };
}

/// <summary>运行自学习统计：调用次数、累计延迟、失败次数。用于分配器打分。</summary>
public sealed class ProfileStats
{
    private readonly object _sync = new();

    public long Calls { get; set; }
    public long TotalLatencyMs { get; set; }
    public long Failures { get; set; }

    public double FailureRate
    {
        get { lock (_sync) return Calls <= 0 ? 0 : (double)Failures / Calls; }
    }

    public double AvgLatencyMs
    {
        get { lock (_sync) return Calls <= 0 ? 0 : (double)TotalLatencyMs / Calls; }
    }

    public void Record(int latencyMs, bool failed)
    {
        lock (_sync)
        {
            Calls++;
            TotalLatencyMs += latencyMs;
            if (failed) Failures++;
        }
    }

    /// <summary>持锁取计数器一致快照（随画像一起持久化）。</summary>
    public ProfileStats Snapshot()
    {
        lock (_sync)
        {
            return new ProfileStats
            {
                Calls = Calls,
                TotalLatencyMs = TotalLatencyMs,
                Failures = Failures,
            };
        }
    }
}

/// <summary>
/// 模型画像目录：查询/更新/持久化。线程安全。
/// </summary>
public interface IModelProfileCatalog
{
    /// <summary>精确查找画像（providerId + modelId）。</summary>
    ModelProfile? Find(string providerId, string modelId);

    /// <summary>
    /// 取画像；无精确匹配时回退该 provider 的默认模型画像；
    /// 再无则创建一份通用默认画像（general 强项、成本未知）。
    /// </summary>
    ModelProfile GetOrAddDefault(string providerId, string modelId);

    void Upsert(ModelProfile profile);

    IReadOnlyList<ModelProfile> List();

    /// <summary>记录一次真实调用的延迟/成败（自学习）。</summary>
    void RecordUsage(string providerId, string modelId, int latencyMs, bool failed);

    /// <summary>持久化到外部存储（JSON 文件等）。无存储时为空操作。</summary>
    Task SaveAsync(CancellationToken ct = default);
}
