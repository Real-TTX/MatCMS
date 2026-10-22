using System.Text.Json;
using System.Text.Json.Nodes;
using MatCMS.Content;
using MatCMS.Models;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Services.Assistant;

/// <summary>
/// The assistant beyond pages: the actions plugins describe (the blog's posts, the shop's products and categories — whatever a loaded plugin
/// offers), components, templates and script plugins. The same rules as the page tools: read freely, write through the code the admin uses,
/// journal before the first change so "Rückgängig" can take the whole run back, refuse a write outside a locked scope.
/// <para>Two walls specific to these: markup the assistant writes into a layout or a component is REFUSED when it carries anything that runs
/// (<see cref="AssistantMarkup"/>), and plugin CODE it writes never runs without the admin — a new plugin is created switched off, and a
/// running plugin whose code it changed is switched off until the admin has looked at it and switches it on again.</para>
/// </summary>
public sealed partial class AssistantTools
{
    /// <summary>A property, not a field: <see cref="Catalog"/> (in the other file of this class) is built from it, and the order in which two
    /// files' static fields are initialised is not defined.</summary>
    private static (string Name, string Args, string What, bool Writes)[] MoreCatalog =>
    [
        ("plugin_action", "{\"name\": \"post.create\", \"args\": {…}}", "Eine Aktion eines Plugins ausführen (Blogbeiträge, Shop-Produkte, Kategorien …) – die verfügbaren stehen unten unter „Plugin-Aktionen“ mit ihren Argumenten.", true),
        ("list_components", "{}", "Die eigenen Komponenten (Blocktypen dieser Website): Typ, Name, Felder.", false),
        ("read_component", "{\"component\": \"typ\"}", "Eine Komponente mit Feldern und HTML-Vorlage lesen.", false),
        ("create_component", "{\"name\", \"type\"?, \"description\"?, \"fields\": [{\"id\": \"titel\", \"label\": \"Titel\", \"type\": \"text|textarea|richtext|image|url\"}], \"html\": \"<section class=…>{{titel}}</section>\"}", "Neuen Blocktyp anlegen: HTML mit {{feldId}}-Platzhaltern (Werte werden maskiert, richtext roh). Kein <script>, keine on…-Attribute. Danach als Block mit type = Typ nutzbar.", true),
        ("update_component", "{\"component\": \"typ\", \"name\"?, \"description\"?, \"fields\"?: […], \"html\"?}", "Komponente ändern (fields ersetzt die Liste; eine umbenannte Feld-id lässt bestehende Blöcke den Wert verlieren).", true),
        ("list_templates", "{}", "Alle Templates (Designs): id, Name, aktiv.", false),
        ("read_template", "{\"template\"?: id}", "Die Dateien eines Templates (Standard: das aktive): body.html (Layout mit {{content}}), styles.css, login.html, article.html (Blogbeitrag), maintenance.html, Plugin-Teile (z. B. shop-card.html), Parameter. script.js nur lesbar.", false),
        ("update_template", "{\"template\"?: id, \"files\": {\"body.html\"?: \"…\", \"styles.css\"?: \"…\", \"login.html\"?, \"article.html\"?, \"maintenance.html\"?, \"shop-card.html\"?: \"…\"}}", "Template-Dateien ersetzen (leer = Standard des Systems). Layout braucht {{content}}. Kein <script>, keine on…-Attribute; script.js ist gesperrt.", true),
        ("create_template", "{\"name\", \"from\"?: id}", "Neues Template als Kopie (Standard: vom aktiven) – zum gefahrlosen Ausprobieren, aktivieren mit activate_template.", true),
        ("activate_template", "{\"template\": id}", "Ein Template zum aktiven Design der Website machen.", true),
        ("list_plugins", "{}", "Alle Plugins: Schlüssel, Name, Art (script|native|builtin), an/aus, Fehler, Aktionen.", false),
        ("read_plugin", "{\"plugin\": \"schlüssel\"}", "Code und Dateien eines Skript-Plugins lesen (C#, Plugin-API von MatCMS).", false),
        ("create_plugin", "{\"name\", \"description\"?, \"code\": \"Plugin.Block(…);\", \"files\"?: {\"lib/x.csx\": \"…\"}}", "Neues Skript-Plugin anlegen. Es wird übersetzt (Fehler kommen zurück) und AUSGESCHALTET gespeichert – einschalten tut der Nutzer.", true),
        ("update_plugin", "{\"plugin\": \"schlüssel\", \"code\"?, \"files\"?, \"name\"?, \"description\"?}", "Code eines Skript-Plugins ändern. Wird übersetzt; ein laufendes Plugin wird dabei ausgeschaltet, bis der Nutzer es prüft und wieder einschaltet.", true),
    ];

