using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MatCMS.Data;
using MatCMS.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatCMS.Pages.Admin.Components;

public class EditModel : PageModel
{
    private readonly AppDbContext _db;
    public EditModel(AppDbContext db) => _db = db;

    public Component Current { get; private set; } = default!;

    [BindProperty] public string? Name { get; set; }
    [BindProperty] public string? Description { get; set; }
    [BindProperty] public string? Icon { get; set; }
    [BindProperty] public string? FieldsJson { get; set; }
    [BindProperty] public string? TemplateHtml { get; set; }
    public string? Error { get; private set; }

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var c = await _db.Components.FindAsync(id);
        if (c is null) return RedirectToPage("Index");
        Current = c;
        Name = c.Name;
        Description = c.Description;
        Icon = c.Icon;
        FieldsJson = c.FieldsJson;
        TemplateHtml = c.TemplateHtml;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int id)
    {
        var c = await _db.Components.FindAsync(id);
        if (c is null) return RedirectToPage("Index");
        Current = c;

        var name = (Name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            Error = "Bitte einen Namen angeben.";
            return Page();
        }

        c.Name = name;
        c.Description = (Description ?? "").Trim();
        c.Icon = MatCMS.Content.MenuIcons.IsValid(Icon) ? Icon!.Trim() : "";
        c.FieldsJson = SanitizeFields(FieldsJson);
        var tpl = TemplateHtml ?? "";
        c.TemplateHtml = tpl.Length > 50000 ? tpl[..50000] : tpl;
        await _db.SaveChangesAsync();

        TempData["Flash"] = "Komponente gespeichert.";
        return RedirectToPage("Index");
    }

    // Normalize the posted field list into a clean [{id,label,type}] array with slugged ids.
    private static string SanitizeFields(string? json)
    {
        try
        {
            if (JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "[]" : json) is not JsonArray arr)
                return "[]";
            var allowed = new[] { "text", "textarea", "richtext", "image", "url" };
            var outArr = new JsonArray();
            var used = new HashSet<string>();
            foreach (var el in arr)
            {
                var label = el?["label"]?.GetValue<string>()?.Trim() ?? "";
                var id = el?["id"]?.GetValue<string>()?.Trim() ?? "";
                var type = el?["type"]?.GetValue<string>()?.Trim() ?? "text";
                if (!allowed.Contains(type)) type = "text";
                if (string.IsNullOrEmpty(id)) id = Slugify(label);
                if (string.IsNullOrEmpty(id) || !used.Add(id)) continue;
                outArr.Add(new JsonObject { ["id"] = id, ["label"] = string.IsNullOrEmpty(label) ? id : label, ["type"] = type });
            }
            return outArr.ToJsonString();
        }
        catch
        {
            return "[]";
        }
    }

    private static string Slugify(string s)
    {
        s = s.Trim().ToLowerInvariant()
            .Replace("ä", "ae").Replace("ö", "oe").Replace("ü", "ue").Replace("ß", "ss");
        return Regex.Replace(s, "[^a-z0-9]+", "_").Trim('_');
    }
}
