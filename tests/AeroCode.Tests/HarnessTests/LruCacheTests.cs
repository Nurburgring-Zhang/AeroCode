// Copyright (c) AeroCode V3.0
// LruCache + CacheKeyBuilder + LoopRunner cache-first tests
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AeroCode.Harness.Cache;
using AeroCode.Harness.Loop;
using Xunit;

namespace AeroCode.Tests.HarnessTests;

public class LruCacheTests
{
    [Fact]
    public void Put_TryGet_ReturnsInserted()
    {
        var c = new LruCache<string, int>(3);
        c.Put("a", 1);
        Assert.True(c.TryGet("a", out var v));
        Assert.Equal(1, v);
    }

    [Fact]
    public void Eviction_RemovesLeastRecentlyUsed_WhenOverCapacity()
    {
        var c = new LruCache<string, int>(2);
        c.Put("a", 1);
        c.Put("b", 2);
        c.TryGet("a", out _); // access a → b is now LRU
        c.Put("c", 3);         // should evict b
        Assert.True(c.TryGet("a", out _));
        Assert.True(c.TryGet("c", out _));
        Assert.False(c.TryGet("b", out _));
    }

    [Fact]
    public void HitMiss_Stats_TrackCorrectly()
    {
        var c = new LruCache<string, int>(5);
        c.Put("x", 1);
        c.TryGet("x", out _); // hit
        c.TryGet("y", out _); // miss
        c.TryGet("x", out _); // hit
        Assert.Equal(2, c.Hits);
        Assert.Equal(1, c.Misses);
        Assert.Equal(2.0 / 3.0, c.HitRatio, 3);
    }

    [Fact]
    public async Task GetOrAddAsync_InvokesFactoryOnce_OnCacheMiss()
    {
        var c = new LruCache<string, int>(5);
        var invocations = 0;
        async Task<int> Factory() { invocations++; await Task.Delay(1); return 42; }
        var a = await c.GetOrAddAsync("k", Factory);
        var b = await c.GetOrAddAsync("k", Factory);
        Assert.Equal(42, a);
        Assert.Equal(42, b);
        Assert.Equal(1, invocations);
    }

    [Fact]
    public async Task LoopRunner_CacheFirst_HitsSkipStep()
    {
        var invocations = 0;
        StepAttempt Step = innerCt => { invocations++; return Task.FromResult<string?>(null); };
        var cache = new LruCache<string, string>(10);
        var key = CacheKeyBuilder.For("test", new Dictionary<string, object?> { ["x"] = 1 });
        cache.Put(key, "cached-result");
        var runner = new LoopRunner(maxIterations: 3, cache: cache);
        var r = await runner.RunAsync(Step, cacheKey: key);
        Assert.True(r.Succeeded);
        Assert.Equal(1, r.CacheHits);
        Assert.Equal(0, r.CacheMisses);
        Assert.Equal(0, invocations); // step never ran
    }

    [Fact]
    public async Task LoopRunner_CacheMiss_RunsStep_AndTracksMiss()
    {
        var invocations = 0;
        StepAttempt Step = innerCt => { invocations++; return Task.FromResult<string?>(null); };
        var cache = new LruCache<string, string>(10);
        var key = CacheKeyBuilder.For("test", new Dictionary<string, object?> { ["x"] = 1 });
        var runner = new LoopRunner(maxIterations: 3, cache: cache);
        var r = await runner.RunAsync(Step, cacheKey: key);
        Assert.True(r.Succeeded);
        Assert.Equal(0, r.CacheHits);
        Assert.Equal(1, r.CacheMisses);
        Assert.Equal(1, invocations);
    }

    [Fact]
    public void CacheKeyBuilder_SameArgs_ProduceSameKey()
    {
        var k1 = CacheKeyBuilder.For("fetch", new Dictionary<string, object?> { ["url"] = "http://a", ["max"] = 5 });
        var k2 = CacheKeyBuilder.For("fetch", new Dictionary<string, object?> { ["max"] = 5, ["url"] = "http://a" });
        Assert.Equal(k1, k2); // order-independent
    }

    [Fact]
    public void CacheKeyBuilder_DifferentArgs_ProduceDifferentKeys()
    {
        var k1 = CacheKeyBuilder.For("fetch", new Dictionary<string, object?> { ["url"] = "http://a" });
        var k2 = CacheKeyBuilder.For("fetch", new Dictionary<string, object?> { ["url"] = "http://b" });
        Assert.NotEqual(k1, k2);
    }
}
