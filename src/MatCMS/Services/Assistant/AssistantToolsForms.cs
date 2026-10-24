using System.Text.Json;
using System.Text.Json.Nodes;
using MatCMS.Content;
using MatCMS.Models;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Services.Assistant;

/// <summary>
/// Forms: read, build and change them — the same definition the form builder writes (<see cref="FormDefinition"/>), cleaned by the same rules
/// (<see cref="FormDefinition.Sanitize"/>). A form is shown on a page with the block "form" (field form = the form's slug).
/// <para><b>What the assistant does NOT touch: where submissions go.</b> The notification (to whom a submission is mailed) stays the admin's — an
/// attached document or a page text could otherwise steer the model into sending every visitor's message to an address of its choosing. Nor is
/// a form's slug changed (blocks point at it) or a form deleted (its submissions hang on it).</para>
/// </summary>
public sealed partial class AssistantTools
{
    /// <summary>A property, like <see cref="MoreCatalog"/>: the catalogue is built from it, and static field order across files is undefined.</summary>
    private static (string Name, string Args, string What, bool Writes)[] FormCatalog =>
    [
        ("read_form", "{\"form\": \"slug\"}", "Ein Formular mit allen Feldern lesen (die Liste aller Formulare steht im site_overview).", false),
        ("create_form", "{\"name\": \"Kontakt\", \"slug\"?, \"fields\": [Feld…], \"successMessage\"?, \"submitLabel\"?}",
            "Neues Formular anlegen. Feld = {\"id\", \"type\", \"label\", \"required\"?, \"placeholder\"?, \"options\"?: [\"A\", \"B\"] oder [{\"value\", \"label\"}], \"fields\"?: [Feld…] (nur bei group)}. Typen: text (einzeilig), textarea (mehrzeilig – für Nachrichten), email, phone, number, date, daterange, select, multiselect, richselect, title (Überschrift), description (Text), group. Auf eine Seite bringt es der Block {\"type\": \"form\", \"data\": {\"form\": slug}}. Wohin Einsendungen gemailt werden, stellt der Nutzer selbst ein.", true),
        ("update_form", "{\"form\": \"slug\", \"name\"?, \"fields\"?: [Feld…], \"successMessage\"?, \"submitLabel\"?}", "Formular ändern; fields ersetzt die ganze Feldliste (eine geänderte Feld-id trennt sie von früheren Einsendungen). Slug und Benachrichtigung bleiben.", true),
    ];

    private const int MaxFormElements = 60, MaxFormLabel = 300;

    private async Task<Form> FindFormAsync(JsonNode? which, CancellationToken ct, bool tracked)
    {
        var key = Str(which).Trim();
        var q = tracked ? _db.Forms : _db.Forms.AsNoTracking();
        return (int.TryParse(key, out var id) ? await q.FirstOrDefaultAsync(f => f.Id == id, ct) : null)
            ?? await q.FirstOrDefaultAsync(f => f.Slug == key, ct)
            ?? throw new ToolError($"Formular „{key}“ nicht gefunden", $"Ein Formular \"{key}\" gibt es nicht. Vorhanden: {string.Join(", ", await _db.Forms.AsNoTracking().Select(f => f.Slug).ToListAsync(ct))}.");
    }

    private static JsonObject FormJson(Form f) => new()
    {
        ["id"] = f.Id, ["slug"] = f.Slug, ["name"] = f.Name, ["successMessage"] = f.SuccessMessage, ["submitLabel"] = f.SubmitLabel,
        ["fields"] = JsonNode.Parse(FormDefinition.Serialize(FormDefinition.Parse(f.DefinitionJson))),
        ["notification"] = f.NotifyEnabled ? "an (vom Nutzer eingestellt)" : "aus (stellt der Nutzer ein)"
    };

