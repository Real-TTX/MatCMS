using MatCMS.Data;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Pages.Admin;

public class IndexModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly MatCMS.Services.CloudState _cloud;
    public IndexModel(AppDbContext db, MatCMS.Services.CloudState cloud) { _db = db; _cloud = cloud; }

    public int PageCount { get; private set; }
    public int PostCount { get; private set; }
    public int MediaCount { get; private set; }
    public int UserCount { get; private set; }
    public int MemberCount { get; private set; }
    public int SubmissionCount { get; private set; }
    public int UnreadCount { get; private set; }
    public bool SetupComplete { get; private set; }
    public List<MatCMS.Models.FormSubmission> RecentSubmissions { get; private set; } = new();

    // Cloud connection status for the "at a glance" card.
    public bool CloudConnected => _cloud.Connected;
    public bool CloudOutOfSync => _cloud.OutOfSync;
    public DateTime? CloudLastSync => _cloud.LastSyncUtc;

    public async Task OnGetAsync()
    {
        PageCount = await _db.Pages.CountAsync();
        PostCount = await _db.Posts.CountAsync();
        MediaCount = await _db.Media.CountAsync();
        UserCount = await _db.Users.CountAsync();
        MemberCount = await _db.SiteMembers.CountAsync();
        SubmissionCount = await _db.FormSubmissions.CountAsync();
        UnreadCount = await _db.FormSubmissions.CountAsync(s => !s.IsRead);
        RecentSubmissions = await _db.FormSubmissions.AsNoTracking().Include(s => s.Form)
            .OrderByDescending(s => s.CreatedAt).Take(5).ToListAsync();
        SetupComplete = (await _db.SiteSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == MatCMS.Services.SettingKeys.SetupComplete))?.Value == "1";
    }
}
