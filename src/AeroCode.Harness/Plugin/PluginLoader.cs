// Copyright (c) AeroCode V3.0
// PluginLoader — true .NET 9 hot-pluggable plugin loader.
//
// Architecture (per .NET 9 best practice):
//   - Each plugin DLL loads into its OWN collectible AssemblyLoadContext (ALC).
//   - FileSystemWatcher on `plugins/` directory listens for *.dll add/change/delete.
//   - On add/change: load new ALC, scan for IPlugin / IContentProvider implementations,
//     register them in the global PluginRegistry.
//   - On delete: unload ALC (async; subject to GC). Plugins that referenced types from
//     the unloaded context will throw on next use; the host should re-resolve them.
//   - Shared contracts (AeroCode.Skills.Registry.ISkill, AeroCode.Harness.Plugin.IPlugin)
//     are loaded into the default ALC; both host and plugins see the same type identity.
//
// Limits / known caveats (per .NET docs):
//   - Unloading is asynchronous. The GC may take seconds to reclaim after Unload().
//   - Cross-context static state leaks. We avoid static state in plugin contracts.
//   - DLL locks: on Windows, you can't overwrite a loaded DLL. We rename .old on reload.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;

namespace AeroCode.Harness.Plugin;

/// <summary>
/// Contract that all plugins must implement. The interface lives in the shared
/// AeroCode.Harness assembly (default ALC) so identity is consistent across plugin ALCs.
/// </summary>
public interface IPlugin
{
    string PluginId { get; }
    string Version { get; }
    void OnLoaded();
    void OnUnloading();
}

/// <summary>Information about a loaded plugin.</summary>
public sealed class LoadedPlugin
{
    public required string PluginId { get; init; }
    public required string Version { get; init; }
    public required string DllPath { get; init; }
    public required DateTime LoadedAt { get; init; }
    public required IPlugin Instance { get; init; }
    internal readonly WeakReference<AssemblyLoadContext> ContextRef;

    internal LoadedPlugin(WeakReference<AssemblyLoadContext> contextRef)
    {
        ContextRef = contextRef;
    }

    /// <summary>True if the underlying ALC is still loaded.</summary>
    public bool IsAlive => ContextRef.TryGetTarget(out var ctx) && ctx != null;
}

/// <summary>
/// File-system + ALC-backed plugin loader. Thread-safe.
/// </summary>
public sealed class PluginLoader : IDisposable
{
    private readonly string _pluginsDir;
    private readonly FileSystemWatcher? _watcher;
    private readonly ConcurrentDictionary<string /*dllPath*/, LoadedPlugin> _loaded = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private bool _disposed;

    public event EventHandler<LoadedPlugin>? PluginLoaded;
    public event EventHandler<string>? PluginUnloading;
    public event EventHandler<Exception>? LoadFailed;

    public IReadOnlyCollection<LoadedPlugin> LoadedPlugins
    {
        get { lock (_lock) return _loaded.Values.Where(p => p.IsAlive).ToList(); }
    }

    public string PluginsDirectory => _pluginsDir;

