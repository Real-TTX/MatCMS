using MatCMS.Content;
using MatCMS.Data;
using MatCMS.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using PageEntity = MatCMS.Models.Page;

namespace MatCMS.Pages;

/// <summary>Renders the admin-assigned page for a 404 / server error (Settings → Fehlerhandling),
/// or a built-in fallback message. Reached via UseStatusCodePagesWithReExecute("/_status").</summary>
public class StatusModel : PageModel
{
    private readonly AppDbContext _db;
    public StatusModel(AppDbContext db, BlockRegistry registry)
    {
        _db = db;
        Registry = registry;
    }

    public BlockRegistry Registry { get; }
    public PageEntity? CustomPage { get; private set; }
    public int Code { get; private set; } = 404;

    public async Task OnGetAsync(int? code)
    {
        Code = code is 400 or 401 or 403 or 404 or 500 ? code.Value : 404;
        var key = Code == 404 ? SettingKeys.NotFoundPage : SettingKeys.ErrorPage;
        var slug = (await _db.SiteSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key))?.Value;
        if (!string.IsNullOrWhiteSpace(slug))
        {
            CustomPage = await _db.Pages.Include(p => p.Blocks).AsNoTracking()
                .FirstOrDefaultAsync(p => p.Slug == slug.Trim() && p.Locale == Localizer.DefaultCulture && p.IsPublished);
            if (CustomPage is not null) ViewData["Title"] = CustomPage.Title;
        }
    }
}
