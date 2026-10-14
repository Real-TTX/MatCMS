using System.Text;
using MatCMS.Data;
using MatCMS.Plugin;
using MatCMS.Services.Plugins;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Services;

/// <summary>Request context passed to plugin admin-page and block render callbacks (at request time).</summary>
public class PluginRequest
{
    public IServiceProvider Services { get; init; } = default!;
    public string Method { get; init; } = "GET";
    /// <summary>Request path (e.g. "/leser") — set for block renders and public endpoints. Handy for
    /// building a same-page return URL after a public POST.</summary>
    public string Path { get; init; } = "";
    public IReadOnlyDictionary<string, string> Query { get; init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, string> Form { get; init; } = new Dictionary<string, string>();
    /// <summary>Block field data (JSON) — only for block render callbacks.</summary>
    public string Data { get; init; } = "{}";

    /// <summary>True when the visitor is signed in to the back office as an administrator (a block may show a preview of drafts to them).</summary>
    public bool IsAdmin { get; init; }

    /// <summary>Pre-rendered hidden antiforgery &lt;input&gt; for admin POST forms (empty on public/
    /// anonymous requests). Include it in any custom &lt;form method="post"&gt;, or — simpler — use
    /// <see cref="Ui"/>, whose Form/ActionButton helpers add it for you.</summary>
    public string Antiforgery { get; init; } = "";

    public PluginRegistry Registry { get; init; } = default!;
    public T? Service<T>() => Services.GetService(typeof(T)) is T t ? t : default;
    public string Q(string key) => Query.TryGetValue(key, out var v) ? v : "";
    public string F(string key) => Form.TryGetValue(key, out var v) ? v : "";
    public bool IsPost => string.Equals(Method, "POST", StringComparison.OrdinalIgnoreCase);
    /// <summary>Convenience: the posted <c>action</c> field — handy for dispatching POST handlers.</summary>
    public string Action => F("action");
    /// <summary>A tiny admin-UI builder (cards, alerts, POST forms/buttons that auto-carry the
    /// antiforgery token). Access as <c>req.Ui</c>. See <see cref="AdminUi"/>.</summary>
    public AdminUi Ui => new(Antiforgery);
    /// <summary>Write a line to the plugin log (visible under Plugins in the admin).</summary>
    public void Log(object? message) => Registry?.AddLog(message?.ToString() ?? "");
}

/// <summary>
/// A small helper for building admin-page HTML from plugins without hand-writing boilerplate: cards,
/// alerts, tables, and — crucially — POST forms/buttons that automatically include the antiforgery
/// token, so plugin admin actions POST correctly to the auto-validated <c>/admin</c> Razor Pages.
/// Obtained per request via <c>req.Ui</c>. All text arguments are HTML-encoded; pass ready-made markup
/// only through the explicit <c>*Html</c> parameters.
/// </summary>
public sealed class AdminUi
{
    private readonly string _csrf; // pre-rendered hidden antiforgery field ("" on public requests)
    public AdminUi(string antiforgeryField) => _csrf = antiforgeryField ?? "";

