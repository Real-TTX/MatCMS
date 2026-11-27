using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MatCMS.Data;
using MatCMS.Models;
using MatCMS.Shared;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Services;

/// <summary>
/// Applies the content operations the cloud offers on the heartbeat (Stage 2, Increment 1: creating pages) —
/// the instance side of an AI change made through the cloud's MCP server. The cloud only ever ASKS; this is
/// where the site actually does the work, and it does it through its OWN validated writer
/// (<see cref="BlockGenerator.ValidateBlocks(string?)"/>, the exact validator the in-app AI page/site
/// generators use), never applying anything the cloud sent as code. Add-only: an existing slug is skipped,
/// never overwritten. Each op yields a <see cref="ContentOpReport"/> the caller sends back on the next beat.
/// </summary>
public class ContentOpApplier
{
    private readonly AppDbContext _db;
    private readonly BlockGenerator _blockGen;
    private readonly CleanupService _cleanup;
    private readonly IServiceProvider _services;

    public ContentOpApplier(AppDbContext db, BlockGenerator blockGen, CleanupService cleanup, IServiceProvider services)
    {
        _db = db;
        _blockGen = blockGen;
        _cleanup = cleanup;
        _services = services;
    }

    public async Task<ContentOpReport> ApplyAsync(PendingContentOp op, CancellationToken ct = default)
    {
        try
        {
            return op.Kind switch
            {
                "page.create" => await CreatePageAsync(op, ct),
                "form.create" => await CreateFormAsync(op, ct),
                "setting.set" => await SetSettingAsync(op, ct),
                "site.address" => await SetAddressAsync(op, ct),
                "cleanup.analyze" => await AnalyzeCleanupAsync(op, ct),
                "cleanup.apply" => await ApplyCleanupAsync(op, ct),
                "pages.list" => await ListPagesAsync(op, ct),
                "page.read" => await ReadPageAsync(op, ct),
                "page.updateBlocks" => await UpdatePageBlocksAsync(op, ct),
                _ => await RunPluginOperationAsync(op, ct),
            };
        }
        catch (Exception ex)
        {
            // A single bad op must never take the heartbeat down — report it failed and move on.
            return Report(op, "failed", ex.Message);
        }
    }

    /// <summary>An operation no core handler knows may be offered by a loaded plugin (<c>Plugin.Operation(name, …)</c>). The plugin
    /// gets the payload as JSON and answers with a message; it validates what it receives like any other writer.</summary>
    private async Task<ContentOpReport> RunPluginOperationAsync(PendingContentOp op, CancellationToken ct)
    {
        var plugins = _services.GetRequiredService<Plugins.PluginContributions>();
        if (!plugins.HasOperation(op.Kind)) return Report(op, "failed", $"Unbekannte Aktion: {op.Kind}");
        var args = string.IsNullOrWhiteSpace(op.PayloadJson) ? null : JsonNode.Parse(op.PayloadJson);
        var answer = await plugins.RunOperationAsync(_services, op.Kind, args, ct);
        // The answer is a JSON object { "status": "applied|skipped-exists|failed", "message": "…" } (a bare string is a message).
        // A READ adds "data", which rides home as the report's result (the same slot as a page read).
        if (answer is JsonObject o)
        {
            var status = o["status"]?.GetValue<string>() is { Length: > 0 } s ? s : "applied";
            var message = o["message"]?.GetValue<string>() ?? "";
            return o["data"] is { } data ? new ContentOpReport { OpId = op.OpId, Outcome = status, Detail = message, ResultJson = data.ToJsonString() } : Report(op, status, message);
        }
        return Report(op, "applied", answer?.ToString() ?? "");
    }

