using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using MatCMS.Plugin;

namespace MatCMS.Services.Plugins;

/// <summary>
/// Loads native plugins into collectible load contexts and unloads them again. A singleton: the contexts
/// outlive any request.
/// <para>What "unload" promises, because .NET decides the rest: the plugin's registrations are gone at once
/// (<see cref="PluginRegistry.Remove"/>), and the load context is released. Whether the MEMORY comes back
/// depends on nothing in the host still pointing at the plugin — EF's static caches and reflection-based
/// <c>System.Text.Json</c> over a plugin's own types are the known holders. So after every unload the host
/// checks with a <see cref="WeakReference"/> and, when the context is still alive, remembers it as
/// <i>lingering</i>: the plugin is off, its DLL is replaced by the next load, and the admin sees "restart to free
/// the memory" instead of a silent leak.</para>
/// </summary>
public sealed class NativePluginHost
{
    private sealed record Loaded(string Key, string Version, PluginLoadContext Context);

    private readonly Dictionary<string, Loaded> _loaded = new(StringComparer.Ordinal);
    private readonly List<(string Key, WeakReference Ref)> _lingering = new();
    private readonly object _lock = new();
    private readonly ILogger<NativePluginHost> _log;

    public NativePluginHost(ILogger<NativePluginHost> log) => _log = log;

    /// <summary>True while the native plugin has a loaded context.</summary>
    public bool IsLoaded(string key) { lock (_lock) return _loaded.ContainsKey(key); }

    /// <summary>True when a context of this plugin was unloaded but the runtime has not been able to free it —
    /// the memory comes back with the next restart.</summary>
    public bool IsLingering(string key)
    {
        lock (_lock)
        {
            _lingering.RemoveAll(l => !l.Ref.IsAlive);
            return _lingering.Any(l => l.Key == key);
        }
    }

    /// <summary>Hex SHA-256 of a file.</summary>
    public static string HashOf(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
    }

    /// <summary>
    /// Loads the plugin from <paramref name="srcDir"/> and publishes its registrations in ONE step, replacing a
    /// previous version atomically. If anything fails, nothing changes: the previous version keeps running.
    /// </summary>
    /// <param name="p">The plugin row.</param>
    /// <param name="info">The plugin's identity.</param>
    /// <param name="srcDir">The folder with the assemblies.</param>
    /// <param name="registry">The registry to publish to.</param>
    /// <param name="beforePublish">Runs once the plugin is configured and BEFORE it is published (tables, installers); if it throws,
    /// nothing is published and the previous version stays.</param>
    public void Load(Models.Plugin p, PluginInfo info, string srcDir, PluginRegistry registry, Action<PluginRegistry.Slice>? beforePublish = null)
    {
        // An update: the previous version is detached INSIDE LoadInto (after the new one is live) and only its
        // weak reference comes back, so no frame up here keeps the old context alive through the collection.
        var previous = LoadInto(p, info, srcDir, registry, beforePublish);
        if (previous is not null) Collect(previous, p.Key, registry);
    }

    /// <summary>Unloads a plugin: removes everything it registered and releases its context. True when the
    /// memory was freed, false when the context lingers (or the plugin was not loaded natively).</summary>
    public bool Unload(string key, PluginRegistry registry)
    {
        registry.Remove(key);
        var weak = DetachByKey(key);
        return weak is null || Collect(weak, key, registry);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private WeakReference? DetachByKey(string key)
    {
        Loaded? old;
        lock (_lock) { _loaded.Remove(key, out old); }
        return old is null ? null : Detach(old);
    }

    // The two steps below are NOT inlined, on purpose: a local that still points at the context in the frame
    // of the caller keeps it alive through the very collection that is meant to prove it can be freed.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private WeakReference? LoadInto(Models.Plugin p, PluginInfo info, string srcDir, PluginRegistry registry, Action<PluginRegistry.Slice>? beforePublish)
    {
        var path = Path.Combine(srcDir, p.Entry);
        if (string.IsNullOrWhiteSpace(p.Entry) || !File.Exists(path))
            throw new FileNotFoundException($"Die Hauptdatei „{p.Entry}“ des Plugins fehlt in {srcDir}.");
        // A DLL that is not the one imported does not run: a changed byte on the volume is a changed program.
        if (!string.IsNullOrEmpty(p.Sha256) && !string.Equals(HashOf(path), p.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Die Prüfsumme der Plugin-DLL stimmt nicht mit der importierten überein.");

        var context = new PluginLoadContext(p.Key, path);
        try
        {
            var assembly = context.LoadFromAssemblyPath(path);
            var types = assembly.GetExportedTypes()
                .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IPlugin).IsAssignableFrom(t)).ToList();
            if (types.Count != 1)
                throw new InvalidOperationException(types.Count == 0
                    ? "Die DLL enthält keine öffentliche Klasse, die IPlugin implementiert."
                    : "Die DLL enthält mehrere Klassen, die IPlugin implementieren — erlaubt ist genau eine.");

            var plugin = (IPlugin)Activator.CreateInstance(types[0])!;
            var slice = registry.Begin(p.Key, p.Id);
            PluginStringFiles.FromDirectory(slice, srcDir, msg => registry.AddLog(p.Key, "Sprachdatei: " + msg));
            plugin.Configure(new PluginBuilder(registry, slice, info));
            beforePublish?.Invoke(slice);
            registry.Publish(slice);
        }
        catch
        {
            context.Unload();   // nothing of this attempt may stay; the previous version (if any) is untouched
            throw;
        }

        Loaded? previous;
        lock (_lock)
        {
            _loaded.Remove(p.Key, out previous);
            _loaded[p.Key] = new Loaded(p.Key, p.Version, context);
        }
        return previous is null ? null : Detach(previous);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference Detach(Loaded l)
    {
        var weak = new WeakReference(l.Context, trackResurrection: true);
        l.Context.Unload();
        // EF's provider cache is the one known holder the host can clear. After Unload, before the collections.
        EfInternals.ClearServiceProviderCache();
        return weak;
    }

    private bool Collect(WeakReference context, string key, PluginRegistry registry)
    {
        for (var i = 0; i < 10 && context.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            if (context.IsAlive) Thread.Sleep(30);
        }
        if (!context.IsAlive) return true;

        lock (_lock) _lingering.Add((key, context));
        registry.AddLog(key, "entladen — der Speicher wird erst mit dem nächsten Neustart frei (etwas hält die DLL noch fest)");
        _log.LogWarning("Native plugin '{Key}' was unloaded but its load context is still referenced", key);
        return false;
    }
}
