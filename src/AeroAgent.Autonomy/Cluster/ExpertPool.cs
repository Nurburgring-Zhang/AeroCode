// Copyright (c) AeroCode V3.0
// ExpertPool — persistent sub-agent registry. Each expert has a stable id, role and
// session identity, plus a memory that is really persisted as one JSON file per expert
// under the Autonomy data directory ({root}/cluster/experts/{expertId}.json) and really
// loaded: the pool restores every expert from disk on construction, and the scheduler
// injects the persisted memory snapshot into each new task context.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using AeroAgent.Autonomy.Data;
using Microsoft.Extensions.Logging;

namespace AeroAgent.Autonomy.Cluster;

/// <summary>Identity of one persistent expert sub-agent.</summary>
public sealed class ExpertHandle
{
    /// <summary>Stable unique id (persisted, used as the file name of the expert record).</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Role description (e.g. "backend engineer", "test specialist").</summary>
    public string Role { get; set; } = string.Empty;

    /// <summary>Optional longer description of the expert's remit.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Stable session id so agent-backed executors keep one context per expert.</summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>Registration time (UTC).</summary>
    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>One memory entry accumulated by an expert across tasks.</summary>
/// <param name="AtUtc">When the entry was recorded.</param>
/// <param name="Kind">Category tag (e.g. "cluster" for scheduler-written notes).</param>
/// <param name="Content">Entry body.</param>
public sealed record ExpertMemoryEntry(DateTime AtUtc, string Kind, string Content);

/// <summary>On-disk shape of one expert file (profile + memory).</summary>
internal sealed class StoredExpert
{
    /// <summary>The expert's identity record.</summary>
    public ExpertHandle Profile { get; set; } = new();

    /// <summary>The expert's accumulated memory entries.</summary>
    public List<ExpertMemoryEntry> Memory { get; set; } = new();
}

/// <summary>
/// Persistent pool of expert sub-agents. Registration, lookup and listing are served
/// from an in-memory registry that is hydrated from disk at construction; every
/// mutation (registration, memory append) is immediately persisted to the per-expert
/// JSON file, so a fresh pool over the same data directory sees the same experts.
/// All members are thread-safe.
/// </summary>
public sealed class ExpertPool
{
    /// <summary>每个专家 memory 条目上限默认值（0 = 无限制）。</summary>
    public const int DefaultMaxMemoryEntries = 1000;

    private readonly string _clusterDirectory;
    private readonly string _expertsDirectory;
    private readonly int _maxMemoryEntries;
    private readonly object _gate = new();
    private readonly Dictionary<string, ExpertHandle> _experts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ExpertMemoryEntry>> _memory = new(StringComparer.Ordinal);
    private readonly ILogger? _logger;

    /// <summary>Cluster data root ({AutonomyRoot}/cluster).</summary>
    public string ClusterDirectory => _clusterDirectory;

    /// <summary>Directory holding one JSON file per expert.</summary>
    public string ExpertsDirectory => _expertsDirectory;

    /// <summary>每个专家允许保留的最大 memory 条目数（0 = 无限制）。</summary>
    public int MaxMemoryEntries => _maxMemoryEntries;

    /// <summary>Number of registered experts.</summary>
    public int Count
    {
        get { lock (_gate) { return _experts.Count; } }
    }

    /// <summary>
    /// Create (or reopen) a pool rooted at the given Autonomy data paths. Existing
    /// expert files in {root}/cluster/experts are loaded; unreadable files are skipped
    /// with a [DEGRADED] warning instead of failing the pool.
    /// </summary>
    /// <param name="paths">Autonomy data paths.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="maxMemoryEntries">每个专家 memory 条目上限；超过时删除最旧条目。0 表示不限制。</param>
    public ExpertPool(AutonomyDataPaths paths, ILogger? logger = null, int maxMemoryEntries = DefaultMaxMemoryEntries)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _logger = logger;
        _maxMemoryEntries = maxMemoryEntries < 0 ? 0 : maxMemoryEntries;
        _clusterDirectory = Path.Combine(paths.RootDirectory, "cluster");
        _expertsDirectory = Path.Combine(_clusterDirectory, "experts");
        Directory.CreateDirectory(_expertsDirectory);
        LoadFromDisk();
    }

    /// <summary>
    /// Register a new persistent expert and persist it to disk immediately.
    /// </summary>
    /// <param name="role">Role description (non-empty).</param>
    /// <param name="description">Optional longer description.</param>
    /// <returns>The registered expert's handle.</returns>
    public ExpertHandle RegisterExpert(string role, string? description = null)
    {
        if (string.IsNullOrWhiteSpace(role))
        {
            throw new ArgumentException("Expert role must not be empty.", nameof(role));
        }

        var suffix = Guid.NewGuid().ToString("N");
        var handle = new ExpertHandle
        {
            Id = "expert-" + suffix[..16],
            Role = role.Trim(),
            Description = (description ?? string.Empty).Trim(),
            SessionId = "expert-session-" + suffix[16..],
            CreatedAtUtc = DateTime.UtcNow,
        };

        lock (_gate)
        {
            _experts[handle.Id] = handle;
            _memory[handle.Id] = new List<ExpertMemoryEntry>();
            PersistNoLock(handle.Id);
        }

        _logger?.LogInformation("ExpertPool registered expert {Expert} (role={Role}).", handle.Id, handle.Role);
        return handle;
    }

