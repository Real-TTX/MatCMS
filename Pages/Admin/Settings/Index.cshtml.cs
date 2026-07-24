using MatCMS.Data;
using MatCMS.Models;
using MatCMS.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Pages.Admin.Settings;

public class IndexModel : PageModel
{
    private readonly AppDbContext _db;
    public IndexModel(AppDbContext db) => _db = db;

    [BindProperty] public Dictionary<string, string> Values { get; set; } = new();

    public async Task OnGetAsync()
    {
        var existing = await _db.SiteSettings.ToDictionaryAsync(s => s.Key, s => s.Value);
        foreach (var key in SettingKeys.All)
            Values[key] = existing.TryGetValue(key, out var v) ? v : "";
    }

    public async Task<IActionResult> OnPostAsync()
    {
        foreach (var key in SettingKeys.All)
        {
            var value = Values.TryGetValue(key, out var v) ? (v ?? "") : "";
            var setting = await _db.SiteSettings.FirstOrDefaultAsync(s => s.Key == key);
            if (setting is null)
                _db.SiteSettings.Add(new SiteSetting { Key = key, Value = value });
            else
                setting.Value = value;
        }
        await _db.SaveChangesAsync();
        TempData["Flash"] = "Einstellungen gespeichert.";
        return RedirectToPage();
    }
}
