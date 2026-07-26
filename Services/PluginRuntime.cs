using MatCMS.Data;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Services;

/// <summary>Request context passed to plugin admin-page and block render callbacks (at request time).</summary>
public class PluginRequest
{
    public IServiceProvider Services { get; init; } = default!;
    public string Method { get; init; } = "GET";
    public IReadOnlyDictionary<string, string> Query { get; init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, string> Form { get; init; } = new Dictionary<string, string>();
    /// <summary>Block field data (JSON) — only for block render callbacks.</summary>
    public string Data { get; init; } = "{}";

    public PluginRegistry Registry { get; init; } = default!;
    public T? Service<T>() => Services.GetService(typeof(T)) is T t ? t : default;
    public string Q(string key) => Query.TryGetValue(key, out var v) ? v : "";
    public string F(string key) => Form.TryGetValue(key, out var v) ? v : "";
    public bool IsPost => string.Equals(Method, "POST", StringComparison.OrdinalIgnoreCase);
    /// <summary>Write a line to the plugin log (visible under Plugins in the admin).</summary>
    public void Log(object? message) => Registry?.AddLog(message?.ToString() ?? "");
}

/// <summary>What plugins registered on their last run (shared across requests, singleton).</summary>
public class PluginRegistry
{
    public sealed record AdminMenuEntry(string Label, string Url, string Icon);
    public sealed record PluginBlock(string Type, string Name, string Description, Func<PluginRequest, string> Render);

    private readonly object _lock = new();
    public List<AdminMenuEntry> AdminMenu { get; } = new();
    public List<PluginBlock> Blocks { get; } = new();
    public Dictionary<string, Func<PluginRequest, string>> Pages { get; } = new();
    public Dictionary<int, string> Errors { get; } = new();

    // Rolling log written by plugins via Log(...); newest last. Survives re-runs.
    private readonly List<string> _log = new();
    public IReadOnlyList<string> Log { get { lock (_lock) return _log.ToList(); } }

    public void AddLog(string message)
    {
        lock (_lock)
        {
            _log.Add($"{DateTime.Now:HH:mm:ss}  {message}");
            if (_log.Count > 200) _log.RemoveRange(0, _log.Count - 200);
        }
    }

    /// <summary>Clears registrations before a re-run (keeps the log so output history survives).</summary>
    public void Reset()
    {
        lock (_lock) { AdminMenu.Clear(); Blocks.Clear(); Pages.Clear(); Errors.Clear(); }
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

    /// <summary>Register an admin page served at <c>/admin/plugin/{key}</c>. The callback returns HTML.</summary>
    public void AddAdminPage(string key, Func<PluginRequest, string> handler)
    {
        if (!string.IsNullOrWhiteSpace(key)) _registry.Pages[key] = handler;
    }

    /// <summary>Register a content block. The render callback gets the block's data + request services.</summary>
    public void AddBlock(string type, string name, string description, Func<PluginRequest, string> render)
    {
        if (!string.IsNullOrWhiteSpace(type)) _registry.Blocks.Add(new(type, name ?? type, description ?? "", render));
    }

    /// <summary>Resolve a framework service, e.g. <c>Service&lt;AppDbContext&gt;()</c>.</summary>
    public T? Service<T>() => Services.GetService(typeof(T)) is T t ? t : default;

    /// <summary>Write a line to the plugin log (visible under Plugins in the admin).</summary>
    public void Log(object? message) => _registry.AddLog(message?.ToString() ?? "");
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
