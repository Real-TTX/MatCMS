using System.Text.Json;
using MatCMS.Content;
using MatCMS.Data;
using MatCMS.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using PagesIndex = MatCMS.Pages.Admin.Pages.IndexModel;

namespace MatCMS.Pages.Admin.Forms;

public class EditModel : PageModel
{
    private readonly AppDbContext _db;
    public EditModel(AppDbContext db) => _db = db;

    public Form Current { get; private set; } = default!;
    public List<FormElement> Elements { get; private set; } = new();

    public FormElement? SelectedElement { get; private set; }
    public string ElementJsonData { get; private set; } = "null";
    public string AvailableFieldsJson { get; private set; } = "[]";

    [BindProperty] public FormMetaInput Meta { get; set; } = new();
    [BindProperty] public string ElementJson { get; set; } = "";
    [BindProperty] public SettingsInput Settings { get; set; } = new();
    /// <summary>The unsaved draft definition (all elements as JSON) posted by the live-preview push.</summary>
    [BindProperty] public string? Draft { get; set; }

    /// <summary>All users — offered as selectable notification recipients (those with an e-mail).</summary>
    public List<User> AllUsers { get; private set; } = new();

    public class FormMetaInput
    {
        public string Name { get; set; } = "";
        public string Slug { get; set; } = "";
    }

    public class SettingsInput
    {
        public string SuccessMessage { get; set; } = "";
        public string SubmitLabel { get; set; } = "";
        public bool NotifyEnabled { get; set; }
        public List<int> NotifyUserIds { get; set; } = new();
        public string NotifyEmails { get; set; } = "";
    }

    /// <summary>Element types offered in the "+ Element" picker (inner SVG markup for a 0 0 24 24 icon).</summary>
    public static readonly (string Type, string Svg)[] ElementTypes =
    [
        ("title",       @"<path d=""M4 6h16""/><path d=""M4 12h10""/><path d=""M4 18h7""/>"),
        ("description", @"<path d=""M4 7h16""/><path d=""M4 12h16""/><path d=""M4 17h11""/>"),
        ("text",        @"<rect x=""3"" y=""8"" width=""18"" height=""8"" rx=""2""/><path d=""M7 12h6""/>"),
        ("textarea",    @"<rect x=""3"" y=""5"" width=""18"" height=""14"" rx=""2""/><path d=""M7 9h10""/><path d=""M7 13h10""/><path d=""M7 17h6""/>"),
        ("date",        @"<rect x=""3"" y=""5"" width=""18"" height=""16"" rx=""2""/><path d=""M3 9h18""/><path d=""M8 3v4""/><path d=""M16 3v4""/>"),
        ("daterange",   @"<rect x=""3"" y=""5"" width=""18"" height=""16"" rx=""2""/><path d=""M3 9h18""/><path d=""M8 3v4""/><path d=""M16 3v4""/><path d=""M8 14l8 0""/>"),
        ("number",      @"<path d=""M6 4l-1 16""/><path d=""M14 4l-1 16""/><path d=""M4 9h16""/><path d=""M3 15h16""/>"),
        ("phone",       @"<path d=""M5 4h4l2 5-3 2a12 12 0 0 0 5 5l2-3 5 2v4a2 2 0 0 1-2 2A16 16 0 0 1 3 6a2 2 0 0 1 2-2""/>"),
        ("email",       @"<rect x=""3"" y=""5"" width=""18"" height=""14"" rx=""2""/><path d=""M4 7l8 6 8-6""/>"),
        ("select",      @"<rect x=""3"" y=""6"" width=""18"" height=""12"" rx=""2""/><path d=""M8 11l4 3 4-3""/>"),
        ("richselect",  @"<rect x=""3"" y=""5"" width=""18"" height=""14"" rx=""2""/><rect x=""5"" y=""8"" width=""6"" height=""8"" rx=""1""/><path d=""M13 9h6""/><path d=""M13 13h4""/>"),
        ("group",       @"<rect x=""3"" y=""4"" width=""18"" height=""16"" rx=""2""/><path d=""M3 9h18""/>"),
    ];

