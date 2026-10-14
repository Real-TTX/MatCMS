using System.Reflection;
using System.Runtime.Loader;

namespace MatCMS.Services.Plugins;

/// <summary>
/// The collectible load context of ONE native plugin. Only the plugin's own assemblies load here;
/// everything the host and the plugin must agree on — the plugin API, the framework, EF and the other
/// libraries the host already runs — comes from the host's default context. That is what keeps a type
/// from the API the same type on both sides: a plugin that shipped its own copy of
/// <c>MatCMS.Plugin.Abstractions</c> would implement an <c>IPlugin</c> the host does not recognise.
/// </summary>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly string _dir;
    private readonly AssemblyDependencyResolver? _resolver;

    /// <summary>The assembly that IS the plugin API. Never loaded from a plugin folder.</summary>
    private static readonly string ApiAssembly = typeof(MatCMS.Plugin.IPlugin).Assembly.GetName().Name!;

    public PluginLoadContext(string key, string entryPath) : base("plugin:" + key, isCollectible: true)
    {
        _dir = Path.GetDirectoryName(entryPath)!;
        // A .deps.json beside the entry makes the resolver exact; without one the folder is probed by name.
        try { _resolver = File.Exists(Path.ChangeExtension(entryPath, ".deps.json")) ? new AssemblyDependencyResolver(entryPath) : null; }
        catch { _resolver = null; }
    }

    protected override Assembly? Load(AssemblyName name)
    {
        if (string.IsNullOrEmpty(name.Name)) return null;
        if (name.Name == ApiAssembly) return null;

        // Already running in the host (EF, Microsoft.Extensions.*, System.*, ImageSharp, …): use THAT one.
        // Two copies of EF would be two different DbContext base classes.
        foreach (var a in Default.Assemblies)
            if (string.Equals(a.GetName().Name, name.Name, StringComparison.OrdinalIgnoreCase)) return a;
        try { return Default.LoadFromAssemblyName(name); } catch { /* not a host assembly — the plugin brings it */ }

        var path = _resolver?.ResolveAssemblyToPath(name);
        if (path is null)
        {
            var guess = Path.Combine(_dir, name.Name + ".dll");
            if (File.Exists(guess)) path = guess;
        }
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver?.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }
}
