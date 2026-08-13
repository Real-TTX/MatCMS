using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using MatCMS.Cloud.Services;
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

    /// <summary>The instance's own admin, derived from the address it reports. Nothing is stored for
    /// this: a second field would only be one more thing that can disagree with the site's real URL.</summary>
    /// <summary>Scheme + host + port of the instance — what its pages report as e.origin when they
    /// message this page. Derived, not stored: it must be the origin of the address actually being
    /// framed, or the check would reject exactly the messages it is meant to let through.</summary>
    /// <summary>
    /// Whether this page may put the instance in a frame at all: it needs an address, and an https
    /// admin may not frame an http one.
    /// <para>ONE answer for the whole page. The frame asked this and the toolbar did not, so the
    /// buttons went on offering to load addresses the browser then refused — the console filled with
    /// mixed-content errors while the page looked merely broken.</para>
    /// </summary>
    public bool CanEmbed =>
        !string.IsNullOrWhiteSpace(Item.PreviewUrl)
        && !MixedContent.IsBlocked(Request.IsHttps, Item.PreviewUrl);

    public string? SiteOrigin =>
        Uri.TryCreate(Item.PreviewUrl, UriKind.Absolute, out var u)
            ? u.GetLeftPart(UriPartial.Authority)
            : null;

    public string? AdminUrl => string.IsNullOrWhiteSpace(Item.PreviewUrl)
        ? null
        : Item.PreviewUrl!.TrimEnd('/') + "/Admin";

    /// <summary>
    /// Everything the switcher lists: ALL instances, not only the reachable ones.
    /// <para>Offline is exactly when an operator goes looking — leaving those out means the control
    /// is missing the entries you opened it for, and it silently misrepresents the fleet as smaller
    /// than it is. Each entry says what state it is in instead; one without an address goes to its
    /// detail page rather than to a blank frame.</para>
    /// </summary>
    public List<Instance> Switchable { get; private set; } = [];

    /// <param name="view">Set only by the view toggle. Any other way in here leaves the
    /// remembered choice alone — otherwise opening one instance from a list would silently
    /// decide how every later one opens.</param>
    public async Task<IActionResult> OnGetAsync(int id, string? view = null)
    {
        if (view is not null) ContextSwitcher.Remember(HttpContext, view);
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
