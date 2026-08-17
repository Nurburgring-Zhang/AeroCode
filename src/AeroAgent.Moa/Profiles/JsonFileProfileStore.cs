using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace AeroAgent.Moa.Profiles;

/// <summary>
/// JSON 文件画像存储。文件不存在时返回 null（目录以种子铺底）。
/// 写入为"临时文件 + 原子替换"，避免半截文件。
/// </summary>
public sealed class JsonFileProfileStore : IProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _filePath;

    public JsonFileProfileStore(string filePath)
    {
        _filePath = string.IsNullOrWhiteSpace(filePath)
            ? throw new ArgumentException("path required", nameof(filePath))
            : filePath;
    }

    public async Task<IReadOnlyList<ModelProfile>?> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_filePath))
        {
            return null;
        }

        var json = await File.ReadAllTextAsync(_filePath, ct);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var doc = JsonSerializer.Deserialize<ProfileFile>(json, JsonOptions);
            return doc?.Profiles ?? new List<ModelProfile>();
        }
        catch (JsonException)
        {
            // 画像文件由用户手工编辑，损坏不应阻塞启动：
            // 返回 null 让目录回退内建种子，用户重新保存即可覆盖坏文件。
            return null;
        }
    }

    public async Task SaveAsync(IReadOnlyList<ModelProfile> profiles, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var json = JsonSerializer.Serialize(new ProfileFile { Profiles = profiles }, JsonOptions);
        // 随机临时名：固定 .tmp 在快速连续保存时会互相覆盖在途文件。
        var tmp = $"{_filePath}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(tmp, json, ct);
        File.Move(tmp, _filePath, overwrite: true);
    }

    private sealed class ProfileFile
    {
        public int Version { get; set; } = 1;
        public IReadOnlyList<ModelProfile> Profiles { get; set; } = new List<ModelProfile>();
    }
}

/// <summary>
/// 内建画像种子：按 provider 家族给出可编辑的初始强项。
/// 成本一律留空（未知）——真实价格随时间变动，由用户在设置中填写后才参与核算。
/// </summary>
public static class BuiltInProfiles
{
    public static IReadOnlyList<ModelProfile> Seed()
    {
        return new List<ModelProfile>
        {
            With("deepseek", ModelStrength.Code, ModelStrength.Math, ModelStrength.Analysis),
            With("qwen", ModelStrength.General, ModelStrength.Writing, ModelStrength.Analysis),
            With("kimi", ModelStrength.Analysis, ModelStrength.Writing),
            With("glm", ModelStrength.General, ModelStrength.Writing),
            With("openai", ModelStrength.General, ModelStrength.Code, ModelStrength.Analysis),
            With("claude", ModelStrength.Code, ModelStrength.Writing, ModelStrength.Analysis),
            With("anthropic", ModelStrength.Code, ModelStrength.Writing, ModelStrength.Analysis),
            With("openrouter", ModelStrength.General),
            With("ollama", ModelStrength.General),
            With("lmstudio", ModelStrength.General),
            With("minimax", ModelStrength.Writing),
            With("custom", ModelStrength.General),
        };
    }

    private static ModelProfile With(string providerId, params string[] strengths) =>
        new()
        {
            ProviderId = providerId,
            ModelId = string.Empty, // 该 provider 的默认模型
            Strengths = new List<string>(strengths),
        };
}