    private static string Enc(string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");

    /// <summary>The raw hidden antiforgery &lt;input&gt; — drop it into any custom POST form.</summary>
    public string Token => _csrf;

    /// <summary>Page header: an optional &lt;h1&gt; and/or a muted subtitle. Omit the title when the admin
    /// topbar already shows it (avoids a duplicate heading).</summary>
    public string PageHead(string? subtitle = null, string? title = null) =>
        "<div class=\"page-head\">" +
        (string.IsNullOrWhiteSpace(title) ? "" : "<h1>" + Enc(title) + "</h1>") +
        (string.IsNullOrWhiteSpace(subtitle) ? "" : "<p class=\"muted\">" + Enc(subtitle) + "</p>") +
        "</div>";

    /// <summary>A card panel with an optional heading. <paramref name="innerHtml"/> is emitted verbatim.</summary>
    public string Card(string innerHtml, string? title = null) =>
        "<div class=\"card\">" + (string.IsNullOrWhiteSpace(title) ? "" : "<h2>" + Enc(title) + "</h2>") + (innerHtml ?? "") + "</div>";

    /// <summary>A status banner. <paramref name="type"/>: info | success | error | warning.</summary>
    public string Alert(string message, string type = "info") =>
        "<div class=\"alert alert-" + Enc(type) + "\">" + Enc(message) + "</div>";

    /// <summary>A POST &lt;form&gt; that carries the antiforgery token plus any <paramref name="hidden"/>
    /// fields; <paramref name="innerHtml"/> is your own markup (inputs, buttons). Posts to the current page.</summary>
    public string Form(string innerHtml, IDictionary<string, string>? hidden = null, string? cssClass = null)
    {
        var sb = new StringBuilder("<form method=\"post\"");
        if (!string.IsNullOrWhiteSpace(cssClass)) sb.Append(" class=\"").Append(Enc(cssClass)).Append('"');
        sb.Append('>').Append(_csrf);
        if (hidden != null)
            foreach (var kv in hidden)
                sb.Append("<input type=\"hidden\" name=\"").Append(Enc(kv.Key)).Append("\" value=\"").Append(Enc(kv.Value)).Append("\"/>");
        sb.Append(innerHtml ?? "").Append("</form>");
        return sb.ToString();
    }

    /// <summary>A compact row action: a mini POST form rendering one submit button that carries the token
    /// plus <paramref name="hidden"/> fields. <paramref name="confirm"/> asks first — in the admin's own dialog
    /// (data-confirm, mat-dialogs.js), never the browser's confirm().</summary>
    public string ActionButton(string label, IDictionary<string, string> hidden, string? cssClass = null, string? confirm = null)
    {
        var ask = string.IsNullOrWhiteSpace(confirm) ? "" : " data-confirm=\"" + Enc(confirm) + "\"";
        var btn = "<button type=\"submit\" class=\"btn btn-sm " + Enc(cssClass) + "\"" + ask + ">" + Enc(label) + "</button>";
        return Form(btn, hidden, "inline-form");
    }
}

/// <summary>The API surface handed to plugin scripts as globals.</summary>
public class PluginContext
{
    private readonly PluginRegistry _registry;
    private readonly PluginRegistry.Slice _slice;
    public IServiceProvider Services { get; }

    /// <summary>The plugin API of this host: the same <c>IPluginBuilder</c> a native plugin receives in
    /// <c>Configure</c>. New scripts register through it (<c>Plugin.Route(...)</c>, <c>Plugin.Admin.Section(...)</c>);
    /// the older <c>AddAdminMenu</c>/<c>AddBlock</c>/… members below stay for the plugins already written.</summary>
    public IPluginBuilder Plugin { get; }

    /// <summary>This plugin's stable slug — also the name of its asset folder / URL prefix.</summary>
    public string Key { get; }

    private readonly IReadOnlyDictionary<string, string> _config;

    public PluginContext(PluginRegistry registry, PluginRegistry.Slice slice, IServiceProvider services, PluginInfo info,
        IReadOnlyDictionary<string, string>? config = null)
    {
        _registry = registry;
        _slice = slice;
        Services = services;
        Key = info.Key ?? "";
        _config = config ?? new Dictionary<string, string>();
        Plugin = new PluginBuilder(registry, slice, info);
    }

    /// <summary>Reads an admin-set configuration value (from the plugin's "Konfiguration"), or "" if unset.</summary>
    public string Config(string key) => _config.TryGetValue(key ?? "", out var v) ? v : "";

    /// <summary>Public URL of a file in THIS plugin's asset folder, e.g. AssetUrl("app.js") → /plugin-assets/{key}/app.js.</summary>
    public string AssetUrl(string file)
    {
        var name = System.IO.Path.GetFileName((file ?? "").Trim());
        return string.IsNullOrEmpty(Key) || string.IsNullOrEmpty(name) ? "" : $"/plugin-assets/{Key}/{name}";
    }

    /// <summary>Injects raw HTML site-wide, just before &lt;/head&gt;.</summary>
    public void AddHeadHtml(string html) => Plugin.Head(html);

    /// <summary>Injects raw HTML site-wide, just before &lt;/body&gt;.</summary>
    public void AddBodyHtml(string html) => Plugin.Body(html);