    public async Task<IActionResult> OnGetAsync(int id, string? element)
    {
        var form = await _db.Forms.FindAsync(id);
        if (form is null) return NotFound();

        Current = form;
        Meta = new FormMetaInput { Name = form.Name, Slug = form.Slug };
        Elements = FormDefinition.Parse(form.DefinitionJson);

        AllUsers = await _db.Users.AsNoTracking().OrderBy(u => u.Username).ToListAsync();
        var notify = FormNotify.Parse(form.NotifyJson);
        Settings = new SettingsInput
        {
            SuccessMessage = form.SuccessMessage ?? "",
            SubmitLabel = form.SubmitLabel ?? "",
            NotifyEnabled = form.NotifyEnabled,
            NotifyUserIds = notify.UserIds,
            NotifyEmails = string.Join("\n", notify.Emails)
        };

        if (!string.IsNullOrWhiteSpace(element))
        {
            // A field inside a group isn't a top-level element — clicking it in the preview selects
            // its parent group (whose panel then shows that child), instead of resolving to nothing.
            SelectedElement = Elements.FirstOrDefault(e => e.Id == element)
                ?? Elements.FirstOrDefault(e => e.Type == "group" && e.Fields.Any(f => f.Id == element));
            if (SelectedElement is not null)
            {
                ElementJsonData = FormDefinition.Serialize(new[] { SelectedElement })[1..^1]; // unwrap the single-element array
                // Fields available as condition sources: all input fields except the selected element (and its own children).
                // Include each field's type + (for selects) its options, so the condition editor can offer the
                // real option VALUES to compare against — a free-text label never matches the submitted value.
                var ownChildIds = SelectedElement.Fields.Select(f => f.Id).ToHashSet();
                var available = FormDefinition.Flatten(Elements)
                    .Where(e => FormDefinition.IsInput(e.Type) && e.Id != SelectedElement.Id && !ownChildIds.Contains(e.Id))
                    .Select(e => new
                    {
                        id = e.Id,
                        label = string.IsNullOrWhiteSpace(e.Label) ? e.Id : e.Label,
                        type = e.Type,
                        options = (e.Options ?? new()).Select(o => new { value = o.Value, label = o.Label }).ToList()
                    })
                    .ToList();
                AvailableFieldsJson = JsonSerializer.Serialize(available, FormDefinition.Opts);
            }
        }
        return Page();
    }

    public async Task<IActionResult> OnPostAddElementAsync(int id, string type)
    {
        var form = await _db.Forms.FindAsync(id);
        if (form is null) return NotFound();
        if (!ElementTypes.Any(t => t.Type == type)) return RedirectToPage(new { id });

        var elements = FormDefinition.Parse(form.DefinitionJson);
        var el = new FormElement { Id = GenId(), Type = type, Label = DefaultLabel(type) };
        if (type == "select")
            el.Options = new() { new FormOption { Value = "option-1", Label = "Option 1" } };
        elements.Add(el);

        form.DefinitionJson = FormDefinition.Serialize(elements);
        await _db.SaveChangesAsync();
        return RedirectToPage(new { id, element = el.Id });
    }

    public async Task<IActionResult> OnPostSaveElementAsync(int id, string elementId)
    {
        var form = await _db.Forms.FindAsync(id);
        if (form is null) return NotFound();

        FormElement? incoming;
        try { incoming = JsonSerializer.Deserialize<FormElement>(ElementJson, FormDefinition.Opts); }
        catch { incoming = null; }
        if (incoming is null)
        {
            TempData["FlashError"] = "Das Element konnte nicht gespeichert werden (ungültiges Format).";
            return RedirectToPage(new { id, element = elementId });
        }

        var elements = FormDefinition.Parse(form.DefinitionJson);
        var idx = elements.FindIndex(e => e.Id == elementId);
        if (idx < 0) return RedirectToPage(new { id });

        incoming.Id = elementId; // never let the client change the id
        Sanitize(incoming);
        elements[idx] = incoming;

        form.DefinitionJson = FormDefinition.Serialize(elements);
        await _db.SaveChangesAsync();
        TempData["Flash"] = "Element gespeichert.";
        return RedirectToPage(new { id, element = elementId });
    }

    public async Task<IActionResult> OnPostDeleteElementAsync(int id, string elementId)
    {
        var form = await _db.Forms.FindAsync(id);
        if (form is null) return NotFound();

        var elements = FormDefinition.Parse(form.DefinitionJson);
        elements.RemoveAll(e => e.Id == elementId);
        form.DefinitionJson = FormDefinition.Serialize(elements);
        await _db.SaveChangesAsync();
        TempData["Flash"] = "Element gelöscht.";
        return RedirectToPage(new { id });
    }

    /// <summary>Live preview: render the current (unsaved) DRAFT definition to form HTML — no DB write.
    /// The builder posts the whole draft on every field change and swaps the preview iframe content.</summary>
    public IActionResult OnPostRenderPreview(int id)
    {
        List<FormElement> els;
        try { els = FormDefinition.Parse(string.IsNullOrWhiteSpace(Draft) ? "[]" : Draft!); }
        catch { els = new(); }
        var model = new FormRenderModel
        {
            FormId = id,
            Slug = "preview",
            Name = "",
            Elements = els,
            SubmitLabel = _db.Forms.AsNoTracking().Where(f => f.Id == id).Select(f => f.SubmitLabel).FirstOrDefault(),
            Preview = true,
            Builder = false
        };
        return Partial("Blocks/_FormRender", model);
    }