    private async Task<ContentOpReport> CreatePageAsync(PendingContentOp op, CancellationToken ct)
    {
        var payload = JsonNode.Parse(op.PayloadJson) as JsonObject
            ?? throw new InvalidOperationException("PayloadJson ist kein Objekt.");

        var title = (payload["title"]?.GetValue<string>() ?? "").Trim();
        var slug = Slugify(payload["slug"]?.GetValue<string>() ?? title);
        var showInNav = payload["showInNav"]?.GetValue<bool>() ?? true;
        var navLabel = payload["navLabel"]?.GetValue<string>();

        if (string.IsNullOrWhiteSpace(title))
            return Report(op, "failed", "Kein Titel angegeben.");
        if (string.IsNullOrWhiteSpace(slug))
            return Report(op, "failed", "Kein gültiger Slug ableitbar.");
        if (Pages.Admin.Pages.IndexModel.IsReserved(slug))
            return Report(op, "failed", $"Slug „{slug}“ ist reserviert.");

        // Add-only: (Slug, Locale) is unique, so an existing page is left exactly as it is.
        if (await _db.Pages.AnyAsync(p => p.Slug == slug && p.Locale == Localizer.DefaultCulture, ct))
            return Report(op, "skipped-exists", $"Seite „{slug}“ existiert bereits.");

        // The blocks travel as the "blocks" array; re-validate with the shared validator — unknown block
        // types and unknown fields are dropped here, exactly as for the in-app generators.
        var blocksJson = payload["blocks"]?.ToJsonString() ?? "[]";
        var validated = _blockGen.ValidateBlocks(blocksJson);

        var page = new Page
        {
            Title = title,
            Slug = slug,
            Locale = Localizer.DefaultCulture,
            TranslationGroup = Guid.NewGuid().ToString("N"),
            IsPublished = true,
            ShowInNav = showInNav,
            NavLabel = string.IsNullOrWhiteSpace(navLabel) ? null : navLabel.Trim(),
            NavOrder = showInNav ? await NextNavOrderAsync(ct) : 0,
        };
        var sort = 0;
        foreach (var (type, _, data) in validated)
            page.Blocks.Add(new ContentBlock { BlockType = type, DataJson = data.ToJsonString(), SortOrder = sort++ });

        _db.Pages.Add(page);
        // "In die Navigation" means the site's header menu: the public menus are built from MenuItems only, so ShowInNav
        // alone (all this did before) showed the page nowhere. Same entry as the assistant's addToMenu; never twice.
        var menuNote = "";
        if (showInNav)
        {
            var url = Assistant.AssistantTools.PublicUrl(slug, Localizer.DefaultCulture);
            if (!await _db.MenuItems.AnyAsync(m => m.Menu == "header" && m.Locale == Localizer.DefaultCulture && m.Url == url, ct))
            {
                var max = await _db.MenuItems.Where(m => m.Menu == "header" && m.Locale == Localizer.DefaultCulture && m.ParentId == null)
                    .MaxAsync(m => (int?)m.SortOrder, ct) ?? 0;
                _db.MenuItems.Add(new MenuItem { Menu = "header", Locale = Localizer.DefaultCulture, Label = page.NavLabel ?? page.Title, Url = url, SortOrder = max + 1 });
                menuNote = ", ins Menü aufgenommen";
            }
        }
        await _db.SaveChangesAsync(ct);

        return Report(op, "applied", $"Seite „{slug}“ mit {validated.Count} Block/Blöcken angelegt{menuNote}.");
    }

    private async Task<ContentOpReport> CreateFormAsync(PendingContentOp op, CancellationToken ct)
    {
        var payload = JsonNode.Parse(op.PayloadJson) as JsonObject
            ?? throw new InvalidOperationException("PayloadJson ist kein Objekt.");

        var name = (payload["name"]?.GetValue<string>() ?? "").Trim();
        var slug = Slugify(payload["slug"]?.GetValue<string>() ?? name);
        if (string.IsNullOrWhiteSpace(name)) return Report(op, "failed", "Kein Name angegeben.");
        if (string.IsNullOrWhiteSpace(slug)) return Report(op, "failed", "Kein gültiger Slug ableitbar.");

        // Add-only: Slug is the reference key from a form block, so an existing form is left as it is.
        if (await _db.Forms.AnyAsync(f => f.Slug == slug, ct))
            return Report(op, "skipped-exists", $"Formular „{slug}“ existiert bereits.");

        // The field definition travels as the "fields" array; it is a structured field list (not raw HTML),
        // rendered by the form builder — stored as-is, defaulting to empty when absent.
        var fields = payload["fields"];
        var definitionJson = fields is JsonArray arr ? NormalizeFormFields(arr) : "[]";
        var successMessage = payload["successMessage"]?.GetValue<string>();
        var submitLabel = payload["submitLabel"]?.GetValue<string>();

        _db.Forms.Add(new Form
        {
            Name = name,
            Slug = slug,
            DefinitionJson = definitionJson,
            SuccessMessage = string.IsNullOrWhiteSpace(successMessage) ? null : successMessage,
            SubmitLabel = string.IsNullOrWhiteSpace(submitLabel) ? null : submitLabel,
        });
        await _db.SaveChangesAsync(ct);
        return Report(op, "applied", $"Formular „{slug}“ angelegt.");
    }