    private const int MaxComponentHtml = 50000, MaxPluginCode = 200000;

    private static readonly string[] FieldTypes = ["text", "textarea", "richtext", "image", "url"];

    // ============================================================================================== plugin actions

    private Plugins.PluginContributions Plugins => _services.GetRequiredService<Plugins.PluginContributions>();

    /// <summary>The plugin actions as prompt text — only the described ones that carry no customers' data.</summary>
    public string PluginActionsText()
    {
        var ops = Plugins.AssistantOperations;
        if (ops.Count == 0) return "";
        var sb = new System.Text.StringBuilder("\n## Plugin-Aktionen (mit plugin_action ausführen)\n");
        foreach (var o in ops.OrderBy(o => o.Owner).ThenBy(o => o.Name))
            sb.Append("- ").Append(o.Name).Append(" (").Append(o.Info.Name).Append(')').Append(o.Spec!.Writes ? " [ändert]" : "")
              .Append(" — ").Append(o.Spec.Description).Append(" Argumente: ").Append(o.Spec.Arguments).Append('\n');
        return sb.ToString();
    }

    private async Task<ToolOutcome> PluginActionAsync(JsonObject args, CancellationToken ct)
    {
        var name = Str(args["name"]).Trim();
        var op = Plugins.AssistantOperations.FirstOrDefault(o => o.Name == name)
            ?? throw new ToolError($"Unbekannte Plugin-Aktion „{name}“", $"Die Aktion \"{name}\" gibt es nicht (oder ihr Plugin ist aus). Verfügbar: {string.Join(", ", Plugins.AssistantOperations.Select(o => o.Name))}.");
        if (op.Spec!.Writes) RequireUnlocked($"die Plugin-Aktion „{name}“ ausführen");
        var input = args["args"] is JsonObject a ? a.DeepClone() : new JsonObject();
        var answer = await Plugins.RunOperationAsync(_services, name, input, ct);
        var o = answer as JsonObject ?? new JsonObject { ["status"] = "applied", ["message"] = answer?.ToString() ?? "" };
        var status = Str(o["status"]) is { Length: > 0 } s ? s : "applied";
        var message = Str(o["message"]);
        if (status == "failed") throw new ToolError($"{name}: fehlgeschlagen", message.Length > 0 ? message : "Die Aktion ist fehlgeschlagen.");
        var result = new JsonObject { ["status"] = status, ["message"] = message };
        if (o["data"] is { } data) result["data"] = data.DeepClone();
        var warnings = new List<string>();
        var changed = op.Spec.Writes && status == "applied";
        if (changed)
        {
            if (o["undo"] is JsonObject undo && Str(undo["name"]).Length > 0)
            {
                Journal("op", name + "#" + Guid.NewGuid().ToString("N")[..8], undo.ToJsonString());
                await _db.SaveChangesAsync(ct);
            }
            else warnings.Add($"Die Aktion {name} lässt sich nicht automatisch rückgängig machen.");
        }
        var summary = $"{op.Info.Name}: {(message.Length > 0 ? message : name)}";
        return Ok(summary.Length > 200 ? summary[..200] + "…" : summary, result, warnings, null, changed: changed);
    }

    // ============================================================================================== components

    private static JsonArray FieldsJson(string json) =>
        new(ComponentDefinition.ParseFields(json).Select(f => (JsonNode)new JsonObject { ["id"] = f.Id, ["label"] = f.Label, ["type"] = f.Type.ToString().ToLowerInvariant() }).ToArray());