    /// <summary>
    /// Loads a JS file from this plugin's asset folder site-wide. Scripts run in include order.
    /// By default the tag is NOT deferred, so a library loaded here is available to any inline init
    /// the plugin emits afterwards (via AddBodyHtml or a block). Pass defer: true for independent
    /// scripts that may run after the document parses. Set inHead to load it in &lt;head&gt;.
    /// </summary>
    public void IncludeScript(string file, bool inHead = false, bool defer = false) => Plugin.Script(file, inHead, defer);

    /// <summary>Loads a CSS file from this plugin's asset folder site-wide (in &lt;head&gt;).</summary>
    public void IncludeStyle(string file) => Plugin.Style(file);

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
    internal static int CompareVersions(string? a, string? b)
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
    public void AddAdminMenu(string label, string url, string icon = "ti-plug")
        => _slice.Menu.Add(new(label ?? "", string.IsNullOrWhiteSpace(url) ? "#" : url, string.IsNullOrWhiteSpace(icon) ? "ti-plug" : icon) { Owner = Key });

    /// <summary>Register an admin page served at <c>/admin/plugin/{key}</c>. The callback returns HTML.</summary>
    public void AddAdminPage(string key, Func<PluginRequest, string> handler)
    {
        if (!string.IsNullOrWhiteSpace(key)) _slice.Pages[key] = handler;
    }

    /// <summary>Register a PUBLIC (anonymous) endpoint served at <c>/plugin/{endpointKey}</c>. GET returns
    /// the callback's HTML; POST runs the callback (a mutating action) and then redirects — to the form's
    /// <c>__return</c> field when it is a safe local path, otherwise to "/". Always include a <c>__return</c>
    /// hidden field in your form. Use for visitor submissions (reviews, comments). The callback gets
    /// Form/Query/Method/Path on the request. The endpoint is rate-limited per client IP.</summary>
    public void AddPublicPage(string endpointKey, Func<PluginRequest, string> handler)
    {
        if (!string.IsNullOrWhiteSpace(endpointKey) && handler is not null) _slice.PublicPages[endpointKey] = handler;
    }

    /// <summary>Register a content block. The render callback gets the block's data + request services.</summary>
    public void AddBlock(string type, string name, string description, Func<PluginRequest, string> render)
        => AddBlock(type, name, description, render, null);

    /// <summary>
    /// Register a content block with editable fields shown in the block editor. <paramref name="fieldsJson"/>
    /// is a JSON array of field definitions: <c>[{ "id", "label", "type", "placeholder", "help", "default",
    /// "options":[{"value","label"}] }]</c>. Supported types: text, textarea, richtext, image, url, select, list.
    /// </summary>
    public void AddBlock(string type, string name, string description, Func<PluginRequest, string> render, string? fieldsJson)
    {
        if (!string.IsNullOrWhiteSpace(type))
            _slice.Blocks.Add(new(type, name ?? type, description ?? "", render) { Fields = ParseBlockFields(fieldsJson), Owner = Key });
    }

    private static List<MatCMS.Content.BlockField> ParseBlockFields(string? json)
    {
        var list = new List<MatCMS.Content.BlockField>();
        if (string.IsNullOrWhiteSpace(json)) return list;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array) return list;
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                string S(string k) => el.TryGetProperty(k, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? (v.GetString() ?? "") : "";
                var id = S("id");
                if (string.IsNullOrWhiteSpace(id)) continue;
                var f = new MatCMS.Content.BlockField
                {
                    Id = id,
                    Label = string.IsNullOrWhiteSpace(S("label")) ? id : S("label"),
                    Type = ParseFieldType(S("type")),
                    Placeholder = string.IsNullOrWhiteSpace(S("placeholder")) ? null : S("placeholder"),
                    Help = string.IsNullOrWhiteSpace(S("help")) ? null : S("help"),
                    Default = string.IsNullOrWhiteSpace(S("default")) ? null : S("default")
                };
                if (el.TryGetProperty("options", out var opts) && opts.ValueKind == System.Text.Json.JsonValueKind.Array)
                    foreach (var o in opts.EnumerateArray())
                    {
                        if (o.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                        var ov = o.TryGetProperty("value", out var vv) ? (vv.GetString() ?? "") : "";
                        var ol = o.TryGetProperty("label", out var ll) ? (ll.GetString() ?? ov) : ov;
                        f.Options.Add(new MatCMS.Content.SelectOption(ov, ol));
                    }
                list.Add(f);
            }
        }
        catch { }
        return list;
    }

