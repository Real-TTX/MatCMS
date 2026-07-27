using MatCMS.Data;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Pages.Admin;

public class IndexModel : PageModel
{
    private readonly AppDbContext _db;
    public IndexModel(AppDbContext db) => _db = db;

    public int PageCount { get; private set; }
    public int UserCount { get; private set; }
    public int SubmissionCount { get; private set; }
    public int UnreadCount { get; private set; }
    public bool SetupComplete { get; private set; }

    public async Task OnGetAsync()
    {
        PageCount = await _db.Pages.CountAsync();
        UserCount = await _db.Users.CountAsync();
        SubmissionCount = await _db.FormSubmissions.CountAsync();
        UnreadCount = await _db.FormSubmissions.CountAsync(s => !s.IsRead);
        SetupComplete = (await _db.SiteSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == MatCMS.Services.SettingKeys.SetupComplete))?.Value == "1";
    }
}
