using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using MatCMS.Cloud.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Pages.Admin.ApiKeys;

public class CreateModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly ApiKeyService _keys;
    public CreateModel(AppDbContext db, ApiKeyService keys) { _db = db; _keys = keys; }

    public List<Instance> Instances { get; private set; } = new();

    public async Task OnGetAsync()
    {
        Instances = await _db.Instances.AsNoTracking().OrderBy(i => i.Name).ToListAsync();
    }

    public async Task<IActionResult> OnPostAsync(string name, bool canRestore, string scope, int[]? instanceIds)
    {
        // "selected" = scoped to the ticked instances; anything else = all instances.
        var allInstances = scope != "selected";
        var ids = instanceIds ?? Array.Empty<int>();

        if (!allInstances && ids.Length == 0)
        {
            // A scoped key with no instances would be inert — refuse rather than create a key that
            // silently can reach nothing.
            TempData["FlashError"] = "Bitte mindestens eine Instanz auswählen oder „Alle Instanzen“ wählen.";
            await OnGetAsync();
            return Page();
        }

        var created = await _keys.CreateAsync(name, canRestore, allInstances, ids);
        // The raw key exists only here — handed to the list page to be shown exactly once.
        TempData["NewApiKey"] = created.RawKey;
        TempData["Flash"] = "API-Schlüssel erstellt.";
        return RedirectToPage("Index");
    }
}