    private static MatCMS.Content.FieldType ParseFieldType(string? t) => (t ?? "").Trim().ToLowerInvariant() switch
    {
        "textarea" => MatCMS.Content.FieldType.Textarea,
        "richtext" => MatCMS.Content.FieldType.RichText,
        "image" => MatCMS.Content.FieldType.Image,
        "url" => MatCMS.Content.FieldType.Url,
        "select" => MatCMS.Content.FieldType.Select,
        "list" => MatCMS.Content.FieldType.List,
        _ => MatCMS.Content.FieldType.Text
    };

    /// <summary>Resolve a framework service, e.g. <c>Service&lt;AppDbContext&gt;()</c>.</summary>
    public T? Service<T>() => Services.GetService(typeof(T)) is T t ? t : default;

    /// <summary>Write a line to the plugin log (visible under Plugins in the admin).</summary>
    public void Log(object? message) => _registry.AddLog(Key, message?.ToString() ?? "");
}

/// <summary>
/// Loads plugins into the registry: the enabled ones at startup, ONE of them when it is saved, switched
/// on, or updated, and removes one when it is switched off or deleted. Script and (later) native plugins
/// go through the same steps and end in the same <see cref="PluginRegistry.Slice"/>.
/// </summary>
public class PluginRunner
{
    private readonly AppDbContext _db;
    private readonly PluginRegistry _registry;
    private readonly IServiceProvider _services;
    private readonly ILogger<PluginRunner> _log;
    private readonly NativePluginHost _native;
    private readonly IWebHostEnvironment _env;
    private readonly PluginDataService _data;

    /// <summary>Whether the plugins that ship with the CMS (the blog) are started. Always, except in the plugin-machinery tests, which
    /// want the registry to hold only what a test put there.</summary>
    internal bool BuiltIns { get; set; } = true;

    // Serializes loads process-wide: a load builds and publishes a slice and may run data migrations
    // that write to the DB, so two concurrent loads of the same plugin could double-apply a migration.
    // Requests are not blocked: they read the published snapshot.
    private static readonly SemaphoreSlim _gate = new(1, 1);

    public PluginRunner(AppDbContext db, PluginRegistry registry, IServiceProvider services, ILogger<PluginRunner> log,
        NativePluginHost native, IWebHostEnvironment env, PluginDataService data)
    {
        _data = data;
        _native = native;
        _env = env;
        _db = db;
        _registry = registry;
        _services = services;
        _log = log;
    }