    private async Task<ToolOutcome> ListComponentsAsync(CancellationToken ct)
    {
        var list = await _db.Components.AsNoTracking().OrderBy(c => c.Name).ToListAsync(ct);
        var arr = new JsonArray(list.Select(c => (JsonNode)new JsonObject
        {
            ["id"] = c.Id, ["type"] = c.Type, ["name"] = c.Name, ["description"] = c.Description, ["fields"] = FieldsJson(c.FieldsJson),
        }).ToArray());
        return Ok($"{list.Count} Komponenten gelesen", arr);
    }

    private async Task<Component> FindComponentAsync(JsonNode? raw, CancellationToken ct, bool tracked)
    {
        var key = Str(raw).Trim();
        var q = tracked ? _db.Components : _db.Components.AsNoTracking();
        return (int.TryParse(key, out var id) ? await q.FirstOrDefaultAsync(c => c.Id == id, ct) : await q.FirstOrDefaultAsync(c => c.Type == key, ct))
            ?? throw new ToolError("Komponente nicht gefunden", $"Keine Komponente \"{key}\" (list_components zeigt alle).");
    }

    private async Task<ToolOutcome> ReadComponentAsync(JsonObject args, CancellationToken ct)
    {
        var c = await FindComponentAsync(args["component"] ?? args["type"], ct, false);
        return Ok($"Komponente „{c.Name}“ gelesen", new JsonObject
        {
            ["id"] = c.Id, ["type"] = c.Type, ["name"] = c.Name, ["description"] = c.Description, ["fields"] = FieldsJson(c.FieldsJson), ["html"] = c.TemplateHtml,
        });
    }

    /// <summary>The field list as stored (<c>[{id,label,type}]</c>): ids are lower-case letters, digits and "_", types from the editor's list.</summary>
    private static string CleanFields(JsonNode? raw, List<string> warnings)
    {
        if (raw is not JsonArray arr) throw new ToolError("Felder fehlen", "fields muss eine Liste [{\"id\", \"label\", \"type\"}] sein.");
        var list = new JsonArray();
        var seen = new HashSet<string>();
        foreach (var f in arr.OfType<JsonObject>().Take(40))
        {
            var label = Str(f["label"]).Trim();
            var id = System.Text.RegularExpressions.Regex.Replace(Str(f["id"]).Trim().ToLowerInvariant(), "[^a-z0-9_]+", "_").Trim('_');
            if (id.Length == 0) id = System.Text.RegularExpressions.Regex.Replace(label.ToLowerInvariant(), "[^a-z0-9]+", "_").Trim('_');
            if (id.Length == 0 || !seen.Add(id)) { warnings.Add($"Feld \"{label}\": keine oder doppelte id – ausgelassen."); continue; }
            var type = Str(f["type"]).Trim().ToLowerInvariant();
            if (!FieldTypes.Contains(type)) { if (type.Length > 0) warnings.Add($"Feld {id}: Typ \"{type}\" gibt es nicht – text genommen."); type = "text"; }
            list.Add(new JsonObject { ["id"] = id, ["label"] = label.Length > 0 ? Cap(label, 80) : id, ["type"] = type });
        }
        return list.ToJsonString();
    }

    private static string CleanMarkup(string html, string what, int max)
    {
        if (html.Length > max) throw new ToolError($"{what} zu lang", $"{what} ist {html.Length} Zeichen lang, erlaubt sind {max}.");
        var problems = AssistantMarkup.Problems(html);
        if (problems.Count > 0)
            throw new ToolError($"{what}: nicht erlaubtes Markup", $"{what} wurde NICHT gespeichert: {string.Join("; ", problems)}. Ohne Skripte und Ereignis-Attribute neu schreiben (Gestaltung über CSS/Klassen).");
        return html;
    }

    private static string ComponentSnapshot(Component c) =>
        JsonSerializer.Serialize(new { c.Name, c.Description, c.Icon, c.FieldsJson, c.TemplateHtml });