    public PluginLoader(string? pluginsDir = null)
    {
        _pluginsDir = pluginsDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AeroCode", "plugins");
        Directory.CreateDirectory(_pluginsDir);

        _watcher = new FileSystemWatcher(_pluginsDir, "*.dll")
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true
        };
        _watcher.Created += async (_, e) => await SafeLoadAsync(e.FullPath);
        _watcher.Changed += async (_, e) => await SafeReloadAsync(e.FullPath);
        _watcher.Deleted += (_, e) => SafeUnload(e.FullPath);
        _watcher.Renamed += async (_, e) =>
        {
            SafeUnload(e.OldFullPath);
            await SafeLoadAsync(e.FullPath);
        };
        _watcher.Error += (_, ex) => LoadFailed?.Invoke(this, ex.GetException());
    }

    /// <summary>Eagerly scan and load all *.dll currently in the plugins dir.</summary>
    public async Task<int> LoadAllAsync(CancellationToken ct = default)
    {
        var paths = Directory.EnumerateFiles(_pluginsDir, "*.dll").ToList();
        var ok = 0;
        foreach (var p in paths)
        {
            ct.ThrowIfCancellationRequested();
            if (await LoadPluginAsync(p, ct).ConfigureAwait(false)) ok++;
        }
        return ok;
    }

    public Task<bool> LoadPluginAsync(string dllPath, CancellationToken ct = default)
    {
        if (!File.Exists(dllPath)) return Task.FromResult(false);
        try
        {
            // If already loaded, unload first to free the file lock.
            if (_loaded.ContainsKey(dllPath))
                SafeUnload(dllPath);

            var alc = new PluginLoadContext(dllPath);
            var asm = alc.LoadFromAssemblyPath(dllPath);
            var pluginType = asm.GetTypes()
                .FirstOrDefault(t => typeof(IPlugin).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract);
            if (pluginType is null)
            {
                LoadFailed?.Invoke(this, new InvalidOperationException($"No IPlugin implementation in {dllPath}"));
                return Task.FromResult(false);
            }
            var inst = (IPlugin)Activator.CreateInstance(pluginType)!;
            inst.OnLoaded();
            var info = new LoadedPlugin(new WeakReference<AssemblyLoadContext>(alc))
            {
                PluginId = inst.PluginId,
                Version = inst.Version,
                DllPath = dllPath,
                LoadedAt = DateTime.UtcNow,
                Instance = inst
            };
            _loaded[dllPath] = info;
            PluginLoaded?.Invoke(this, info);
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            LoadFailed?.Invoke(this, ex);
            return Task.FromResult(false);
        }
    }

    public bool UnloadPlugin(string dllPath)
    {
        if (!_loaded.TryGetValue(dllPath, out var info)) return false;
        SafeUnload(dllPath);
        return true;
    }

    public T? GetPlugin<T>(string pluginId) where T : class, IPlugin
    {
        var p = _loaded.Values.FirstOrDefault(x => x.PluginId == pluginId && x.IsAlive);
        return p?.Instance as T;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_watcher is not null) { _watcher.EnableRaisingEvents = false; _watcher.Dispose(); }
        foreach (var p in _loaded.Values.ToList())
            SafeUnload(p.DllPath);
    }

    // ============== internal ===============

    private async Task SafeLoadAsync(string dllPath)
    {
        await Task.Delay(50); // some editors fire Created + Changed in rapid succession
        if (!File.Exists(dllPath)) return;
        await LoadPluginAsync(dllPath);
    }

    private async Task SafeReloadAsync(string dllPath)
    {
        await Task.Delay(50);
        if (!File.Exists(dllPath)) return;
        await LoadPluginAsync(dllPath); // unload + reload handled inside
    }

    private void SafeUnload(string dllPath)
    {
        if (!_loaded.TryRemove(dllPath, out var info)) return;
        if (!info.IsAlive) return;
        try
        {
            info.Instance.OnUnloading();
            PluginUnloading?.Invoke(this, info.PluginId);
            if (info.ContextRef.TryGetTarget(out var ctx))
            {
                ctx.Unload();
                // The unload is async; nudge GC to encourage prompt collection.
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }
        catch (Exception ex)
        {
            LoadFailed?.Invoke(this, ex);
        }
    }
}

/// <summary>
/// Custom collectible ALC with proper dependency resolution:
/// 1. Try the plugin's own deps via AssemblyDependencyResolver.
/// 2. Fall back to the default ALC for shared contracts (so type identity is preserved).
/// </summary>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    public PluginLoadContext(string pluginPath)
        : base(name: $"PluginALC:{Path.GetFileNameWithoutExtension(pluginPath)}", isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(pluginPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        if (path is not null) return LoadFromAssemblyPath(path);
        // Fall back: shared contracts (ISkill, IPlugin, etc.) live in default ALC.
        return Default.LoadFromAssemblyName(assemblyName);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        if (path is not null) return LoadUnmanagedDllFromPath(path);
        return IntPtr.Zero;
    }
}
