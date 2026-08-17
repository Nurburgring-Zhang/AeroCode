using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AeroAgent.Moa.Profiles;

/// <summary>
/// 画像目录默认实现：内存字典 + 可选外部存储（<see cref="IProfileStore"/>）。
/// </summary>
public sealed class ModelProfileCatalog : IModelProfileCatalog
{
    private readonly object _sync = new();
    private readonly Dictionary<string, ModelProfile> _profiles = new(StringComparer.Ordinal);
    private readonly IProfileStore? _store;

    public ModelProfileCatalog(IProfileStore? store = null)
    {
        _store = store;
    }

    /// <summary>从外部存储加载；不存在时以 <paramref name="seed"/> 铺底。</summary>
    public async Task LoadAsync(IEnumerable<ModelProfile>? seed = null, CancellationToken ct = default)
    {
        var loaded = _store is null ? null : await _store.LoadAsync(ct);
        lock (_sync)
        {
            _profiles.Clear();
            if (seed is not null)
            {
                foreach (var p in seed) _profiles[p.Key] = p;
            }

            if (loaded is not null)
            {
                foreach (var p in loaded) _profiles[p.Key] = p; // 存储覆盖铺底
            }
        }
    }

    public ModelProfile? Find(string providerId, string modelId)
    {
        lock (_sync)
        {
            return _profiles.TryGetValue(ModelProfile.MakeKey(providerId, modelId), out var p) ? p : null;
        }
    }

    public ModelProfile GetOrAddDefault(string providerId, string modelId)
    {
        lock (_sync)
        {
            if (_profiles.TryGetValue(ModelProfile.MakeKey(providerId, modelId), out var exact))
            {
                return exact;
            }

            if (modelId.Length > 0 &&
                _profiles.TryGetValue(ModelProfile.MakeKey(providerId, string.Empty), out var fallback))
            {
                return fallback;
            }

            var created = new ModelProfile
            {
                ProviderId = providerId,
                ModelId = modelId,
                Strengths = new List<string> { ModelStrength.General },
            };
            _profiles[created.Key] = created;
            return created;
        }
    }

    public void Upsert(ModelProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        lock (_sync)
        {
            _profiles[profile.Key] = profile;
        }
    }

    public IReadOnlyList<ModelProfile> List()
    {
        lock (_sync)
        {
            return _profiles.Values
                .OrderBy(p => p.ProviderId, StringComparer.Ordinal)
                .ThenBy(p => p.ModelId, StringComparer.Ordinal)
                .ToList();
        }
    }

    public void RecordUsage(string providerId, string modelId, int latencyMs, bool failed)
    {
        var profile = GetOrAddDefault(providerId, modelId);
        profile.Stats.Record(latencyMs, failed);
    }

    public async Task SaveAsync(CancellationToken ct = default)
    {
        if (_store is null)
        {
            return;
        }

        // 深拷贝快照（持目录锁取）：序列化期间并发的 RecordUsage/编辑
        // 不得改动正在写盘的对象，否则产出脏 JSON。
        IReadOnlyList<ModelProfile> snapshot;
        lock (_sync)
        {
            snapshot = _profiles.Values
                .OrderBy(p => p.ProviderId, StringComparer.Ordinal)
                .ThenBy(p => p.ModelId, StringComparer.Ordinal)
                .Select(p => p.Snapshot())
                .ToList();
        }

        await _store.SaveAsync(snapshot, ct);
    }
}

/// <summary>画像外部存储抽象（JSON 文件实现见 <see cref="JsonFileProfileStore"/>）。</summary>
public interface IProfileStore
{
    Task<IReadOnlyList<ModelProfile>?> LoadAsync(CancellationToken ct = default);
    Task SaveAsync(IReadOnlyList<ModelProfile> profiles, CancellationToken ct = default);
}
