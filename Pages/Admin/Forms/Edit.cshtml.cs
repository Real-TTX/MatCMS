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

    public class FormMetaInput
    {
        public string Name { get; set; } = "";
        public string Slug { get; set; } = "";
    }

    /// <summary>Element types offered in the "+ Element" picker (inner SVG markup for a 0 0 24 24 icon).</summary>
    public static readonly (string Type, string Svg)[] ElementTypes =
    [
        ("title",       @"<path d=""M4 6h16""/><path d=""M4 12h10""/><path d=""M4 18h7""/>"),
        ("description", @"<path d=""M4 7h16""/><path d=""M4 12h16""/><path d=""M4 17h11""/>"),
        ("text",        @"<rect x=""3"" y=""8"" width=""18"" height=""8"" rx=""2""/><path d=""M7 12h6""/>"),
        ("date",        @"<rect x=""3"" y=""5"" width=""18"" height=""16"" rx=""2""/><path d=""M3 9h18""/><path d=""M8 3v4""/><path d=""M16 3v4""/>"),
        ("number",      @"<path d=""M6 4l-1 16""/><path d=""M14 4l-1 16""/><path d=""M4 9h16""/><path d=""M3 15h16""/>"),
        ("phone",       @"<path d=""M5 4h4l2 5-3 2a12 12 0 0 0 5 5l2-3 5 2v4a2 2 0 0 1-2 2A16 16 0 0 1 3 6a2 2 0 0 1 2-2""/>"),
        ("email",       @"<rect x=""3"" y=""5"" width=""18"" height=""14"" rx=""2""/><path d=""M4 7l8 6 8-6""/>"),
        ("select",      @"<rect x=""3"" y=""6"" width=""18"" height=""12"" rx=""2""/><path d=""M8 11l4 3 4-3""/>"),
        ("group",       @"<rect x=""3"" y=""4"" width=""18"" height=""16"" rx=""2""/><path d=""M3 9h18""/>"),
    ];

    public async Task<IActionResult> OnGetAsync(int id, string? element)
    {
        var form = await _db.Forms.FindAsync(id);
        if (form is null) return NotFound();

        Current = form;
        Meta = new FormMetaInput { Name = form.Name, Slug = form.Slug };
        Elements = FormDefinition.Parse(form.DefinitionJson);

        if (!string.IsNullOrWhiteSpace(element))
        {
            SelectedElement = Elements.FirstOrDefault(e => e.Id == element);
            if (SelectedElement is not null)
            {
                ElementJsonData = FormDefinition.Serialize(new[] { SelectedElement })[1..^1]; // unwrap the single-element array
                // Fields available as condition sources: all input fields except the selected element (and its own children).
                var ownChildIds = SelectedElement.Fields.Select(f => f.Id).ToHashSet();
                var available = FormDefinition.Flatten(Elements)
                    .Where(e => FormDefinition.IsInput(e.Type) && e.Id != SelectedElement.Id && !ownChildIds.Contains(e.Id))
                    .Select(e => new { id = e.Id, label = string.IsNullOrWhiteSpace(e.Label) ? e.Id : e.Label })
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

    // --- helpers -------------------------------------------------------

    private static string GenId() => "f" + Guid.NewGuid().ToString("N")[..8];

    private static string DefaultLabel(string type) => type switch
    {
        "title" => "Überschrift",
        "description" => "Beschreibungstext",
        "text" => "Textfeld",
        "date" => "Datum",
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

        if (el.Type != "select") el.Options = new();
        else el.Options = el.Options.Where(o => !string.IsNullOrWhiteSpace(o.Value)).ToList();

        if (el.Type == "group")
        {
            foreach (var child in el.Fields)
            {
                if (string.IsNullOrWhiteSpace(child.Id)) child.Id = GenId();
                if (child.Type == "group") child.Type = "text"; // no nested groups
                child.Condition = child.Condition is not null && child.Condition.IsSet ? child.Condition : null;
                if (child.Type != "select") child.Options = new();
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
