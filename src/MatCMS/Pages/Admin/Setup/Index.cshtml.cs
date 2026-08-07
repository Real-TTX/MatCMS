using MatCMS.Data;
using MatCMS.Models;
using MatCMS.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using PagesIndex = MatCMS.Pages.Admin.Pages.IndexModel;

namespace MatCMS.Pages.Admin.Setup;

/// <summary>First-run setup wizard: admin account → theme → SMTP → first page. One form, applied on finish.</summary>
public class IndexModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly AuthService _auth;
    public IndexModel(AppDbContext db, AuthService auth) { _db = db; _auth = auth; }

    [BindProperty] public string AdminEmail { get; set; } = "";
    [BindProperty] public string? AdminName { get; set; }
    [BindProperty] public string? AdminPassword { get; set; }
    [BindProperty] public string ThemeName { get; set; } = "";
    [BindProperty] public Dictionary<string, string> Smtp { get; set; } = new();
    [BindProperty] public string? PageTitle { get; set; }

    public List<Template> Themes { get; private set; } = new();
    public string? Error { get; private set; }

    private async Task<User?> CurrentAdminAsync()
    {
        var idStr = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (int.TryParse(idStr, out var uid))
        {
            var u = await _db.Users.FindAsync(uid);
            if (u is not null) return u;
        }
        return await _db.Users.OrderBy(u => u.Id).FirstOrDefaultAsync();
    }

    public async Task OnGetAsync()
    {
        Themes = await _db.Templates.AsNoTracking().OrderByDescending(t => t.IsActive).ThenBy(t => t.Name).ToListAsync();
        ThemeName = Themes.FirstOrDefault(t => t.IsActive)?.Name ?? Themes.FirstOrDefault()?.Name ?? "";
        var admin = await CurrentAdminAsync();
        AdminEmail = admin?.Email ?? "";
        AdminName = admin?.DisplayName;
        foreach (var k in SettingKeys.Smtp)
            Smtp[k] = (await _db.SiteSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == k))?.Value ?? "";
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var email = (AdminEmail ?? "").Trim();
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
        {
            Error = "Bitte eine gültige Admin-E-Mail-Adresse eingeben.";
            await OnGetAsync();
            return Page();
        }

        // 1) Admin account
        var admin = await CurrentAdminAsync();
        if (admin is not null)
        {
            admin.Email = email;
            admin.Username = email; // e-mail becomes the login identity
            if (!string.IsNullOrWhiteSpace(AdminName)) admin.DisplayName = AdminName.Trim();
            if (!string.IsNullOrWhiteSpace(AdminPassword)) admin.PasswordHash = _auth.HashPassword(AdminPassword);
        }

        // 2) Theme
        if (!string.IsNullOrWhiteSpace(ThemeName))
        {
            var all = await _db.Templates.ToListAsync();
            foreach (var t in all) t.IsActive = string.Equals(t.Name, ThemeName, StringComparison.OrdinalIgnoreCase);
        }

        // 3) SMTP
        foreach (var key in SettingKeys.Smtp)
        {
            var val = Smtp.TryGetValue(key, out var v) ? (v ?? "") : "";
            var row = await _db.SiteSettings.FirstOrDefaultAsync(s => s.Key == key);
            if (row is null) _db.SiteSettings.Add(new SiteSetting { Key = key, Value = val });
            else row.Value = val;
        }

        // 4) First page (optional)
        if (!string.IsNullOrWhiteSpace(PageTitle))
        {
            var slug = PagesIndex.Slugify(PageTitle);
            if (!string.IsNullOrWhiteSpace(slug) && !await _db.Pages.AnyAsync(p => p.Slug == slug && p.Locale == Localizer.DefaultCulture))
            {
                _db.Pages.Add(new MatCMS.Models.Page
                {
                    Title = PageTitle.Trim(), Slug = slug, Locale = Localizer.DefaultCulture,
                    TranslationGroup = Guid.NewGuid().ToString("N"),
                    IsPublished = true, ShowInNav = true
                });
            }
        }

        await SetSettingAsync(SettingKeys.SetupComplete, "1");
        await _db.SaveChangesAsync();
        TempData["Flash"] = "Einrichtung abgeschlossen.";
        return RedirectToPage("/Admin/Index");
    }

    private async Task SetSettingAsync(string key, string value)
    {
        var row = await _db.SiteSettings.FirstOrDefaultAsync(s => s.Key == key);
        if (row is null) _db.SiteSettings.Add(new SiteSetting { Key = key, Value = value });
        else row.Value = value;
    }
}