    /// <summary>Loads every enabled plugin and unloads the rest — startup, restore, cleanup. Each plugin
    /// is loaded on its own: one that fails does not stop the others.</summary>
    public async Task RunAllAsync()
    {
        await _gate.WaitAsync();
        try
        {
            List<Models.Plugin> plugins;
            try
            {
                // The built-in plugins (the blog) have a row like any other so that on/off and settings work the same way.
                if (BuiltIns) foreach (var key in await BuiltInPlugins.EnsureRowsAsync(_db))
                    _registry.AddLog(key, "Ein Plugin mit diesem Schlüssel existiert bereits — das eingebaute wird nicht gestartet.");
                plugins = await _db.Plugins.AsNoTracking().OrderBy(p => p.Id).ToListAsync();
            }
            catch { return; } // table may not exist yet on the very first startup

            var keep = plugins.Where(p => p.Enabled).Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var key in _registry.LoadedKeys.Where(k => !keep.Contains(k)).ToList()) UnloadCore(key);
            foreach (var p in plugins.Where(p => !p.Enabled)) _registry.ClearError(p.Id);

            var options = ScriptOptionsFor();
            foreach (var p in plugins.Where(p => p.Enabled)) await LoadCoreAsync(p, options);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Loads (or reloads) ONE plugin from the database — after it was saved, enabled or updated.
    /// A disabled or missing plugin is unloaded instead. No other plugin is touched.</summary>
    public async Task ReloadAsync(int id)
    {
        await _gate.WaitAsync();
        try
        {
            var p = await _db.Plugins.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
            if (p is null) return;
            if (!p.Enabled) { UnloadCore(p.Key); _registry.ClearError(p.Id); return; }
            await LoadCoreAsync(p, ScriptOptionsFor());
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Unloads one plugin by key — it was switched off or deleted. Idempotent.</summary>
    public async Task UnloadAsync(string key)
    {
        await _gate.WaitAsync();
        try { UnloadCore(key); }
        finally { _gate.Release(); }
    }

    private void UnloadCore(string key)
    {
        // Both kinds end here: a script has only its registrations to remove, a native plugin also a load
        // context to release.
        var wasNative = _native.IsLoaded(key);
        var had = wasNative ? _native.Unload(key, _registry) : _registry.Remove(key);
        if (wasNative || had) _registry.AddLog(key, "entladen");
    }

    private sealed class NativeNotAllowed(string message) : InvalidOperationException(message);

    private async Task LoadNativeAsync(Models.Plugin p)
    {
        try
        {
            // Compiled code from outside runs with every right of this process. Whoever operates the site
            // switches that on — an import or a cloud rollout can bring the files, never the permission.
            var allowed = await _db.SiteSettings.AsNoTracking()
                .AnyAsync(s => s.Key == SettingKeys.AllowNativePlugins && (s.Value == "1" || s.Value == "true"));
            if (!allowed)
            {
                // A withdrawn permission is not a failed load: nothing may keep running on the strength of an
                // earlier "yes", so what is loaded goes first and only then is the reason recorded.
                UnloadCore(p.Key);
                throw new NativeNotAllowed("Native Plugins sind auf dieser Instanz nicht erlaubt (Plugins → „Native Plugins erlauben“).");
            }

            var api = typeof(IPlugin).Assembly.GetName().Version!.Major;
            if (p.Api != api)
                throw new InvalidOperationException($"Das Plugin wurde für Plugin-API {p.Api} gebaut, dieses MatCMS bietet API {api}.");
            var info = new PluginInfo(p.Key, p.Name, p.Version, p.Description, p.Api);
            var src = StoragePaths.PluginSrc(_env, p.Key, p.Version);
            // Tables and install/update handlers run between "configured" and "published": a plugin whose tables cannot be
            // made, or whose installer throws, is never visible half-done.
            _native.Load(p, info, src, _registry, slice => ActivateAsync(p, info, slice).GetAwaiter().GetResult());
            _registry.ClearError(p.Id);
        }
        catch (Exception ex)
        {
            // Same rule as for scripts: a failed (re)load keeps the previous version running.
            _registry.SetError(p.Id, ex.Message);
            _registry.AddLog(p.Key, "Fehler beim Laden: " + ex.Message);
            // A refused permission is a state the operator chose, not a fault: no stack trace for it.
            if (ex is NativeNotAllowed) _log.LogInformation("Native plugin '{Name}' is not loaded: native plugins are not allowed", p.Name);
            else _log.LogWarning(ex, "Native plugin '{Name}' failed", p.Name);
        }
    }

    /// <summary>One finding of <see cref="Check"/>.</summary>
    /// <param name="Severity">error or warning.</param>
    /// <param name="File">The file it is in: the entry file or a file the plugin loads.</param>
    /// <param name="Line">1-based line in THAT file.</param>
    /// <param name="Column">1-based column.</param>
    /// <param name="Id">The compiler's code (CS0103 …).</param>
    /// <param name="Message">What is wrong.</param>
    public sealed record ScriptDiagnostic(string Severity, string File, int Line, int Column, string Id, string Message);

    /// <summary>
    /// Compiles a plugin's script exactly as the loader would — same references, same <c>#load</c> files, same include folders — but
    /// does <b>not run it</b>. For the editor's "Prüfen": errors are found before the plugin is saved or switched on, not after the
    /// first request. Line numbers are those of the file the author is looking at (the include prologue the loader adds is taken out again).
    /// </summary>
    public static IReadOnlyList<ScriptDiagnostic> Check(string? code, string? filesJson, string? mappingJson)
    {
        code ??= "";
        var files = PluginFileResolver.Parse(filesJson);
        var options = ScriptOptionsFor();
        if (files.Count > 0) options = options.WithSourceResolver(new PluginFileResolver(files));
        var includes = PluginMapping.IncludeFiles(files, PluginMapping.Parse(mappingJson));
        var full = PluginMapping.BuildEntry(code, includes);

        var codeLines = code.Split('\n');
        var fullLines = full.Split('\n');
        var added = fullLines.Length - codeLines.Length;
        var insertedAt = 0;
        if (added > 0) while (insertedAt < codeLines.Length && codeLines[insertedAt] == fullLines[insertedAt]) insertedAt++;

        var script = CSharpScript.Create(full, options, globalsType: typeof(PluginContext));
        var list = new List<ScriptDiagnostic>();
        foreach (var d in script.GetCompilation().GetDiagnostics())
        {
            if (d.Severity is not (Microsoft.CodeAnalysis.DiagnosticSeverity.Error or Microsoft.CodeAnalysis.DiagnosticSeverity.Warning)) continue;
            var span = d.Location.GetLineSpan();
            var path = span.Path;
            var isEntry = string.IsNullOrEmpty(path);
            var line = span.StartLinePosition.Line;
            if (isEntry && added > 0 && line >= insertedAt + added) line -= added;
            list.Add(new ScriptDiagnostic(d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error ? "error" : "warning",
                isEntry ? "plugin.csx" : path.TrimStart('/'), line + 1, span.StartLinePosition.Character + 1, d.Id, d.GetMessage()));
            if (list.Count >= 50) break;
        }
        return list;
    }

    /// <summary>Loads a plugin that is compiled into the CMS: no files, no load context — the same builder, the same activation (tables,
    /// lifecycle) and the same atomic publish as every other kind.</summary>
    private async Task LoadBuiltInAsync(Models.Plugin p)
    {
        var def = BuiltInPlugins.Find(p.Key);
        if (def is null)
        {
            _registry.SetError(p.Id, "Dieses eingebaute Plugin kennt diese Version von MatCMS nicht.");
            return;
        }
        var info = new PluginInfo(p.Key, def.Name, def.Version, def.Description, typeof(IPlugin).Assembly.GetName().Version!.Major);
        var slice = _registry.Begin(p.Key, p.Id);
        try
        {
            def.Create().Configure(new PluginBuilder(_registry, slice, info, builtIn: true));
            await ActivateAsync(p, info, slice);
            _registry.Publish(slice);
            _registry.ClearError(p.Id);
        }
        catch (Exception ex)
        {
            _registry.SetError(p.Id, ex.Message);
            _registry.AddLog(p.Key, "Fehler beim Laden: " + ex.Message);
            _log.LogWarning(ex, "Built-in plugin '{Name}' failed", p.Name);
        }
    }

    // Reference every loaded assembly so scripts can use the framework + app types.
    private static ScriptOptions ScriptOptionsFor()
    {
        var refs = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrWhiteSpace(a.Location))
            .ToList();
        return ScriptOptions.Default
            .WithReferences(refs)
            .WithImports("System", "System.Linq", "System.Collections.Generic", "System.Threading.Tasks",
                         "MatCMS.Services", "MatCMS.Data", "MatCMS.Models", "Microsoft.EntityFrameworkCore",
                         "MatCMS.Plugin");
    }

    private async Task LoadCoreAsync(Models.Plugin p, ScriptOptions options)
    {
        if (Models.PluginKind.Normalize(p.Kind) == Models.PluginKind.Native) { await LoadNativeAsync(p); return; }
        if (Models.PluginKind.Normalize(p.Kind) == Models.PluginKind.BuiltIn) { await LoadBuiltInAsync(p); return; }
        var info = new PluginInfo(p.Key, p.Name, p.Version, p.Description, 1);
        var slice = _registry.Begin(p.Key, p.Id);
        // Each plugin gets its own context carrying its Key (asset folder / URL prefix), its builder
        // and its admin-set configuration (read via Config("key")).
        var ctx = new PluginContext(_registry, slice, _services, info, ParseConfig(p.ConfigJson));
        try
        {
            // Die Dateien DIESES Plugins sind die einzige Quelle für #load. Je Plugin eigene
            // Optionen, damit kein Plugin die Dateien eines anderen sieht.
            var files = PluginFileResolver.Parse(p.FilesJson);
            // Sprachdateien (strings/de.json …) gelten vor dem Code: was das Skript selbst mit Plugin.Strings anmeldet, überschreibt sie.
            PluginStringFiles.FromFiles(slice, files, msg => _registry.AddLog(p.Key, "Sprachdatei: " + msg));
            var opts = files.Count == 0 ? options : options.WithSourceResolver(new PluginFileResolver(files));
            // Ordner mit der Rolle „Include“ werden vor der Einstiegsdatei geladen, ohne dass
            // jemand sie von Hand einbindet. Ohne solchen Ordner kommt der Code unverändert
            // zurück — ein Plugin ohne gesetzte Rolle läuft damit exakt wie vorher.
            var mapping = PluginMapping.Parse(p.MappingJson);
            var code = PluginMapping.BuildEntry(p.Code, PluginMapping.IncludeFiles(files, mapping));
            await CSharpScript.RunAsync(code, opts, globals: ctx, globalsType: typeof(PluginContext));
            await ActivateAsync(p, info, slice);
            // Published only now: a script that throws half-way registers NOTHING, instead of leaving
            // the menu entry it got to before the error.
            _registry.Publish(slice);
            _registry.ClearError(p.Id);
        }
        catch (Exception ex)
        {
            // A failed (re)load keeps what was running before — a typo saved into a live plugin must not
            // take its pages down. The error is shown on the plugin; on a first load nothing is running.
            _registry.SetError(p.Id, ex.Message);
            _registry.AddLog(p.Key, "Fehler beim Laden: " + ex.Message);
            _log.LogWarning(ex, "Plugin '{Name}' failed", p.Name);
        }
    }

    /// <summary>
    /// What happens between a plugin being configured and being published: its tables are made to match the declaration, and
    /// its install / update / load handlers run. Anything that throws here stops the load — the error is shown on the plugin and
    /// a previously running version keeps running — so the site never serves a plugin whose data is half set up.
    /// <para>The stored "data version" (the same field the older <c>Migrate</c> uses) is only touched for a plugin that
    /// declares lifecycle handlers; a plugin that does not keeps whatever its own <c>Migrate</c> calls wrote there.</para>
    /// </summary>
    private async Task ActivateAsync(Models.Plugin p, PluginInfo info, PluginRegistry.Slice slice)
    {
        if (slice.Tables.Count > 0) await _data.ReconcileAsync(_db, p.Key, slice.Tables);
        if (!slice.HasLifecycle) return;

        var ctx = PluginRequestContext.ForBackground(info, _services, _registry, slice);
        var row = await _db.Plugins.FirstOrDefaultAsync(x => x.Id == p.Id);
        var installed = row?.DataVersion ?? "";
        if (installed.Length == 0)
            foreach (var h in slice.InstallHandlers) await h(ctx, CancellationToken.None);
        else if (PluginContext.CompareVersions(installed, info.Version) < 0)
            foreach (var h in slice.UpdateHandlers) await h(ctx, installed, CancellationToken.None);
        foreach (var h in slice.LoadHandlers) await h(ctx, CancellationToken.None);

        if (row is not null && row.DataVersion != info.Version)
        {
            row.DataVersion = info.Version ?? "";
            await _db.SaveChangesAsync();
        }
    }

    /// <summary>Parses a plugin's ConfigJson object into a string→string map (best-effort).</summary>
    private static Dictionary<string, string> ParseConfig(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object)
                foreach (var prop in doc.RootElement.EnumerateObject())
                    d[prop.Name] = prop.Value.ValueKind == System.Text.Json.JsonValueKind.String
                        ? (prop.Value.GetString() ?? "")
                        : prop.Value.ToString();
            return d;
        }
        catch { return new(); }
    }
}