    private async Task<ToolOutcome> CreateComponentAsync(JsonObject args, CancellationToken ct)
    {
        RequireUnlocked("neue Komponenten anlegen");
        var name = Str(args["name"]).Trim();
        if (name.Length == 0) throw new ToolError("Name fehlt", "Eine Komponente braucht einen Namen.");
        var warnings = new List<string>();
        var fields = CleanFields(args["fields"], warnings);
        var html = CleanMarkup(Str(args["html"]), "Die HTML-Vorlage", MaxComponentHtml);
        var baseType = System.Text.RegularExpressions.Regex.Replace((Str(args["type"]) is { Length: > 0 } t ? t : name).ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        if (baseType.Length == 0) baseType = "komponente";
        var taken = _blocks.AllTypes();
        var type = baseType;
        for (var n = 2; taken.Contains(type) || await _db.Components.AnyAsync(c => c.Type == type, ct); n++) type = baseType + "-" + n;
        if (type != baseType) warnings.Add($"Den Typ \"{baseType}\" gibt es schon – die Komponente heißt \"{type}\".");
        foreach (var f in ComponentDefinition.ParseFields(fields).Where(f => !html.Contains("{{" + f.Id + "}}")))
            warnings.Add($"Feld {f.Id} kommt in der HTML-Vorlage nicht als {{{{{f.Id}}}}} vor.");
        var c = new Component { Name = Cap(name, 120), Type = type, Description = Cap(Str(args["description"]).Trim(), 400), FieldsJson = fields, TemplateHtml = html };
        _db.Components.Add(c);
        await _db.SaveChangesAsync(ct);
        Journal("component", c.Id.ToString(), null);
        await _db.SaveChangesAsync(ct);
        await _blocks.ReloadAsync(ct);
        return Ok($"Komponente „{c.Name}“ angelegt (Typ {type})", new JsonObject { ["id"] = c.Id, ["type"] = type }, warnings, null, changed: true);
    }

    private async Task<ToolOutcome> UpdateComponentAsync(JsonObject args, CancellationToken ct)
    {
        RequireUnlocked("Komponenten ändern");
        var c = await FindComponentAsync(args["component"] ?? args["type"], ct, true);
        var warnings = new List<string>();
        var fields = args.ContainsKey("fields") ? CleanFields(args["fields"], warnings) : null;
        var html = args.ContainsKey("html") ? CleanMarkup(Str(args["html"]), "Die HTML-Vorlage", MaxComponentHtml) : null;
        if (!Journalled("component", c.Id.ToString())) { Journal("component", c.Id.ToString(), ComponentSnapshot(c)); }
        if (Str(args["name"]).Trim() is { Length: > 0 } name) c.Name = Cap(name, 120);
        if (args.ContainsKey("description")) c.Description = Cap(Str(args["description"]).Trim(), 400);
        if (fields is not null)
        {
            var gone = ComponentDefinition.ParseFields(c.FieldsJson).Select(f => f.Id).Except(ComponentDefinition.ParseFields(fields).Select(f => f.Id)).ToList();
            if (gone.Count > 0) warnings.Add($"Felder entfernt/umbenannt: {string.Join(", ", gone)} – vorhandene Blöcke verlieren diese Werte.");
            c.FieldsJson = fields;
        }
        if (html is not null) c.TemplateHtml = html;
        await _db.SaveChangesAsync(ct);
        await _blocks.ReloadAsync(ct);
        return Ok($"Komponente „{c.Name}“ geändert", new JsonObject { ["id"] = c.Id, ["type"] = c.Type }, warnings, null, changed: true);
    }

    // ============================================================================================== templates

    /// <summary>A template's files as the editor names them. Plugin parts are stored under "key.name" and shown as "key-name.html".</summary>
    private static readonly string[] CoreFiles = ["body.html", "styles.css", "login.html", "article.html", "maintenance.html"];

    private async Task<Template> FindTemplateAsync(JsonNode? raw, CancellationToken ct, bool tracked)
    {
        if (raw is null || Str(raw).Trim().Length == 0)
            return await ActiveTemplateAsync(ct, tracked) ?? throw new ToolError("Kein aktives Template", "Es ist kein Template aktiv.");
        var id = Int(raw) ?? throw new ToolError("Template?", "template muss die id aus list_templates sein.");
        var q = tracked ? _db.Templates : _db.Templates.AsNoTracking();
        return await q.FirstOrDefaultAsync(t => t.Id == id, ct) ?? throw new ToolError("Template nicht gefunden", $"Kein Template mit id {id}.");
    }

    private static JsonObject Parts(Template t) => JsonNode.Parse(string.IsNullOrWhiteSpace(t.PartsJson) ? "{}" : t.PartsJson) as JsonObject ?? new JsonObject();

    private async Task<ToolOutcome> ListTemplatesAsync(CancellationToken ct)
    {
        var list = await _db.Templates.AsNoTracking().OrderByDescending(t => t.IsActive).ThenBy(t => t.Name).Select(t => new { t.Id, t.Name, t.IsActive }).ToListAsync(ct);
        return Ok($"{list.Count} Templates gelesen", new JsonArray(list.Select(t => (JsonNode)new JsonObject { ["id"] = t.Id, ["name"] = t.Name, ["active"] = t.IsActive }).ToArray()));
    }

    private async Task<ToolOutcome> ReadTemplateAsync(JsonObject args, CancellationToken ct)
    {
        var t = await FindTemplateAsync(args["template"], ct, false);
        var parts = Parts(t);
        var files = new JsonObject
        {
            ["body.html"] = t.LayoutHtml, ["styles.css"] = t.CustomCss, ["script.js (nur lesen)"] = t.CustomJs, ["login.html"] = t.LoginHtml,
            ["article.html"] = Str(parts[TemplateSchema.PartPost]), ["maintenance.html"] = Str(parts[TemplateSchema.PartMaintenance]),
        };
        // The plugin parts the loaded plugins offer, with what this template holds for them (empty = the plugin's own default).
        foreach (var part in _services.GetRequiredService<PluginRegistry>().TemplateParts)
            files[part.Owner + "-" + part.Spec.Name + ".html"] = Str(parts[part.Owner + "." + part.Spec.Name]);
        return Ok($"Template „{t.Name}“ gelesen", new JsonObject
        {
            ["id"] = t.Id, ["name"] = t.Name, ["active"] = t.IsActive, ["files"] = files,
            ["hint"] = "Leere Datei = Standard des Systems bzw. des Plugins. body.html braucht {{content}}; weitere Platzhalter: {{logo}}, {{site_name}}, {{menu:header}}, {{#menu:header}}…{{/menu:header}}, {{toolbar}}, {{footer}}, {{year}}.",
            ["parameters"] = JsonNode.Parse(string.IsNullOrWhiteSpace(t.ParametersJson) ? "[]" : t.ParametersJson),
        });
    }

    private static string TemplateFilesSnapshot(Template t) => JsonSerializer.Serialize(new { t.LayoutHtml, t.CustomCss, t.LoginHtml, t.PartsJson });

    private async Task<ToolOutcome> UpdateTemplateAsync(JsonObject args, CancellationToken ct)
    {
        RequireUnlocked("das Template ändern");
        var t = await FindTemplateAsync(args["template"], ct, true);
        if (args["files"] is not JsonObject files || files.Count == 0) throw new ToolError("Keine Dateien", "files fehlt: {\"body.html\": \"…\", \"styles.css\": \"…\"}.");
        var pluginParts = _services.GetRequiredService<PluginRegistry>().TemplateParts.ToDictionary(p => p.Owner + "-" + p.Spec.Name + ".html", p => p.Owner + "." + p.Spec.Name, StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();
        var changed = new List<string>();
        var parts = Parts(t);
        var layout = t.LayoutHtml; var css = t.CustomCss; var login = t.LoginHtml;
        foreach (var (file, node) in files)
        {
            var text = Str(node);
            switch (file.ToLowerInvariant())
            {
                case "body.html":
                    layout = CleanMarkup(TemplateFonts.Code(text), "body.html", TemplateFonts.MaxLayoutHtml);
                    if (layout.Length > 0 && !layout.Contains("{{content}}")) warnings.Add("body.html enthält kein {{content}} – ohne wird das Standard-Layout benutzt.");
                    break;
                case "styles.css":
                    css = TemplateFonts.Code(text).Replace("</", "<\\/");
                    if (css.Length > TemplateFonts.MaxInlineCode) throw new ToolError("CSS zu lang", $"styles.css wäre {css.Length} Zeichen lang, erlaubt sind {TemplateFonts.MaxInlineCode}.");
                    break;
                case "login.html": login = CleanMarkup(TemplateFonts.Code(text), "login.html", TemplateFonts.MaxLayoutHtml); break;
                case "article.html": SetPart(parts, TemplateSchema.PartPost, CleanMarkup(TemplateFonts.Code(text), "article.html", TemplateFonts.MaxLayoutHtml)); break;
                case "maintenance.html": SetPart(parts, TemplateSchema.PartMaintenance, CleanMarkup(TemplateFonts.Code(text), "maintenance.html", TemplateFonts.MaxLayoutHtml)); break;
                case "script.js":
                    warnings.Add("script.js ändert die KI nicht (Skripte laufen bei jedem Besucher) – das macht der Nutzer im Template-Editor.");
                    continue;
                default:
                    if (pluginParts.TryGetValue(file, out var partKey)) { SetPart(parts, partKey, CleanMarkup(TemplateFonts.Code(text), file, TemplateFonts.MaxLayoutHtml)); break; }
                    warnings.Add($"Die Datei \"{file}\" gibt es nicht – ignoriert (read_template zeigt alle).");
                    continue;
            }
            changed.Add(file);
        }
        if (changed.Count == 0) return Ok("Template: nichts geändert", new JsonObject { ["changed"] = 0 }, warnings);
        if (!Journalled("templatefiles", t.Id.ToString())) Journal("templatefiles", t.Id.ToString(), TemplateFilesSnapshot(t));
        t.LayoutHtml = layout; t.CustomCss = css; t.LoginHtml = login; t.PartsJson = parts.ToJsonString();
        await _db.SaveChangesAsync(ct);
        return Ok($"Template „{t.Name}“: {string.Join(", ", changed)} geändert", new JsonObject { ["changed"] = changed.Count }, warnings, null, changed: true);
    }

    private static void SetPart(JsonObject parts, string key, string html)
    {
        if (html.Trim().Length == 0) parts.Remove(key);
        else parts[key] = html;
    }

    private async Task<ToolOutcome> CreateTemplateAsync(JsonObject args, CancellationToken ct)
    {
        RequireUnlocked("neue Templates anlegen");
        var name = Str(args["name"]).Trim();
        if (name.Length == 0) throw new ToolError("Name fehlt", "Ein Template braucht einen Namen.");
        var from = await FindTemplateAsync(args["from"], ct, false);
        var copy = new Template();
        foreach (var prop in typeof(Template).GetProperties().Where(p => p.CanWrite && p.Name is not (nameof(Template.Id) or nameof(Template.IsActive) or nameof(Template.Name))))
            prop.SetValue(copy, prop.GetValue(from));
        copy.Name = Cap(name, 120);
        copy.IsActive = false;
        _db.Templates.Add(copy);
        await _db.SaveChangesAsync(ct);
        Journal("template-new", copy.Id.ToString(), null);
        await _db.SaveChangesAsync(ct);
        return Ok($"Template „{copy.Name}“ als Kopie von „{from.Name}“ angelegt", new JsonObject { ["id"] = copy.Id }, null, null, changed: true);
    }

    private async Task<ToolOutcome> ActivateTemplateAsync(JsonObject args, CancellationToken ct)
    {
        RequireUnlocked("das aktive Design wechseln");
        var t = await FindTemplateAsync(args["template"] ?? throw new ToolError("Template?", "template (id) fehlt."), ct, true);
        if (t.IsActive) return Ok($"„{t.Name}“ ist schon aktiv", new JsonObject { ["id"] = t.Id });
        var current = await _db.Templates.Where(x => x.IsActive).ToListAsync(ct);
        if (!Journalled("template-active", "site")) Journal("template-active", "site", JsonSerializer.Serialize(current.Select(x => x.Id).FirstOrDefault()));
        foreach (var c in current) c.IsActive = false;
        t.IsActive = true;
        await _db.SaveChangesAsync(ct);
        return Ok($"Template „{t.Name}“ ist jetzt aktiv", new JsonObject { ["id"] = t.Id }, null, null, changed: true);
    }

    // ============================================================================================== plugins

    private async Task<ToolOutcome> ListPluginsAsync(CancellationToken ct)
    {
        var registry = _services.GetRequiredService<PluginRegistry>();
        var errors = registry.Errors;
        var list = await _db.Plugins.AsNoTracking().OrderBy(p => p.Name).ToListAsync(ct);
        var arr = new JsonArray(list.Select(p =>
        {
            var o = new JsonObject
            {
                ["key"] = p.Key, ["name"] = p.Name, ["kind"] = p.Kind, ["enabled"] = p.Enabled, ["version"] = p.Version, ["description"] = Cap(p.Description, 300),
                ["actions"] = string.Join(", ", registry.Operations.Where(op => op.Owner == p.Key && op.Spec is { Internal: false }).Select(op => op.Name)),
            };
            if (errors.TryGetValue(p.Id, out var e)) o["error"] = Cap(e, 500);
            return (JsonNode)o;
        }).ToArray());
        return Ok($"{list.Count} Plugins gelesen", arr);
    }

    private async Task<Models.Plugin> FindPluginAsync(JsonNode? raw, CancellationToken ct, bool tracked)
    {
        var key = Str(raw).Trim();
        var q = tracked ? _db.Plugins : _db.Plugins.AsNoTracking();
        return (int.TryParse(key, out var id) ? await q.FirstOrDefaultAsync(p => p.Id == id, ct) : await q.FirstOrDefaultAsync(p => p.Key == key, ct))
            ?? throw new ToolError("Plugin nicht gefunden", $"Kein Plugin \"{key}\" (list_plugins zeigt alle).");
    }

    private async Task<ToolOutcome> ReadPluginAsync(JsonObject args, CancellationToken ct)
    {
        var p = await FindPluginAsync(args["plugin"] ?? args["key"], ct, false);
        var o = new JsonObject { ["key"] = p.Key, ["name"] = p.Name, ["kind"] = p.Kind, ["enabled"] = p.Enabled, ["description"] = p.Description, ["version"] = p.Version };
        if (p.Kind == PluginKind.Script)
        {
            o["code"] = p.Code;
            o["files"] = JsonNode.Parse(string.IsNullOrWhiteSpace(p.FilesJson) ? "{}" : p.FilesJson);
            o["hint"] = "Skript-Plugin in C#: global \"Plugin\" ist der IPluginBuilder (Plugin.Block, Plugin.Route, Plugin.Admin.Page, Plugin.Settings, Plugin.Data.Table, Plugin.Operation …).";
        }
        else o["hint"] = p.Kind == PluginKind.BuiltIn ? "Eingebautes Plugin – Code nicht änderbar; seine Aktionen über plugin_action." : "Natives Plugin (übersetzte DLL) – Code nicht änderbar.";
        if (_services.GetRequiredService<PluginRegistry>().Errors.TryGetValue(p.Id, out var e)) o["error"] = e;
        return Ok($"Plugin „{p.Name}“ gelesen", o);
    }

    /// <summary>The script's further files as stored (path → text), from the model's object.</summary>
    private static string FilesOf(JsonNode? raw, string current)
    {
        if (raw is null) return current;
        if (raw is not JsonObject files) throw new ToolError("files?", "files ist ein Objekt {\"pfad/datei.csx\": \"Inhalt\"}.");
        var o = new JsonObject();
        foreach (var (path, content) in files.Take(40))
        {
            var p = path.Trim().Replace('\\', '/');
            if (p.Length == 0 || p.Contains("..") || p.StartsWith('/') || p.Length > 120) throw new ToolError("Dateipfad ungültig", $"Der Pfad \"{path}\" ist nicht erlaubt.");
            o[p] = Str(content);
        }
        return o.ToJsonString();
    }

    private static string Compiled(string code, string filesJson, string mappingJson)
    {
        var errors = PluginRunner.Check(code, filesJson, mappingJson).Where(d => d.Severity == "error").Take(12).ToList();
        if (errors.Count > 0)
            throw new ToolError("Plugin-Code übersetzt nicht", "NICHT gespeichert, der Code hat Fehler:\n" + string.Join("\n", errors.Select(d => $"{d.File}:{d.Line}:{d.Column} {d.Id} {d.Message}")));
        return code;
    }

    private static string PluginSnapshot(Models.Plugin p) =>
        JsonSerializer.Serialize(new { p.Name, p.Description, p.Code, p.FilesJson, p.Enabled });

    private async Task<ToolOutcome> CreatePluginAsync(JsonObject args, CancellationToken ct)
    {
        RequireUnlocked("Plugins anlegen");
        var name = Str(args["name"]).Trim();
        if (name.Length == 0) throw new ToolError("Name fehlt", "Ein Plugin braucht einen Namen.");
        var code = Str(args["code"]);
        if (code.Length > MaxPluginCode) throw new ToolError("Code zu lang", $"Höchstens {MaxPluginCode} Zeichen.");
        var mapping = JsonSerializer.Serialize(new Dictionary<string, string> { [PluginMapping.LegacyAssetFolder] = PluginMapping.RoleAssets });
        var files = FilesOf(args["files"], "{}");
        Compiled(code, files, mapping);
        var baseKey = Pages.Admin.Pages.IndexModel.Slugify(name) is { Length: > 0 } k ? k : "plugin";
        var key = baseKey;
        for (var n = 2; await _db.Plugins.AnyAsync(p => p.Key == key, ct) || MatCMS.Services.Plugins.BuiltInPlugins.Find(key) is not null; n++) key = baseKey + "-" + n;
        var plugin = new Models.Plugin { Name = Cap(name, 120), Key = key, Description = Cap(Str(args["description"]).Trim(), 1000), Code = code, FilesJson = files, MappingJson = mapping, Enabled = false, Version = "1.0" };
        _db.Plugins.Add(plugin);
        await _db.SaveChangesAsync(ct);
        Journal("plugin", plugin.Id.ToString(), null);
        await _db.SaveChangesAsync(ct);
        return Ok($"Plugin „{plugin.Name}“ angelegt (ausgeschaltet – unter Plugins prüfen und einschalten)",
            new JsonObject { ["key"] = key, ["enabled"] = false, ["admin"] = $"/admin/plugins/edit/{plugin.Id}" },
            ["Das Plugin ist AUSGESCHALTET. Sag dem Nutzer, dass er es unter Plugins prüfen und einschalten muss."], null, changed: true);
    }

    private async Task<ToolOutcome> UpdatePluginAsync(JsonObject args, CancellationToken ct)
    {
        RequireUnlocked("Plugins ändern");
        var p = await FindPluginAsync(args["plugin"] ?? args["key"], ct, true);
        if (p.Kind != PluginKind.Script) throw new ToolError("Nicht änderbar", $"„{p.Name}“ ist kein Skript-Plugin ({p.Kind}) – sein Code ist nicht änderbar.");
        var code = args.ContainsKey("code") ? Str(args["code"]) : p.Code;
        if (code.Length > MaxPluginCode) throw new ToolError("Code zu lang", $"Höchstens {MaxPluginCode} Zeichen.");
        var files = FilesOf(args["files"], p.FilesJson);
        var codeChanged = code != p.Code || files != p.FilesJson;
        if (codeChanged) Compiled(code, files, p.MappingJson);
        if (!Journalled("plugin", p.Id.ToString())) Journal("plugin", p.Id.ToString(), PluginSnapshot(p));
        var warnings = new List<string>();
        var wasOn = p.Enabled;
        p.Code = code; p.FilesJson = files;
        if (Str(args["name"]).Trim() is { Length: > 0 } name) p.Name = Cap(name, 120);
        if (args.ContainsKey("description")) p.Description = Cap(Str(args["description"]).Trim(), 1000);
        // Code the AI wrote runs only after the admin has looked at it: a running plugin goes off until it is switched on again.
        if (codeChanged && wasOn)
        {
            p.Enabled = false;
            warnings.Add("Das Plugin wurde AUSGESCHALTET, weil sein Code geändert wurde. Sag dem Nutzer, dass er es unter Plugins prüfen und wieder einschalten muss.");
        }
        await _db.SaveChangesAsync(ct);
        if (codeChanged && wasOn) await _services.GetRequiredService<PluginRunner>().ReloadAsync(p.Id);
        return Ok($"Plugin „{p.Name}“ geändert" + (codeChanged && wasOn ? " (ausgeschaltet bis zur Prüfung)" : ""),
            new JsonObject { ["key"] = p.Key, ["enabled"] = p.Enabled, ["admin"] = $"/admin/plugins/edit/{p.Id}" }, warnings, null, changed: true);
    }
}