    /// <summary>
    /// A model writes form fields in its own words: "message", "multiline" or "long_text" for what the form builder calls a text area, and often no id.
    /// Unknown types used to fall through to a one-line text box (a "Nachricht" field the size of a name field), and a field without an id cannot be
    /// submitted. So: the known synonyms become the builder's types, a field named like a message becomes a text area, a missing id is made from the
    /// label, and ids are unique.
    /// </summary>
    internal static string NormalizeFormFields(JsonArray fields)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in fields.OfType<JsonObject>())
        {
            var type = (f["type"]?.GetValue<string>() ?? "text").Trim().ToLowerInvariant().Replace("-", "").Replace("_", "");
            var label = f["label"]?.GetValue<string>() ?? "";
            type = type switch
            {
                "textarea" or "message" or "multiline" or "longtext" or "comment" or "comments" => "textarea",
                "heading" or "headline" => "title",
                "paragraph" or "info" or "hint" => "description",
                "tel" or "telephone" or "phone" => "phone",
                "mail" or "email" => "email",
                "dropdown" or "select" => "select",
                "" => "text",
                _ => type
            };
            if (type == "text" && System.Text.RegularExpressions.Regex.IsMatch(label, @"^\s*(nachricht|ihre nachricht|message|your message|anliegen|kommentar|bemerkung|mitteilung|beschreibung)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                type = "textarea";
            f["type"] = type;
            var id = (f["id"]?.GetValue<string>() ?? "").Trim();
            if (id.Length == 0) id = Slugify(label).Replace("-", "_");
            if (id.Length == 0) id = "feld";
            var unique = id; for (var n = 2; !used.Add(unique); n++) unique = id + "_" + n;
            f["id"] = unique;
        }
        return fields.ToJsonString();
    }

    /// <summary>The cloud's hosting push of the site's public address (protocol 19): the ONE way the content channel sets
    /// <c>site.canonicalUrl</c>, which <c>setting.set</c> refuses — it is the origin of every member mail link. Only an
    /// absolute https address (or empty = take it back) is accepted, and nothing but these two keys is written.</summary>
    private async Task<ContentOpReport> SetAddressAsync(PendingContentOp op, CancellationToken ct)
    {
        var payload = JsonNode.Parse(op.PayloadJson) as JsonObject
            ?? throw new InvalidOperationException("PayloadJson ist kein Objekt.");
        var url = (payload["canonicalUrl"]?.GetValue<string>() ?? "").Trim().TrimEnd('/');
        if (url.Length > 0 && !(Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && u.PathAndQuery == "/" && string.IsNullOrEmpty(u.UserInfo)))
            return Report(op, "failed", $"„{url}“ ist keine https-Adresse ohne Pfad.");
        var proxy = url.Length > 0 && payload["behindHttpsProxy"]?.GetValue<string>() == "1" ? "1" : "";
        foreach (var (key, value) in new[] { (SettingKeys.CanonicalUrl, url), (SettingKeys.BehindHttpsProxy, proxy) })
        {
            var row = await _db.SiteSettings.FirstOrDefaultAsync(s => s.Key == key, ct);
            if (row is null) _db.SiteSettings.Add(new SiteSetting { Key = key, Value = value });
            else row.Value = value;
        }
        await _db.SaveChangesAsync(ct);
        return Report(op, "applied", url.Length > 0 ? $"Öffentliche Adresse: {url}." : "Öffentliche Adresse zurückgesetzt.");
    }

    private async Task<ContentOpReport> SetSettingAsync(PendingContentOp op, CancellationToken ct)
    {
        var payload = JsonNode.Parse(op.PayloadJson) as JsonObject
            ?? throw new InvalidOperationException("PayloadJson ist kein Objekt.");
        var key = (payload["key"]?.GetValue<string>() ?? "").Trim();
        var value = payload["value"]?.GetValue<string>() ?? "";
        if (string.IsNullOrWhiteSpace(key)) return Report(op, "failed", "Kein Schlüssel angegeben.");

        // The cloud-link keys live in the same table; letting an op set them would let a change hijack the
        // instance's cloud connection. Same guard as the profile sync applier (CloudSyncService).
        if (SettingKeys.IsProtected(key))
            return Report(op, "failed", $"Schlüssel „{key}“ ist geschützt (Cloud-Verbindung, Erlaubnis für native Plugins) und kann von außen nicht gesetzt werden.");
        // Script fields and sign-in/security policy: never from the content channel (see ContentOpRules for why).
        if (MatCMS.Shared.ContentOpRules.IsRefusedSetting(key))
            return Report(op, "failed", $"Schlüssel „{key}“ (Code, Sicherheit, Anmeldung, Mail, Adresse) wird über diesen Weg nicht gesetzt – bitte im Admin der Website oder im Profil.");

        var row = await _db.SiteSettings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (row is null) { _db.SiteSettings.Add(new SiteSetting { Key = key, Value = value }); }
        else row.Value = value;
        await _db.SaveChangesAsync(ct);
        return Report(op, "applied", $"Einstellung „{key}“ gesetzt.");
    }

    // --- Cleanup (cloud-orchestrated "Aufräumen") ------------------------------------------------------
    // The cloud only asks; the instance runs its OWN CleanupService (the same one the Admin page uses), so
    // "unused" is judged with the full content model, never from a parsed backup.

    /// <summary>Read op: returns the unused-media/component + plugin report so the operator can pick what
    /// to delete. NOT restore-gated (reading is not writing).</summary>
    private async Task<ContentOpReport> AnalyzeCleanupAsync(PendingContentOp op, CancellationToken ct)
    {
        var report = await _cleanup.AnalyzeAsync(ct);
        return ReportResult(op, JsonSerializer.Serialize(report),
            $"{report.UnusedMedia.Count} Medien unbenutzt, {report.StrayFiles.Count} verwaiste Dateien ({report.UnusedComponents.Count} Komponenten), {report.Plugins.Count} Plugins.");
    }

    /// <summary>Write op (restore-gated on the cloud side): deletes exactly the passed selections.
    /// CleanupService re-checks "still unused" before removing anything.</summary>
    private async Task<ContentOpReport> ApplyCleanupAsync(PendingContentOp op, CancellationToken ct)
    {
        var payload = JsonNode.Parse(op.PayloadJson) as JsonObject
            ?? throw new InvalidOperationException("PayloadJson ist kein Objekt.");
        var mediaIds = (payload["mediaIds"] as JsonArray)?.Select(n => n!.GetValue<int>()).ToArray() ?? [];
        var compTypes = (payload["componentTypes"] as JsonArray)?.Select(n => n!.GetValue<string>()).ToArray() ?? [];
        var pluginKeys = (payload["pluginKeys"] as JsonArray)?.Select(n => n!.GetValue<string>()).ToArray() ?? [];
        var (media, comps, plugins) = await _cleanup.DeleteAsync(mediaIds, compTypes, pluginKeys, ct);
        var files = await _cleanup.DeleteStrayFilesAsync((payload["strayFiles"] as JsonArray)?.Select(n => n!.GetValue<string>()).ToArray() ?? [], ct);
        return Report(op, "applied", $"Aufgeräumt: {media} Medien, {files} verwaiste Dateien, {comps} Komponenten, {plugins} Plugins gelöscht.");
    }

    // --- Reads: the instance serializes its OWN content back (the cloud never parses the format) ----------

    private async Task<ContentOpReport> ListPagesAsync(PendingContentOp op, CancellationToken ct)
    {
        // A plugin's pages are laid out in the editor, not through the cloud: the slug of one cannot even be written (it starts with an underscore).
        var pages = await _db.Pages.AsNoTracking().Where(p => p.OwnerPlugin == null)
            .OrderBy(p => p.Locale).ThenBy(p => p.NavOrder).ThenBy(p => p.Title)
            .Select(p => new { slug = p.Slug, title = p.Title, locale = p.Locale, published = p.IsPublished, showInNav = p.ShowInNav })
            .ToListAsync(ct);
        return ReportResult(op, JsonSerializer.Serialize(new { pages }), $"{pages.Count} Seite(n).");
    }

    private async Task<ContentOpReport> ReadPageAsync(PendingContentOp op, CancellationToken ct)
    {
        var payload = JsonNode.Parse(op.PayloadJson) as JsonObject
            ?? throw new InvalidOperationException("PayloadJson ist kein Objekt.");
        var slug = Slugify(payload["slug"]?.GetValue<string>() ?? "");
        if (string.IsNullOrWhiteSpace(slug)) return Report(op, "failed", "Kein Slug angegeben.");

        var page = await _db.Pages.AsNoTracking()
            .Where(p => p.Slug == slug && p.Locale == Localizer.DefaultCulture)
            .Select(p => new { p.Id, p.Title, p.Slug, p.IsPublished })
            .FirstOrDefaultAsync(ct);
        if (page is null) return Report(op, "failed", $"Seite „{slug}“ nicht gefunden.");

        // Top-level blocks in order, each with its ID — page.updateBlocks edits a block BY that id, so a read → edit round trip
        // keeps everything the model did not touch. `editable` names the text fields it may change (the validator's list);
        // `children` says a block holds others, which an edit keeps as they are. DataJson is forwarded as-is.
        var blocks = await _db.ContentBlocks.AsNoTracking()
            .Where(b => b.PageId == page.Id && b.ParentId == null)
            .OrderBy(b => b.SortOrder)
            .Select(b => new { b.Id, type = b.BlockType, dataJson = b.DataJson, children = _db.ContentBlocks.Count(c => c.ParentId == b.Id) })
            .ToListAsync(ct);
        var editable = EditableFields();

        var result = new JsonObject
        {
            ["title"] = page.Title,
            ["slug"] = page.Slug,
            ["published"] = page.IsPublished,
            ["blocks"] = new JsonArray(blocks.Select(b => (JsonNode)new JsonObject
            {
                ["id"] = b.Id,
                ["type"] = b.type,
                ["editable"] = new JsonArray((editable.TryGetValue(b.type, out var f) ? f : new List<string>()).Select(x => (JsonNode)x).ToArray()),
                ["children"] = b.children,
                ["data"] = JsonNode.Parse(string.IsNullOrWhiteSpace(b.dataJson) ? "{}" : b.dataJson),
            }).ToArray()),
        };
        return ReportResult(op, result.ToJsonString(), $"Seite „{slug}“ mit {blocks.Count} Block/Blöcken gelesen.");
    }

    private async Task<ContentOpReport> UpdatePageBlocksAsync(PendingContentOp op, CancellationToken ct)
    {
        var payload = JsonNode.Parse(op.PayloadJson) as JsonObject
            ?? throw new InvalidOperationException("PayloadJson ist kein Objekt.");
        var slug = Slugify(payload["slug"]?.GetValue<string>() ?? "");
        if (string.IsNullOrWhiteSpace(slug)) return Report(op, "failed", "Kein Slug angegeben.");

        var page = await _db.Pages
            .Include(p => p.Blocks)
            .FirstOrDefaultAsync(p => p.Slug == slug && p.Locale == Localizer.DefaultCulture, ct);
        // Overwrite, so the page MUST already exist — this never creates one (that is page.create's job).
        if (page is null) return Report(op, "failed", $"Seite „{slug}“ nicht gefunden.");

        if (payload["blocks"] is not JsonArray incoming || incoming.Count == 0)
            return Report(op, "failed", "Keine Blöcke angegeben — Seite unverändert gelassen.");
        // An older cloud sends no `removeMissing` and no ids: its list IS the whole page (the old replace). Treating a missing
        // key as "keep the rest" would leave its old blocks AND add its new copies — every edit would duplicate the page.
        // The current cloud always sends the key.
        var removeMissing = payload["removeMissing"] is not JsonValue rm || (rm.TryGetValue<bool>(out var rmv) && rmv);

        // A PATCH, not a replace. It used to throw the page's blocks away and write back only what the text validator
        // let through: a get_page → update_page_blocks round trip silently lost every picture, link, list, layout field,
        // nested block, plugin block and everything after the 12th block. Now:
        //  - a block WITH an id (from get_page) is the existing block: only the text fields it sends are validated and
        //    written over its stored data; every other field and its children stay. Its type cannot change.
        //  - a block WITHOUT an id is new and goes through the validator as before.
        //  - an existing block the list does not name stays where it was (after the named ones) unless removeMissing is
        //    true — a model that left blocks out by accident must not delete them.
        var top = page.Blocks.Where(b => b.ParentId == null).OrderBy(b => b.SortOrder).ToList();
        var byId = top.ToDictionary(b => b.Id);
        var ordered = new List<ContentBlock>();
        var notes = new List<string>();
        var editable = EditableFields();
        int edited = 0, added = 0;
        foreach (var item in incoming)
        {
            if (item is not JsonObject obj) continue;
            var type = (obj["type"] as JsonValue)?.ToString() ?? "";
            // An item that carries an id in ANY form is an existing block, never a new one: a model that sent "12" or "Id"
            // would otherwise get a copy, and with removeMissing the original would be deleted.
            var idNode = obj.FirstOrDefault(kv => kv.Key.Equals("id", StringComparison.OrdinalIgnoreCase)).Value;
            if (obj.Any(kv => kv.Key.Equals("id", StringComparison.OrdinalIgnoreCase)))
            {
                int id;
                if (idNode is JsonValue iv && (iv.TryGetValue<int>(out id) || (iv.TryGetValue<string>(out var ids) && int.TryParse(ids, out id)))) { }
                else { notes.Add($"Block-ID „{idNode?.ToJsonString()}“ ist keine Zahl – übergangen."); continue; }
                if (!byId.TryGetValue(id, out var existing)) { notes.Add($"Block {id} gibt es auf dieser Seite nicht – übergangen."); continue; }
                if (ordered.Contains(existing)) { notes.Add($"Block {id} steht doppelt in der Liste – der zweite Eintrag ist übergangen."); continue; }
                if (type.Length > 0 && !type.Equals(existing.BlockType, StringComparison.OrdinalIgnoreCase))
                    notes.Add($"Block {id}: der Typ bleibt „{existing.BlockType}“ (ändern geht nur über einen neuen Block).");
                ordered.Add(existing);
                if (obj["data"] is not JsonObject d || d.Count == 0) continue;

                var fields = editable.TryGetValue(existing.BlockType, out var f) ? f : new List<string>();
                var stored = (string.IsNullOrWhiteSpace(existing.DataJson) ? null : JsonNode.Parse(existing.DataJson) as JsonObject) ?? new JsonObject();
                var changes = new JsonObject();
                var cleared = new List<string>();
                var ignored = new List<string>();
                foreach (var kv in d)
                {
                    // The field's own id, whatever casing the model used — written under any other spelling it would be a
                    // second key next to the real one, and the edit would silently not show.
                    var fid = fields.FirstOrDefault(x => x.Equals(kv.Key, StringComparison.OrdinalIgnoreCase));
                    if (fid is null) { ignored.Add(kv.Key); continue; }
                    var sent = kv.Value is JsonValue sv && sv.TryGetValue<string>(out var s) ? s : null;
                    if (sent is null) { ignored.Add(kv.Key); continue; }
                    var current = stored.FirstOrDefault(x => x.Key.Equals(fid, StringComparison.OrdinalIgnoreCase));
                    if (current.Value is JsonValue cv && cv.TryGetValue<string>(out var cs) && cs == sent) continue; // unchanged: leave it alone
                    if (sent.Trim().Length == 0) { cleared.Add(fid); continue; }                                      // "" empties the field
                    changes[fid] = sent;
                }
                var check = changes.Count == 0 ? new() : _blockGen.ValidateBlocks(new JsonArray(new JsonObject { ["type"] = existing.BlockType, ["data"] = changes }));
                var written = check.Count == 0 ? new JsonObject() : check[0].Data;
                foreach (var key in changes.Select(kv => kv.Key).Where(k => !written.ContainsKey(k))) ignored.Add(key);
                if (written.Count == 0 && cleared.Count == 0)
                {
                    if (ignored.Count > 0) notes.Add($"Block {id} ({existing.BlockType}): Felder {string.Join(", ", ignored)} nicht geändert (nur Textfelder sind änderbar).");
                    continue;
                }
                foreach (var key in stored.Select(x => x.Key).Where(k => written.Any(w => w.Key.Equals(k, StringComparison.OrdinalIgnoreCase)) || cleared.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList())
                    stored.Remove(key);
                foreach (var kv in written) stored[kv.Key] = kv.Value?.DeepClone();
                if (ignored.Count > 0) notes.Add($"Block {id}: Felder {string.Join(", ", ignored)} nicht geändert (nur Textfelder sind änderbar).");
                existing.DataJson = stored.ToJsonString();
                edited++;
            }
            else
            {
                var fresh = _blockGen.ValidateBlocks(new JsonArray(obj.DeepClone()));
                if (fresh.Count == 0) { notes.Add($"Neuer Block „{type}“ ist ungültig oder leer – übergangen."); continue; }
                var nb = new ContentBlock { PageId = page.Id, BlockType = fresh[0].Type, DataJson = fresh[0].Data.ToJsonString() };
                page.Blocks.Add(nb);
                ordered.Add(nb);
                added++;
            }
        }
        if (ordered.Count == 0)
            return Report(op, "failed", "Keine gültigen Blöcke — Seite unverändert gelassen." + (notes.Count > 0 ? " " + string.Join(" ", notes) : ""));

        var missing = top.Where(b => !ordered.Contains(b)).ToList();
        var removed = 0;
        if (removeMissing)
        {
            // Children hang off their parent and go with it (cascade), exactly as deleting the block in the editor.
            _db.ContentBlocks.RemoveRange(missing);
            removed = missing.Count;
        }
        else ordered.AddRange(missing);

        var sort = 0;
        foreach (var b in ordered) b.SortOrder = sort++;
        page.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        var summary = $"Seite „{slug}“: {edited} geändert, {added} neu, {removed} entfernt"
            + (!removeMissing && missing.Count > 0 ? $", {missing.Count} nicht genannte behalten" : "") + ".";
        return Report(op, notes.Count == 0 ? "applied" : "partial", notes.Count == 0 ? summary : summary + " " + string.Join(" ", notes));
    }

    /// <summary>Block type → the text fields a content op may change (the validator's own list, so read and write agree).</summary>
    private Dictionary<string, List<string>> EditableFields() =>
        _blockGen.AllowedBlocks().GroupBy(b => b.Type, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Fields.Select(f => f.Id).ToList(), StringComparer.OrdinalIgnoreCase);

    private async Task<int> NextNavOrderAsync(CancellationToken ct)
    {
        var max = await _db.Pages.Where(p => p.Locale == Localizer.DefaultCulture && p.ShowInNav)
            .MaxAsync(p => (int?)p.NavOrder, ct);
        return (max ?? 0) + 1;
    }

    private static ContentOpReport Report(PendingContentOp op, string outcome, string detail) =>
        new() { OpId = op.OpId, Outcome = outcome, Detail = detail };

    /// <summary>A successful READ: the serialized content rides in <see cref="ContentOpReport.ResultJson"/>.</summary>
    private static ContentOpReport ReportResult(PendingContentOp op, string resultJson, string detail) =>
        new() { OpId = op.OpId, Outcome = "applied", Detail = detail, ResultJson = resultJson };

    /// <summary>Lowercase, ASCII, hyphen-separated — the same shape a slug has everywhere else on the site.
    /// Deliberately conservative: a model-supplied slug is normalised, never trusted verbatim.</summary>
    private static string Slugify(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw.Trim().ToLowerInvariant())
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(ch);
            else if (ch is ' ' or '-' or '_' or '.') sb.Append('-');
            else if (ch == 'ä') sb.Append("ae");
            else if (ch == 'ö') sb.Append("oe");
            else if (ch == 'ü') sb.Append("ue");
            else if (ch == 'ß') sb.Append("ss");
            // everything else (punctuation, other scripts) is dropped
        }
        var s = sb.ToString();
        while (s.Contains("--")) s = s.Replace("--", "-");
        return s.Trim('-');
    }
}