    public async Task<IActionResult> OnPostReorderAsync(int id, string[] order)
    {
        var form = await _db.Forms.FindAsync(id);
        if (form is null) return NotFound();

        var elements = FormDefinition.Parse(form.DefinitionJson);
        if (order is { Length: > 0 })
        {
            var ordered = order
                .Select(oid => elements.FirstOrDefault(e => e.Id == oid))
                .Where(e => e is not null)
                .Select(e => e!)
                .ToList();
            // Append any element not covered by the posted order (safety).
            ordered.AddRange(elements.Where(e => !order.Contains(e.Id)));
            form.DefinitionJson = FormDefinition.Serialize(ordered);
            await _db.SaveChangesAsync();
        }
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostMetaAsync(int id)
    {
        var form = await _db.Forms.FindAsync(id);
        if (form is null) return NotFound();

        var slug = PagesIndex.Slugify(string.IsNullOrWhiteSpace(Meta.Slug) ? Meta.Name : Meta.Slug);
        if (string.IsNullOrWhiteSpace(Meta.Name) || string.IsNullOrWhiteSpace(slug))
        {
            TempData["FlashError"] = "Name und Slug dürfen nicht leer sein.";
            return RedirectToPage(new { id });
        }
        if (await _db.Forms.AnyAsync(f => f.Slug == slug && f.Id != id))
        {
            TempData["FlashError"] = $"Der Slug „{slug}“ ist bereits vergeben.";
            return RedirectToPage(new { id });
        }

        form.Name = Meta.Name.Trim();
        form.Slug = slug;
        await _db.SaveChangesAsync();
        TempData["Flash"] = "Formular-Einstellungen gespeichert.";
        return RedirectToPage(new { id });
    }

    /// <summary>Saves the confirmation message and e-mail-notification settings of the form.</summary>
    public async Task<IActionResult> OnPostSettingsAsync(int id)
    {
        var form = await _db.Forms.FindAsync(id);
        if (form is null) return NotFound();

        form.SuccessMessage = string.IsNullOrWhiteSpace(Settings.SuccessMessage) ? null : Settings.SuccessMessage.Trim();
        form.SubmitLabel = string.IsNullOrWhiteSpace(Settings.SubmitLabel) ? null : Settings.SubmitLabel.Trim();
        form.NotifyEnabled = Settings.NotifyEnabled;
        form.NotifyJson = new FormNotify
        {
            UserIds = (Settings.NotifyUserIds ?? new()).Distinct().ToList(),
            Emails = FormNotify.ParseEmails(Settings.NotifyEmails)
        }.Serialize();

        await _db.SaveChangesAsync();
        TempData["Flash"] = "Meldung & Benachrichtigungen gespeichert.";
        return RedirectToPage(new { id });
    }

    // --- helpers -------------------------------------------------------

    private static string GenId() => "f" + Guid.NewGuid().ToString("N")[..8];

    private static string DefaultLabel(string type) => type switch
    {
        "title" => "Überschrift",
        "description" => "Beschreibungstext",
        "text" => "Textfeld",
        "textarea" => "Textfeld (mehrzeilig)",
        "date" => "Datum",
        "daterange" => "Zeitraum (Anreise – Abreise)",
        "number" => "Zahl",
        "phone" => "Telefon",
        "email" => "E-Mail",
        "select" => "Auswahl",
        "group" => "Gruppe",
        _ => "Feld"
    };

    // Clean up an element coming from the client before persisting.
    private static void Sanitize(FormElement el)
    {
        el.Type ??= "text";
        if (el.Condition is not null && !el.Condition.IsSet) el.Condition = null;

        // Options belong to select AND richselect (the rich variant also carries image/description/tags).
        static bool SelectLike(string? t) => t is "select" or "richselect";
        if (!SelectLike(el.Type)) el.Options = new();
        else el.Options = el.Options.Where(o => !string.IsNullOrWhiteSpace(o.Value)).ToList();

        if (el.Type == "group")
        {
            foreach (var child in el.Fields)
            {
                if (string.IsNullOrWhiteSpace(child.Id)) child.Id = GenId();
                if (child.Type == "group") child.Type = "text"; // no nested groups
                child.Condition = child.Condition is not null && child.Condition.IsSet ? child.Condition : null;
                if (!SelectLike(child.Type)) child.Options = new();
                else child.Options = child.Options.Where(o => !string.IsNullOrWhiteSpace(o.Value)).ToList();
                child.Fields = new();
            }
        }
        else
        {
            el.Fields = new();
        }
    }
}
