// Copyright (c) AeroCode V3.0
// LruCache — 通用 LRU 缓存。双向链表 + 哈希表，O(1) get/put。
// 应用：LoopRunner cache-first 命中跳过 (Reasonix 99.82% hit 设计模式)；
//       WebResearch 抓取结果缓存；AnalyzerSkill 多次扫描复用；MCP tool 重复调用。
// 线程安全：所有操作 lock，可并发。
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AeroCode.Harness.Cache;

/// <summary>
/// A node in the doubly-linked list. Holds the key (needed when evicting the tail)
/// plus the user-supplied value. Caches <see cref="Prev"/>/<see cref="Next"/> for O(1) removal.
/// </summary>
public sealed class LruNode<TKey, TValue> where TKey : notnull
{
    public required TKey Key { get; init; }
    public required TValue Value { get; set; }
    public LruNode<TKey, TValue>? Prev { get; set; }
    public LruNode<TKey, TValue>? Next { get; set; }
    public long HitCount { get; set; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime LastAccessAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// LRU cache with O(1) get/put/remove. Thread-safe. Tracks hit/miss stats.
/// </summary>
public sealed class LruCache<TKey, TValue> where TKey : notnull
{
    private readonly int _capacity;
    private readonly TimeSpan? _ttl;
    private readonly Dictionary<TKey, LruNode<TKey, TValue>> _map;
    private readonly object _lock = new();

    // Sentinel head/tail: head.Next is the most recently used, tail.Prev is the least.
    private readonly LruNode<TKey, TValue> _head = new() { Key = default!, Value = default! };
    private readonly LruNode<TKey, TValue> _tail = new() { Key = default!, Value = default! };

    private long _hits;
    private long _misses;
    private long _evictions;
    private long _expirations;

    public LruCache(int capacity, TimeSpan? ttl = null)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
        _ttl = ttl;
        _map = new Dictionary<TKey, LruNode<TKey, TValue>>(capacity);
        _head.Next = _tail;
        _tail.Prev = _head;
    }

    public int Count
    {
        get { lock (_lock) return _map.Count; }
    }

    public int Capacity => _capacity;
    public long Hits => Interlocked.Read(ref _hits);
    public long Misses => Interlocked.Read(ref _misses);
    public long Evictions => Interlocked.Read(ref _evictions);
    public long Expirations => Interlocked.Read(ref _expirations);
    public double HitRatio
    {
        get
        {
            var h = Hits; var m = Misses;
            var total = h + m;
            return total == 0 ? 0 : (double)h / total;
        }
    }

    /// <summary>Get a cached value, or return default and don't insert.</summary>
    public bool TryGet(TKey key, out TValue? value)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var node))
            {
                if (IsExpired(node))
                {
                    RemoveNode(node);
                    _map.Remove(key);
                    Interlocked.Increment(ref _expirations);
                    value = default;
                    return false;
                }
                MoveToFront(node);
                node.HitCount++;
                node.LastAccessAt = DateTime.UtcNow;
                Interlocked.Increment(ref _hits);
                value = node.Value;
                return true;
            }
            Interlocked.Increment(ref _misses);
            value = default;
            return false;
        }
    }

    /// <summary>Insert or update. Returns the evicted key, or null if no eviction occurred.</summary>
    public TKey? Put(TKey key, TValue value)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var existing))
            {
                existing.Value = value;
                existing.LastAccessAt = DateTime.UtcNow;
                MoveToFront(existing);
                return default;
            }
            var node = new LruNode<TKey, TValue> { Key = key, Value = value };
            _map[key] = node;
            AddToFront(node);
            TKey? evicted = default;
            if (_map.Count > _capacity)
            {
                var lru = _tail.Prev!;
                RemoveNode(lru);
                _map.Remove(lru.Key);
                Interlocked.Increment(ref _evictions);
                evicted = lru.Key;
            }
            return evicted;
        }
    }

    /// <summary>Get or compute, applying the factory only on miss. Atomic.</summary>
    public TValue GetOrAdd(TKey key, Func<TValue> factory)
    {
        if (TryGet(key, out var cached) && cached is not null) return cached;
        var fresh = factory();
        Put(key, fresh);
        return fresh;
    }

    public async Task<TValue> GetOrAddAsync(TKey key, Func<Task<TValue>> factory)
    {
        if (TryGet(key, out var cached) && cached is not null) return cached;
        var fresh = await factory().ConfigureAwait(false);
        Put(key, fresh);
        return fresh;
    }

    public bool Remove(TKey key)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var node))
            {
                RemoveNode(node);
                _map.Remove(key);
                return true;
            }
            return false;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _map.Clear();
            _head.Next = _tail;
            _tail.Prev = _head;
        }
    }

    public IReadOnlyList<TKey> KeysSnapshot()
    {
        lock (_lock)
        {
            var keys = new List<TKey>(_map.Count);
            for (var n = _head.Next; n != null && n != _tail; n = n.Next)
                keys.Add(n.Key);
            return keys;
        }
    }

    public CacheStats Stats() => new(Hits, Misses, Evictions, Expirations, _map.Count, _capacity, HitRatio);

    private bool IsExpired(LruNode<TKey, TValue> node)
        => _ttl.HasValue && (DateTime.UtcNow - node.CreatedAt) > _ttl.Value;

    private void AddToFront(LruNode<TKey, TValue> n)
    {
        n.Prev = _head;
        n.Next = _head.Next;
        _head.Next!.Prev = n;
        _head.Next = n;
    }

    private void RemoveNode(LruNode<TKey, TValue> n)
    {
        n.Prev!.Next = n.Next;
        n.Next!.Prev = n.Prev;
        n.Prev = null;
        n.Next = null;
    }

    private void MoveToFront(LruNode<TKey, TValue> n)
    {
        RemoveNode(n);
        AddToFront(n);
    }
}

public readonly record struct CacheStats(long Hits, long Misses, long Evictions, long Expirations, int CurrentSize, int Capacity, double HitRatio);

/// <summary>
/// Cache key helper — sha256(tool_name + canonical_args_json).
/// Mirrors Reasonix "CacheKey" pattern for tool-call result dedup.
/// </summary>
public static class CacheKeyBuilder
{
    public static string For(string toolName, IReadOnlyDictionary<string, object?>? args = null)
    {
        var canonical = toolName;
        if (args is not null && args.Count > 0)
        {
            // Sort keys to ensure deterministic ordering.
            var ordered = args.OrderBy(kv => kv.Key, StringComparer.Ordinal);
            var parts = ordered.Select(kv => $"{kv.Key}={SerializeValue(kv.Value)}");
            canonical += "|" + string.Join("&", parts);
        }
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = System.Text.Encoding.UTF8.GetBytes(canonical);
        var hash = sha.ComputeHash(bytes);
        return $"{toolName}:{Convert.ToHexString(hash)[..16]}";
    }

    private static string SerializeValue(object? v)
    {
        if (v is null) return "null";
        if (v is string s) return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        if (v is bool b) return b ? "true" : "false";
        if (v is System.Collections.IEnumerable e && v is not string)
        {
            var items = new List<string>();
            foreach (var item in e) items.Add(SerializeValue(item));
            return "[" + string.Join(",", items) + "]";
        }
        return Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? "null";
    }
}
