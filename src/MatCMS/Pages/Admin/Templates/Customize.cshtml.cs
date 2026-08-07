using MatCMS.Content;
using MatCMS.Data;
using MatCMS.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatCMS.Pages.Admin.Templates;

/// <summary>User-facing "Anpassen": edit the values of the parameters a template designer published.</summary>
public class CustomizeModel : PageModel
{
    private readonly AppDbContext _db;
    public CustomizeModel(AppDbContext db) => _db = db;

    public Template Current { get; private set; } = default!;
    public List<TemplateParam> Params { get; private set; } = new();
    public Dictionary<string, string> Values { get; private set; } = new();

    [BindProperty] public Dictionary<string, string> Val { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var t = await _db.Templates.FindAsync(id);
        if (t is null) return RedirectToPage("Index");
        Current = t;
        Params = TemplateParams.Schema(t.ParametersJson);
        Values = TemplateParams.Resolve(t);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int id)
    {
        var t = await _db.Templates.FindAsync(id);
        if (t is null) return RedirectToPage("Index");

        var obj = new System.Text.Json.Nodes.JsonObject();
        foreach (var p in TemplateParams.Schema(t.ParametersJson))
            obj[p.Id] = Val.TryGetValue(p.Id, out var v) ? (v ?? "") : "";
        t.ParamValuesJson = obj.ToJsonString();
        await _db.SaveChangesAsync();

        TempData["Flash"] = "Anpassungen gespeichert.";
        return RedirectToPage("Index");
    }
}