    /// <summary>Get an expert by id, or null when unknown.</summary>
    public ExpertHandle? GetExpert(string expertId)
    {
        ArgumentException.ThrowIfNullOrEmpty(expertId);
        lock (_gate)
        {
            return _experts.TryGetValue(expertId, out var handle) ? handle : null;
        }
    }

    /// <summary>List all registered experts ordered by registration time.</summary>
    public IReadOnlyList<ExpertHandle> ListExperts()
    {
        lock (_gate)
        {
            return _experts.Values.OrderBy(e => e.CreatedAtUtc).ToList();
        }
    }

    /// <summary>
    /// Append one memory entry for an expert and persist the expert file to disk.
    /// </summary>
    /// <exception cref="ArgumentException">The expert id is unknown.</exception>
    public ExpertMemoryEntry AppendMemory(string expertId, string kind, string content)
    {
        ArgumentException.ThrowIfNullOrEmpty(expertId);
        ArgumentException.ThrowIfNullOrEmpty(kind);
        ArgumentNullException.ThrowIfNull(content);

        var entry = new ExpertMemoryEntry(DateTime.UtcNow, kind.Trim(), content);
        lock (_gate)
        {
            if (!_experts.ContainsKey(expertId))
            {
                throw new ArgumentException($"Unknown expert '{expertId}'.", nameof(expertId));
            }

            var list = _memory[expertId];
            list.Add(entry);
            TrimMemoryNoLock(expertId, list);
            PersistNoLock(expertId);
        }

        return entry;
    }

    private void TrimMemoryNoLock(string expertId, List<ExpertMemoryEntry> list)
    {
        if (_maxMemoryEntries <= 0 || list.Count <= _maxMemoryEntries)
        {
            return;
        }

        var removeCount = list.Count - _maxMemoryEntries;
        list.RemoveRange(0, removeCount);
        _logger?.LogWarning(
            "[DEGRADED] Expert {Expert} memory exceeded cap {Cap}: trimmed {Removed} oldest entries.",
            expertId, _maxMemoryEntries, removeCount);
    }

    /// <summary>
    /// Load the expert's memory entries (in append order). The entries are the
    /// disk-backed truth restored at pool construction and kept current on every append.
    /// </summary>
    /// <exception cref="ArgumentException">The expert id is unknown.</exception>
    public IReadOnlyList<ExpertMemoryEntry> LoadMemory(string expertId)
    {
        ArgumentException.ThrowIfNullOrEmpty(expertId);
        lock (_gate)
        {
            if (!_memory.TryGetValue(expertId, out var entries))
            {
                throw new ArgumentException($"Unknown expert '{expertId}'.", nameof(expertId));
            }

            return entries.ToList();
        }
    }

    /// <summary>
    /// Render the expert's most recent memory entries as a text snapshot for injection
    /// into a new task context. Returns an empty string when the expert has no memory yet.
    /// </summary>
    /// <param name="expertId">The expert whose memory is rendered.</param>
    /// <param name="maxEntries">Maximum number of recent entries included (0 = none).</param>
    /// <exception cref="ArgumentException">The expert id is unknown.</exception>
    public string BuildMemorySnapshot(string expertId, int maxEntries)
    {
        if (maxEntries <= 0)
        {
            return string.Empty;
        }

        IReadOnlyList<ExpertMemoryEntry> entries = LoadMemory(expertId);
        if (entries.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        foreach (var entry in entries.TakeLast(maxEntries))
        {
            sb.AppendLine($"- [{entry.AtUtc:O}] ({entry.Kind}) {entry.Content}");
        }

        return sb.ToString().TrimEnd();
    }

    private string ExpertFilePath(string expertId) => Path.Combine(_expertsDirectory, expertId + ".json");

    private void LoadFromDisk()
    {
        string[] files;
        try
        {
            files = Directory.GetFiles(_expertsDirectory, "*.json");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("[DEGRADED] ExpertPool could not enumerate '{Dir}': {Error}", _expertsDirectory, ex.Message);
            return;
        }

        foreach (var file in files.OrderBy(f => f, StringComparer.Ordinal))
        {
            try
            {
                var stored = JsonSerializer.Deserialize<StoredExpert>(File.ReadAllText(file), ClusterJson.Options);
                if (stored?.Profile is null || string.IsNullOrWhiteSpace(stored.Profile.Id))
                {
                    _logger?.LogWarning("[DEGRADED] ExpertPool skipped expert file '{File}': missing profile id.", file);
                    continue;
                }

                _experts[stored.Profile.Id] = stored.Profile;
                var loadedMemory = stored.Memory ?? new List<ExpertMemoryEntry>();
                TrimMemoryNoLock(stored.Profile.Id, loadedMemory);
                _memory[stored.Profile.Id] = loadedMemory;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning("[DEGRADED] ExpertPool failed to load expert file '{File}': {Error}", file, ex.Message);
            }
        }
    }

    /// <summary>Atomically (write-tmp + move) persist one expert record. Caller holds <c>_gate</c>.</summary>
    private void PersistNoLock(string expertId)
    {
        var stored = new StoredExpert { Profile = _experts[expertId], Memory = _memory[expertId] };
        var path = ExpertFilePath(expertId);
        var tmp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(tmp, JsonSerializer.Serialize(stored, ClusterJson.Options));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("[DEGRADED] ExpertPool could not persist expert {Expert} to '{Path}': {Error}", expertId, path, ex.Message);
            try
            {
                if (File.Exists(tmp))
                {
                    File.Delete(tmp);
                }
            }
            catch (IOException)
            {
                // Best effort: the stray tmp file is harmless and overwritten on next persist.
            }
        }
    }
}
