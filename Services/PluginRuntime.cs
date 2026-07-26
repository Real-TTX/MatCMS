using MatCMS.Data;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Services;

/// <summary>What plugins registered on their last run (shared across requests, singleton).</summary>
public class PluginRegistry
{
    public sealed record AdminMenuEntry(string Label, string Url, string Icon);

    private readonly object _lock = new();
    public List<AdminMenuEntry> AdminMenu { get; } = new();
    public Dictionary<int, string> Errors { get; } = new();

    public void Reset()
    {
        lock (_lock) { AdminMenu.Clear(); Errors.Clear(); }
    }
}

/// <summary>The API surface handed to plugin scripts as globals.</summary>
public class PluginContext
{
    private readonly PluginRegistry _registry;
    public IServiceProvider Services { get; }

    public PluginContext(PluginRegistry registry, IServiceProvider services)
    {
        _registry = registry;
        Services = services;
    }

    /// <summary>Register an entry in the admin sidebar (label, target URL, emoji icon).</summary>
    public void AddAdminMenu(string label, string url, string icon = "🔌")
        => _registry.AdminMenu.Add(new(label ?? "", string.IsNullOrWhiteSpace(url) ? "#" : url, string.IsNullOrWhiteSpace(icon) ? "🔌" : icon));

    /// <summary>Resolve a framework service, e.g. <c>Service&lt;AppDbContext&gt;()</c>.</summary>
    public T? Service<T>() => Services.GetService(typeof(T)) is T t ? t : default;
}

/// <summary>Compiles and runs enabled plugins, collecting their registrations into the registry.</summary>
public class PluginRunner
{
    private readonly AppDbContext _db;
    private readonly PluginRegistry _registry;
    private readonly IServiceProvider _services;
    private readonly ILogger<PluginRunner> _log;

    public PluginRunner(AppDbContext db, PluginRegistry registry, IServiceProvider services, ILogger<PluginRunner> log)
    {
        _db = db;
        _registry = registry;
        _services = services;
        _log = log;
    }

    public async Task RunAllAsync()
    {
        _registry.Reset();

        List<Models.Plugin> plugins;
        try { plugins = await _db.Plugins.Where(p => p.Enabled).OrderBy(p => p.Id).ToListAsync(); }
        catch { return; } // table may not exist yet on the very first startup

        // Reference every loaded assembly so scripts can use the framework + app types.
        var refs = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrWhiteSpace(a.Location))
            .ToList();
        var options = ScriptOptions.Default
            .WithReferences(refs)
            .WithImports("System", "System.Linq", "System.Collections.Generic", "System.Threading.Tasks",
                         "MatCMS.Services", "MatCMS.Data", "MatCMS.Models", "Microsoft.EntityFrameworkCore");

        var ctx = new PluginContext(_registry, _services);
        foreach (var p in plugins)
        {
            try
            {
                await CSharpScript.RunAsync(p.Code ?? "", options, globals: ctx, globalsType: typeof(PluginContext));
            }
            catch (Exception ex)
            {
                _registry.Errors[p.Id] = ex.Message;
                _log.LogWarning(ex, "Plugin '{Name}' failed", p.Name);
            }
        }
    }
}
