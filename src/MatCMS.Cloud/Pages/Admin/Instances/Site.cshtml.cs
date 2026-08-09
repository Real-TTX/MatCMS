using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Pages.Admin.Instances;

/// <summary>
/// The instance's own site, embedded in the cloud shell.
/// <para>The other way — a new browser tab — stays exactly as it was and is offered next to this one.
/// Both exist because they answer different needs: opening the site in a tab is what you want when
/// you are going to WORK on it, while this view is for looking at several sites in a row without
/// losing the cloud around you. Hence the switcher in the bar: it is the whole point of staying
/// here.</para>
/// </summary>
public class SiteModel : PageModel
{
    private readonly AppDbContext _db;
    public SiteModel(AppDbContext db) => _db = db;

    public Instance Item { get; private set; } = new();

    /// <summary>
    /// Everything the switcher lists: ALL instances, not only the reachable ones.
    /// <para>Offline is exactly when an operator goes looking — leaving those out means the control
    /// is missing the entries you opened it for, and it silently misrepresents the fleet as smaller
    /// than it is. Each entry says what state it is in instead; one without an address goes to its
    /// detail page rather than to a blank frame.</para>
    /// </summary>
    public List<Instance> Switchable { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var item = await _db.Instances.AsNoTracking().Include(i => i.Profile)
            .FirstOrDefaultAsync(i => i.Id == id);
        if (item is null) return RedirectToPage("Index");
        Item = item;

        // Rejected ones stay out: they were refused, so they are not something to switch between.
        Switchable = await _db.Instances.AsNoTracking()
            .Where(i => i.Status != InstanceStatus.Rejected || i.Id == id)
            .OrderBy(i => i.Name).ToListAsync();

        return Page();
    }
}
