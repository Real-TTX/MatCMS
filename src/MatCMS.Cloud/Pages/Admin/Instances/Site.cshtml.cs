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

    /// <summary>Everything the switcher can reach: approved instances that actually have an address.
    /// One without a URL would switch to a blank frame, which reads as a broken page rather than as
    /// "this instance never reported where it lives".</summary>
    public List<Instance> Switchable { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var item = await _db.Instances.AsNoTracking().Include(i => i.Profile)
            .FirstOrDefaultAsync(i => i.Id == id);
        if (item is null) return RedirectToPage("Index");
        Item = item;

        Switchable = (await _db.Instances.AsNoTracking()
                .Where(i => i.Status == InstanceStatus.Approved)
                .OrderBy(i => i.Name).ToListAsync())
            .Where(i => !string.IsNullOrWhiteSpace(i.PreviewUrl))
            .ToList();

        // The instance being viewed belongs in its own switcher even if it is pending or has only a
        // guessed address — otherwise the control would show a different site than the frame does.
        if (Switchable.All(i => i.Id != item.Id))
            Switchable.Insert(0, item);

        return Page();
    }
}
