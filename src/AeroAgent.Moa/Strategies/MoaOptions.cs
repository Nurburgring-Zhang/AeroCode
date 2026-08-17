using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace AeroAgent.Moa.Strategies;

/// <summary>显式模型绑定：某编排角色固定使用某 provider/model。ModelId 为空 = 该 provider 默认模型。</summary>
public sealed record ModelBinding(string ProviderId, string? ModelId);

/// <summary>
/// MOA 编排选项：角色绑定（router/planner/synthesizer/judge）、集成规模、单轮预算。
/// 未绑定的角色由 ModelAssigner 按画像自动分配。
/// </summary>
public sealed class MoaOptions
{
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
    /// </summary>
    public double? MaxUsdPerTurn { get; set; }
}

/// <summary>MOA 选项的 JSON 文件存储（与画像存储同样的原子写策略）。</summary>
public sealed class JsonMoaOptionsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
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

        var json = await File.ReadAllTextAsync(_filePath, ct);
        if (string.IsNullOrWhiteSpace(json))
        {
            return new MoaOptions();
        }

        try
        {
            return JsonSerializer.Deserialize<MoaOptions>(json, JsonOptions) ?? new MoaOptions();
        }
        catch (JsonException)
        {
            // 配置损坏不应阻塞启动：返回默认项，由用户重新保存覆盖。
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