    private async Task<ToolOutcome> ReadFormAsync(JsonObject args, CancellationToken ct)
    {
        var f = await FindFormAsync(args["form"] ?? args["slug"], ct, tracked: false);
        return Ok($"Formular „{f.Name}“ gelesen", FormJson(f));
    }

    /// <summary>
    /// The fields as the model wrote them, made into the builder's definition: the model's words for types and missing ids are put right
    /// (<see cref="ContentOpApplier.NormalizeFormFields"/>, also inside groups), options may be plain strings, every element goes through the
    /// builder's own clean-up, ids are unique across the whole form. What does not fit becomes a warning the model reads.
    /// </summary>
    private static string CleanFormFields(JsonNode? node, List<string> warnings)
    {
        if (node is not JsonArray raw || raw.Count == 0) throw new ToolError("Felder fehlen", "\"fields\" muss eine Liste von Feldern sein, mindestens eines.");
        var top = (JsonArray)raw.DeepClone();
        if (top.Count > MaxFormElements) { warnings.Add($"Nur die ersten {MaxFormElements} Felder übernommen."); while (top.Count > MaxFormElements) top.RemoveAt(top.Count - 1); }
        foreach (var el in top.OfType<JsonObject>())
        {
            Options(el);
            if (el["fields"] is JsonArray kids) { ContentOpApplier.NormalizeFormFields(kids); foreach (var k in kids.OfType<JsonObject>()) Options(k); }
        }
        ContentOpApplier.NormalizeFormFields(top);
        foreach (var el in top.OfType<JsonObject>())
            if (Str(el["type"]) is var t && t.Length > 0 && !FormDefinition.Types.Contains(t))
                warnings.Add($"Feld \"{Str(el["label"])}\": Typ \"{t}\" gibt es nicht – als Textfeld (text) angelegt.");

        var elements = FormDefinition.Parse(top.ToJsonString());
        if (elements.Count == 0) throw new ToolError("Felder unlesbar", "Die Felder ließen sich nicht lesen. Jedes Feld ist ein Objekt mit id, type und label.");
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string NewId() { for (var n = 1; ; n++) if (used.Add("feld" + n)) return "feld" + n; }
        foreach (var el in elements)
        {
            FormDefinition.Sanitize(el, NewId);
            foreach (var e in new[] { el }.Concat(el.Fields))
            {
                e.Label = Cap((e.Label ?? "").Trim(), MaxFormLabel);
                if (string.IsNullOrWhiteSpace(e.Id) || !used.Add(e.Id)) { var fresh = NewId(); warnings.Add($"Feld \"{e.Label}\": id doppelt oder leer – heißt jetzt {fresh}."); e.Id = fresh; }
                if (FormDefinition.IsInput(e.Type) && e.Label.Length == 0) warnings.Add($"Feld {e.Id} hat keine Beschriftung.");
                if (FormDefinition.HasOptions(e.Type) && e.Options.Count == 0) warnings.Add($"Auswahlfeld \"{e.Label}\" hat keine Optionen.");
            }
        }
        return FormDefinition.Serialize(elements);

        // ["A", "B"] is how a model writes a list of choices; the builder stores {value, label}.
        static void Options(JsonObject el)
        {
            if (el["options"] is not JsonArray opts) return;
            var clean = new JsonArray();
            foreach (var o in opts)
                clean.Add(o is JsonObject obj ? obj.DeepClone()
                    : new JsonObject { ["value"] = Str(o), ["label"] = Str(o) });
            el["options"] = clean;
        }
    }

