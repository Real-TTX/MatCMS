using System.Text;
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
    /// <summary>Request path (e.g. "/leser") — set for block renders and public endpoints. Handy for
    /// building a same-page return URL after a public POST.</summary>
    public string Path { get; init; } = "";
    public IReadOnlyDictionary<string, string> Query { get; init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, string> Form { get; init; } = new Dictionary<string, string>();
    /// <summary>Block field data (JSON) — only for block render callbacks.</summary>
    public string Data { get; init; } = "{}";

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
    /// plus <paramref name="hidden"/> fields. <paramref name="confirm"/> adds a JS confirmation prompt.</summary>
    public string ActionButton(string label, IDictionary<string, string> hidden, string? cssClass = null, string? confirm = null)
    {
        var onclick = "";
        if (!string.IsNullOrWhiteSpace(confirm))
        {
            var js = confirm.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\r", " ").Replace("\n", " ");
            onclick = " onclick=\"return confirm('" + Enc(js) + "')\"";
        }
        var btn = "<button type=\"submit\" class=\"btn btn-sm " + Enc(cssClass) + "\"" + onclick + ">" + Enc(label) + "</button>";
        return Form(btn, hidden, "inline-form");
    }
}

/// <summary>What plugins registered on their last run (shared across requests, singleton).</summary>
public class PluginRegistry
{
    public sealed record AdminMenuEntry(string Label, string Url, string Icon);
    public sealed record PluginBlock(string Type, string Name, string Description, Func<PluginRequest, string> Render)
    {
        /// <summary>Editable fields shown in the block editor (empty = none). Declared via AddBlock's fieldsJson.</summary>
        public List<MatCMS.Content.BlockField> Fields { get; init; } = new();
    }

    private readonly object _lock = new();
    public List<AdminMenuEntry> AdminMenu { get; } = new();
    public List<PluginBlock> Blocks { get; } = new();
    public Dictionary<string, Func<PluginRequest, string>> Pages { get; } = new();
    /// <summary>Public (anonymous) endpoints served at <c>/plugin/{key}</c> — for visitor-facing
    /// actions such as submitting a review or a comment. Cleared/repopulated on each plugin run.
    /// Accessed via <see cref="AddPublicPageEntry"/> / <see cref="TryGetPublicPage"/> under the lock,
    /// because it is read on the anonymous request hot path while a re-run may be rewriting it.</summary>
    private readonly Dictionary<string, Func<PluginRequest, string>> _publicPages = new();
    public Dictionary<int, string> Errors { get; } = new();

    /// <summary>Registers/replaces a public endpoint handler (thread-safe).</summary>
    public void AddPublicPageEntry(string key, Func<PluginRequest, string> handler)
    {
        lock (_lock) _publicPages[key] = handler;
    }

    /// <summary>Looks up a public endpoint handler (thread-safe against a concurrent plugin re-run).</summary>
    public bool TryGetPublicPage(string key, out Func<PluginRequest, string>? handler)
    {
        lock (_lock) return _publicPages.TryGetValue(key, out handler);
    }

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
        lock (_lock) { AdminMenu.Clear(); Blocks.Clear(); Pages.Clear(); _publicPages.Clear(); Errors.Clear(); _headHtml.Clear(); _bodyHtml.Clear(); }
    }
}

/// <summary>The API surface handed to plugin scripts as globals.</summary>
public class PluginContext
{
    private readonly PluginRegistry _registry;
    public IServiceProvider Services { get; }

    /// <summary>This plugin's stable slug — also the name of its asset folder / URL prefix.</summary>
    public string Key { get; }

    private readonly IReadOnlyDictionary<string, string> _config;

    public PluginContext(PluginRegistry registry, IServiceProvider services, string key = "",
        IReadOnlyDictionary<string, string>? config = null)
    {
        _registry = registry;
        Services = services;
        Key = key ?? "";
        _config = config ?? new Dictionary<string, string>();
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

    /// <summary>Register a PUBLIC (anonymous) endpoint served at <c>/plugin/{endpointKey}</c>. GET returns
    /// the callback's HTML; POST runs the callback (a mutating action) and then redirects — to the form's
    /// <c>__return</c> field when it is a safe local path, otherwise to "/". Always include a <c>__return</c>
    /// hidden field in your form. Use for visitor submissions (reviews, comments). The callback gets
    /// Form/Query/Method/Path on the request. The endpoint is rate-limited per client IP.</summary>
    public void AddPublicPage(string endpointKey, Func<PluginRequest, string> handler)
    {
        if (!string.IsNullOrWhiteSpace(endpointKey) && handler is not null) _registry.AddPublicPageEntry(endpointKey, handler);
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
            _registry.Blocks.Add(new(type, name ?? type, description ?? "", render) { Fields = ParseBlockFields(fieldsJson) });
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
                // Each plugin gets its own context carrying its Key (asset folder / URL prefix) and its
                // admin-set configuration (read via Config("key")).
                var ctx = new PluginContext(_registry, _services, p.Key, ParseConfig(p.ConfigJson));
                try
                {
                    // Die Dateien DIESES Plugins sind die einzige Quelle für #load. Je Plugin eigene
                    // Optionen, damit kein Plugin die Dateien eines anderen sieht.
                    var files = PluginFileResolver.Parse(p.FilesJson);
                    var opts = files.Count == 0 ? options : options.WithSourceResolver(new PluginFileResolver(files));
                    await CSharpScript.RunAsync(p.Code ?? "", opts, globals: ctx, globalsType: typeof(PluginContext));
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
