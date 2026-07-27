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

    // Raw HTML fragments plugins asked to inject site-wide, before </head> / </body>. Written by
    // RunAllAsync (admin actions) and read by _Layout on every public request → all access is locked,
    // and the getters return a snapshot so the view can enumerate safely during a concurrent re-run.
    private readonly List<string> _headHtml = new();
    private readonly List<string> _bodyHtml = new();
    public IReadOnlyList<string> HeadHtml { get { lock (_lock) return _headHtml.ToArray(); } }
    public IReadOnlyList<string> BodyHtml { get { lock (_lock) return _bodyHtml.ToArray(); } }
    public void AddHead(string html) { lock (_lock) _headHtml.Add(html); }
    public void AddBody(string html) { lock (_lock) _bodyHtml.Add(html); }

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
        lock (_lock) { AdminMenu.Clear(); Blocks.Clear(); Pages.Clear(); Errors.Clear(); _headHtml.Clear(); _bodyHtml.Clear(); }
    }
}

/// <summary>The API surface handed to plugin scripts as globals.</summary>
public class PluginContext
{
    private readonly PluginRegistry _registry;
    public IServiceProvider Services { get; }

    /// <summary>This plugin's stable slug — also the name of its asset folder / URL prefix.</summary>
    public string Key { get; }

    public PluginContext(PluginRegistry registry, IServiceProvider services, string key = "")
    {
        _registry = registry;
        Services = services;
        Key = key ?? "";
    }

    /// <summary>Public URL of a file in THIS plugin's asset folder, e.g. AssetUrl("app.js") → /plugin-assets/{key}/app.js.</summary>
    public string AssetUrl(string file)
    {
        var name = System.IO.Path.GetFileName((file ?? "").Trim());
        return string.IsNullOrEmpty(Key) || string.IsNullOrEmpty(name) ? "" : $"/plugin-assets/{Key}/{name}";
    }

    /// <summary>Injects raw HTML site-wide, just before &lt;/head&gt;.</summary>
    public void AddHeadHtml(string html) { if (!string.IsNullOrWhiteSpace(html)) _registry.AddHead(html); }

    /// <summary>Injects raw HTML site-wide, just before &lt;/body&gt;.</summary>
    public void AddBodyHtml(string html) { if (!string.IsNullOrWhiteSpace(html)) _registry.AddBody(html); }

    /// <summary>
    /// Loads a JS file from this plugin's asset folder site-wide. Scripts run in include order.
    /// By default the tag is NOT deferred, so a library loaded here is available to any inline init
    /// the plugin emits afterwards (via AddBodyHtml or a block). Pass defer: true for independent
    /// scripts that may run after the document parses. Set inHead to load it in &lt;head&gt;.
    /// </summary>
    public void IncludeScript(string file, bool inHead = false, bool defer = false)
    {
        var url = AssetUrl(file);
        if (url.Length == 0) return;
        var tag = $"<script src=\"{System.Net.WebUtility.HtmlEncode(url)}\"{(defer ? " defer" : "")}></script>";
        if (inHead) _registry.AddHead(tag); else _registry.AddBody(tag);
    }

    /// <summary>Loads a CSS file from this plugin's asset folder site-wide (in &lt;head&gt;).</summary>
    public void IncludeStyle(string file)
    {
        var url = AssetUrl(file);
        if (url.Length == 0) return;
        _registry.AddHead($"<link rel=\"stylesheet\" href=\"{System.Net.WebUtility.HtmlEncode(url)}\" />");
    }

    /// <summary>
    /// Runs a one-time data migration for THIS plugin. If the plugin's stored data version is lower
    /// than <paramref name="toVersion"/>, executes <paramref name="migrate"/> and then records the new
    /// data version. Idempotent: each version's migration runs at most once, even though plugin code
    /// runs on every startup/save. Use it to evolve a plugin's stored data across updates, e.g.
    /// <c>Migrate("2", () =&gt; { /* rewrite plugin.todos to the new shape */ });</c>
    /// </summary>
    public void Migrate(string toVersion, Action migrate)
    {
        if (string.IsNullOrWhiteSpace(toVersion) || string.IsNullOrEmpty(Key) || migrate is null) return;
        var db = Service<AppDbContext>();
        var p = db?.Plugins.FirstOrDefault(x => x.Key == Key);
        if (db is null || p is null) return;
        if (CompareVersions(p.DataVersion, toVersion) >= 0) return; // already at/after this version

        // Run the migration body + the version bump atomically. If the body throws, roll back and drop
        // any partial tracked writes so nothing half-migrated is committed and DataVersion stays put
        // (the migration will simply be retried next run). Errors surface via RunAllAsync's catch.
        using var tx = db.Database.BeginTransaction();
        try
        {
            migrate();
            p.DataVersion = toVersion.Trim();
            db.SaveChanges();
            tx.Commit();
        }
        catch
        {
            try { tx.Rollback(); } catch { /* ignore */ }
            db.ChangeTracker.Clear();
            throw;
        }
    }

    /// <summary>Compares version strings (System.Version when parseable, else ordinal). Empty = lowest.</summary>
    private static int CompareVersions(string? a, string? b)
    {
        static string Norm(string? s)
        {
            s = (s ?? "").Trim();
            if (s.Length == 0) s = "0";
            return s.Contains('.') ? s : s + ".0";
        }
        if (Version.TryParse(Norm(a), out var va) && Version.TryParse(Norm(b), out var vb))
            return va.CompareTo(vb);
        return string.CompareOrdinal((a ?? "").Trim(), (b ?? "").Trim());
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

    // Serializes runs process-wide: RunAllAsync clears + repopulates the singleton registry and runs
    // migrations that write to the DB, so two concurrent runs would corrupt the registry and could
    // double-apply a migration. One run at a time.
    private static readonly SemaphoreSlim _gate = new(1, 1);

    public PluginRunner(AppDbContext db, PluginRegistry registry, IServiceProvider services, ILogger<PluginRunner> log)
    {
        _db = db;
        _registry = registry;
        _services = services;
        _log = log;
    }

    public async Task RunAllAsync()
    {
        await _gate.WaitAsync();
        try
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

            foreach (var p in plugins)
            {
                // Each plugin gets its own context carrying its Key, so AssetUrl/IncludeScript resolve to
                // that plugin's own asset folder (/plugin-assets/{key}/…).
                var ctx = new PluginContext(_registry, _services, p.Key);
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
        finally
        {
            _gate.Release();
        }
    }
}