    private async Task<ToolOutcome> CreateFormAsync(JsonObject args, CancellationToken ct)
    {
        RequireUnlocked("neue Formulare anlegen");
        var name = Cap(Str(args["name"]).Trim(), 120);
        if (name.Length == 0) throw new ToolError("Name fehlt", "Ein Formular braucht einen Namen (\"name\").");
        var warnings = new List<string>();
        var definition = CleanFormFields(args["fields"], warnings);
        var baseSlug = Pages.Admin.Pages.IndexModel.Slugify(Str(args["slug"]) is { Length: > 0 } s ? s : name);
        if (baseSlug.Length == 0) baseSlug = "formular";
        var slug = baseSlug;
        for (var n = 2; await _db.Forms.AnyAsync(f => f.Slug == slug, ct); n++) slug = baseSlug + "-" + n;
        if (slug != baseSlug) warnings.Add($"Den Slug \"{baseSlug}\" gibt es schon – das Formular heißt \"{slug}\".");
        var form = new Form
        {
            Name = name, Slug = slug, DefinitionJson = definition,
            SuccessMessage = Str(args["successMessage"]).Trim() is { Length: > 0 } sm ? Cap(sm, 1000) : null,
            SubmitLabel = Str(args["submitLabel"]).Trim() is { Length: > 0 } sl ? Cap(sl, 80) : null
        };
        _db.Forms.Add(form);
        await _db.SaveChangesAsync(ct);
        Journal("form", form.Id.ToString(), null);
        await _db.SaveChangesAsync(ct);
        await _blocks.LoadAsync(ct);                 // the block "form" offers the forms that exist: now this one too
        warnings.Add("Wohin Einsendungen per E-Mail gehen, stellt der Nutzer unter Formulare → Benachrichtigung ein – darauf hinweisen.");
        return Ok($"Formular „{name}“ angelegt ({slug})", new JsonObject { ["id"] = form.Id, ["slug"] = slug, ["block"] = new JsonObject { ["type"] = "form", ["data"] = new JsonObject { ["form"] = slug } } }, warnings, null, changed: true);
    }

    private async Task<ToolOutcome> UpdateFormAsync(JsonObject args, CancellationToken ct)
    {
        RequireUnlocked("Formulare ändern");
        var form = await FindFormAsync(args["form"] ?? args["slug"], ct, tracked: true);
        var warnings = new List<string>();
        var definition = args.ContainsKey("fields") ? CleanFormFields(args["fields"], warnings) : null;
        if (!Journalled("form", form.Id.ToString())) Journal("form", form.Id.ToString(), FormSnapshot(form));
        if (Str(args["name"]).Trim() is { Length: > 0 } name) form.Name = Cap(name, 120);
        if (args.ContainsKey("successMessage")) form.SuccessMessage = Str(args["successMessage"]).Trim() is { Length: > 0 } sm ? Cap(sm, 1000) : null;
        if (args.ContainsKey("submitLabel")) form.SubmitLabel = Str(args["submitLabel"]).Trim() is { Length: > 0 } sl ? Cap(sl, 80) : null;
        if (definition is not null)
        {
            var gone = FormDefinition.Flatten(FormDefinition.Parse(form.DefinitionJson)).Where(e => FormDefinition.IsInput(e.Type)).Select(e => e.Id)
                .Except(FormDefinition.Flatten(FormDefinition.Parse(definition)).Select(e => e.Id)).ToList();
            if (gone.Count > 0) warnings.Add($"Felder entfernt oder umbenannt: {string.Join(", ", gone)} – frühere Einsendungen behalten ihre Werte, das Formular fragt sie nicht mehr ab.");
            form.DefinitionJson = definition;
        }
        foreach (var k in new[] { "slug", "notify", "notification", "notifyEmails", "recipients" }.Where(args.ContainsKey))
            warnings.Add($"\"{k}\" ändert der Assistent nicht (Slug: Blöcke verweisen darauf; Benachrichtigung: stellt der Nutzer selbst ein).");
        await _db.SaveChangesAsync(ct);
        return Ok($"Formular „{form.Name}“ geändert", new JsonObject { ["id"] = form.Id, ["slug"] = form.Slug }, warnings, null, changed: true);
    }

    internal static string FormSnapshot(Form f) =>
        JsonSerializer.Serialize(new { f.Name, f.DefinitionJson, f.SuccessMessage, f.SubmitLabel });
}
